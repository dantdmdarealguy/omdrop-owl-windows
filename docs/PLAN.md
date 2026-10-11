# omdrop-owl → native Windows (ESP32 as the AWDL radio)

## Context

omdrop-owl is the Linux "radio half" of omdrop. `userspace/omdrop-discoverable` (bash) sets up
a monitor interface with `iw`/`ip`/`nmcli`, runs OWL (a C userspace AWDL daemon, built by
`tools/build-owl.sh` with `patches/owl-0*.patch`), and OWL creates the `awdl0` interface.
omdrop's Python receiver, and omdrop-awdl's Python tools (`send-to-peer`, `airdrop-send.py`,
`awdl-airdrop-adv.py`, `awdl-mdns-respond.py`, `ble-airdrop-adv.py`), handle AirDrop over that
interface. Everything depends on Linux: nl80211, libpcap monitor mode with injection,
tun/tap, NetworkManager, BlueZ, pkexec, and a QML panel for Omarchy.

The goal is a native Windows build. Windows Wi-Fi drivers can't do monitor mode with frame
injection, so **the radio moves onto an ESP32 over USB**. The ESP32 runs in promiscuous
mode and sends raw frames with `esp_wifi_80211_tx`, acting as a "pcap over USB" device.
OWL runs on Windows with new platform backends, and the AirDrop layer is rewritten natively.
The PC's own Wi-Fi is never touched, so the shared/exclusive split, `nmcli`, mt76 power
management and the reconnect workarounds all go away.

### Is it feasible? Plausibly yes, but it isn't proven. Phase 0 decides.

What already fits:
- OWL already separates its platform code: `daemon/io.{c,h}` (`wlan_send`, `host_send`,
  `host_recv`), `daemon/netutils.{c,h}` (`set_channel`, `neighbor_add`, `rfc4291_addr`, …) and
  `daemon/core.c` (libev loop). The protocol code in `src/` doesn't depend on any platform.
- OWL sends **plain (non-QoS) data frames and action frames**, behind a radiotap header with
  only a rate field. Those are exactly the frame types `esp_wifi_80211_tx` allows.
- The ESP32 is a fixed radio on one channel. That's the case `owl-03` (`-F`), `owl-05` (no ACK
  wait) and `owl-06` (`-S pin`) were written for, and the AX201 already works that way in
  exclusive mode on channel 6.
- ESP32-S3 (2.4 GHz only) → **channel 6 only**. Expect AX201-class speed: README says a
  2.4 MB photo took 10–17 s. The **ESP32-C5 adds channels 44 and 149**, where iPhones spend
  far more AWDL time, so discovery and throughput should be much better there.

What isn't known yet (each is a Phase 0 test):
1. **Hardware ACKs.** Does the ESP32 ACK an iPhone's unicast to its own MAC while it's
   unassociated and in promiscuous mode? ESP-NOW suggests it does. Without ACKs the iPhone
   gives up, as the README describes.
2. **Timestamp jitter.** OWL fills the AWDL `phy_tx`/`target_tx` fields from the host clock.
   USB adds about 1–2 ms of jitter, and 16 TU slots are about 16 ms. Mitigation: the firmware
   rewrites those fields at transmit time, and the host runs on the ESP32's clock.
3. **USB throughput and latency.** The S3 has native full-speed USB, and the C5 has
   USB-Serial-JTAG.
4. **BLE wake advert.** Windows' `BluetoothLEAdvertisementPublisher` may reject Apple
   (0x004C) Continuity payloads. Fallback: advertise from the ESP32's BLE, which shares the
   radio with Wi-Fi and costs some airtime.

The Flipper Zero isn't useful here: its radios are sub-GHz and NFC, and its Wi-Fi board is a
weaker ESP32-S2.

## Repo layout (new)

```
firmware/            ESP-IDF project "owl-radio" (C, CMake; targets esp32s3, esp32c5)
windows/owl/         Windows daemon for OWL: core_win.c, io_esp.c, netutils_win.c, compat/
                     compiles upstream src/*.c from upstream/owl (pinned + patches, as today)
windows/OmdropOwl/   .NET 8 solution: Service (radio helper), App (tray UI), Core (AirDrop)
tools/build-windows.ps1, tools/spike/  (Phase 0 host scripts)
justfile             + build-firmware, flash, build-win, spike recipes
```
This is a standalone repo, not a fork: none of the original shell remains. The Linux tree
stays reachable locally as the `upstream-main` branch, for reference while porting.
`owl.exe` stays a **separate process**. OWL is GPL-3.0+, and anything derived from
omdrop-awdl is GPL-2.0-only, so the two must not be linked into one binary.

## Phase 0: feasibility spike on the ESP32-S3 (go/no-go)

A minimal ESP-IDF sketch in `firmware/` plus a Python host script in `tools/spike/` on COM:
1. **RX:** promiscuous mode on channel 6, filtered to AWDL (vendor action frames with Apple
   OUI `00:17:f2`, and BSSID `00:25:00:ff:94:73`). Stream frames to a `.pcap` with
   radiotap-style RSSI and the ESP µs timestamp, then open it in Wireshark (it has an AWDL
   dissector). Pass: PSF/MIF frames from an iPhone with AirDrop open.
2. **TX:** inject a captured AWDL action frame rewritten with the ESP's MAC. Pass: it's seen
   by the C5 acting as a sniffer once it arrives, or the iPhone reacts by sending MIFs or
   unicast to us.
3. **ACK** (`spike.py ack-test`, needs only one ESP and any 2.4 GHz AP): send unicast probe
   requests to the AP, once from the ESP's own MAC and once from a MAC nobody owns. An AP
   retransmits a probe response, with the Retry bit set, until it's ACKed. Pass: few
   retries in the first round and many in the control round, which means the ESP ACKs
   frames to its MAC while unassociated. Once an iPhone peers with OWL, recheck that its
   unicast to us is rarely retried.
4. **Link:** measure round-trip latency and frames/s over native USB vs the COM (UART) port.

Stop and re-plan if test 3 fails.

### Phase 0 results (ESP32-S3 over the CH343 UART at 3 Mbit/s): go

All four tests pass. The rest of the risk is AWDL-level: whether an iPhone syncs with
OWL's frames through the ESP's timing, which Phase 2 shows.

| Test | Result |
|---|---|
| 1. RX | ✅ Two Apple devices' AWDL PSF/MIF action frames, and their mDNS data frames on AWDL's BSSID, captured on channel 6; the pcap parses (radiotap, Apple OUI, AWDL type 8). |
| 2. TX | ✅ for raw frames: `esp_wifi_80211_tx` sends data and management frames, **from any source MAC**, and reports per-frame ACK status that is real (an AP: 39 of 40 ACKed; a MAC nobody has: 0 of 38). Whether an iPhone accepts our AWDL frames waits for OWL (Phase 2). |
| 3. ACK | ✅ **Pass, against an iPhone's Personal Hotspot.** Probe responses to the ESP's MAC: 40, with 1 retry (2%). To a MAC nobody owns: 118 for 40 probes, 79 of them retries (67%). The ESP32 ACKs unicast to its MAC while unassociated, and promiscuous mode does deliver retransmissions, so the test can see a failure. (Campus APs were useless here: they don't retransmit probe responses at all.) |
| 4. Link | Round trip 0.3 ms median, 0.4 ms p99. TX 141 frames/s (196 kB/s) with one frame in flight at a time; the serial line is the limit (1.4 kB is 4.7 ms at 3 Mbit/s). Phase 1: pipeline TX, and move to native USB. |

Fixed along the way: with the console on USB-Serial-JTAG, UART0's pins must be routed
explicitly (`uart_set_pin`), and per-byte `uart_read_bytes` cost about 10 µs per byte.

## Phase 1: ESP32 firmware `owl-radio`

- Transport: TinyUSB vendor/CDC on the S3's native USB port, USB-Serial-JTAG on the C5.
  COBS framing with CRC16.
- Commands: `HELLO` (chip, bands/channels allowed under `esp_wifi_set_country`, fw version,
  MAC), `SET_CHANNEL`, `SET_MAC`, `SET_FILTER` (AWDL BSSID, our MAC, multicast; keeps USB
  traffic down), `TX` (frame without radiotap, rate via `esp_wifi_config_80211_tx_rate`, and a
  flag "patch AWDL phy_tx/target_tx at offset N"), `RX` event (RSSI, channel, rate, µs
  timestamp), `TIME` (clock sync), `STATS`.
- One codebase for both chips (`idf.py set-target esp32s3|esp32c5`). Ship the release `.bin`
  files.

## Phase 2: OWL on Windows (`owl.exe`, C, CMake: works in CLion with MSVC or MinGW)

Built like `tools/build-owl.sh`: clone at `OWL_COMMIT` from `pins`, apply `patches/owl-0*`, then
compile upstream `src/*.c` and `daemon/owl.c` together with Windows files:
- `compat/`: replacements for `netinet/ether.h`, `net/if.h`, `endian.h`, `ether_ntoa`, and so on.
- `io_esp.c` implements `io.h`. `io_state_init(wlan="esp:auto"|"COM5")` finds the board by
  VID/PID and sends `HELLO`. `wlan_send` strips radiotap and maps its rate. A reader thread
  queues RX frames. The host side uses **Wintun**, which is L3 while OWL speaks Ethernet:
  `host_recv` adds an Ethernet header (destination MAC from OWL's neighbor table, `33:33:…`
  for multicast), and `host_send` strips it.
- `netutils_win.c`: `set_channel` and `is_channel_available` go to firmware commands and
  capabilities. `neighbor_add*` becomes an in-process table. Creating the `awdl0` adapter uses
  `rfc4291_addr` to assign an fe80:: address with DAD skipped (`CreateUnicastIpAddressEntry`,
  `IpDadStatePreferred`). It also sets MTU, no IPv4 and a high metric. `get_hostname` uses
  `GetComputerNameEx`.
- `core_win.c` replaces libev, which can't wait on Windows handles. It runs a
  `WaitForMultipleObjects` loop over the Wintun read event, the serial RX event and a
  waitable timer driving OWL's timers.
- Runs as `owl.exe -i esp:auto -c <6|44|149> -N -F -E -S pin -v`, the exclusive-mode flags
  from `omdrop-discoverable`.

## Phase 3: radio helper → `OmdropOwl` Windows service (.NET 8, runs as SYSTEM)

It keeps **the same contract** as `omdrop-discoverable`: `start <s>`, `stop`, `status --json`,
`peers`, `probe --json`, with the same JSON and exit codes 0/2/3/4/5. It's exposed over a
named pipe, plus a small `omdrop-discoverable.exe` CLI shim. This replaces pkexec: the UI
stays unprivileged. How the shell functions map:
- `station_iface`/`driver_of`/`load_profile` become ESP detection plus `HELLO` capabilities.
  `owl-profiles` turns into board profiles: s3 → {6}, c5 → {149, 44, 6}.
- `tx_channels` becomes the capabilities plus the country set on the firmware.
- `exclusive_up/down`, `nmcli`, `mt76_dir`, `reconnect` and the AP-channel checks are deleted.
- `visible()`: `owl.exe` alive, and `awdl0` has a preferred link-local address.
  `supervise()`: a service timer for window expiry, plus announcements.
- `cmd_peers`: the same "add/remove peer" log parsing, including MAC zero-padding.
- `/run/omdrop-owl` → `%ProgramData%\omdrop-owl\`, and `/etc/omdrop` knobs →
  `%ProgramData%\omdrop\`.

## Phase 4: AirDrop layer, native (.NET 8 `Core` + tray `App`)

Port each piece from omdrop's receiver and omdrop-awdl's tools:
- mDNS announcer (`awdl-airdrop-adv.py --plain`, flags 137, port 8771) and responder
  (`awdl-mdns-respond.py`): UDP 5353 scoped to `awdl0`. Keep Windows' own mDNS responder off
  that adapter.
- Receiver: TLS on `[fe80::…%awdl0]:8771` (Kestrel) serving `/Discover`, `/Ask`, `/Upload`.
  Upload bodies are gzip'd cpio archives (own extractor), saved to a chosen folder.
- Sender (`airdrop-send.py`, `send-to-peer`): the same three endpoints, with cpio packing.
- Identity (`awdl_identity.py`): self-signed cert and record data. "Everyone" mode first,
  Contacts Only later.
- BLE wake (`ble-airdrop-adv.py`): WinRT publisher, falling back to ESP32 BLE.
- Firewall: allow TCP 8771 on `awdl0` only (`INetFwPolicy2`).
- Tray app: on/off, timer, device name, save folder, peer list, send files. Later, an Explorer
  "Send to → AirDrop" entry.

## Phase 5: packaging

An Inno Setup or WiX installer with the service, `owl.exe`, `wintun.dll` (signed by WireGuard),
the tray app (single-file publish) and the firmware `.bin` files. It also includes a "flash
your ESP32" step with a bundled esptool. Firmware and app versions are checked during
`HELLO`.

## Verification

- Phase 0: Wireshark shows AWDL PSF/MIF frames from the iPhone, and few of its unicast
  frames are retries.
- Phase 2: `owl.exe -v` logs `add peer <mac>` with an iPhone's AirDrop sheet open.
  `Get-NetIPAddress -InterfaceAlias awdl0` shows a preferred fe80:: address.
  `Resolve-DnsName`/mDNS over awdl0 sees the iPhone's `_airdrop._tcp`.
- Phase 3: the contract is exercised like the README's MT7925 check. `start 60` returns 0
  within about 2 s, a second `start` returns 5, `status --json` shows `rx_proven:true`,
  `stop` tears down, and a window expires on its own.
- Phase 4: the iPhone lists the PC in its AirDrop sheet, a photo is received in each
  direction, and transfer times are recorded against the README's AX201 numbers (S3) and
  then the C5 on channel 149.
- Port `tools/test-card.sh`'s report flow into a `just test-win` recipe for the same check.
