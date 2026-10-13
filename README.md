# omdrop-owl-windows

AirDrop between Windows and Apple devices, with an ESP32 as the Wi-Fi radio.

AirDrop runs over AWDL, Apple's Wi-Fi peer-to-peer link. On Linux,
[omdrop-owl](https://github.com/t4t5/omdrop-owl) runs AWDL in userspace with
[OWL](https://github.com/seemoo-lab/owl), on a Wi-Fi card that can do monitor
mode and inject frames. Windows Wi-Fi drivers can do neither. So this project
moves the radio onto a USB-attached ESP32, which receives in promiscuous mode
and sends raw 802.11 frames. OWL runs on Windows against it, and the AirDrop
layer is rewritten natively. Your PC's own Wi-Fi is never touched.

> **Status: early, but AirDrop works both ways between Windows and an iPhone.**
> With `owl.exe` and an ESP32-S3 as the radio, `omdrop.exe receive` shows up in
> the iPhone's AirDrop sheet and saves what it's sent to Downloads (a 214 kB
> photo in 1.5-3 s), and `omdrop.exe send` sends to a device you name (the same
> photo in 11.5 s). Not yet: a tray app, an installer, and the ESP32-C5 for
> channel 149 (on channel 6 the phone is reachable one slot in sixteen). See
> [docs/PLAN.md](docs/PLAN.md) for the full plan, and what could still stop it.

## Hardware

| Board | Bands | AWDL channels | Status |
|---|---|---|---|
| ESP32-S3 (DevKitC-1, N16R8) | 2.4 GHz | 6 | ✅ AirDrop with an iPhone: receive ~75-150 kB/s, send ~19 kB/s |
| ESP32-C5 (Waveshare dev board) | 2.4 + 5 GHz | 6, 44, 149 | ⏳ Planned. iPhones spend far more AWDL time on 44 and 149 |

On channel 6 only, expect slow transfers: an Intel AX201 on Linux took
10–17 s for a 2.4 MB photo there.

## Layout

```
firmware/      owl-radio, the ESP-IDF firmware (promiscuous RX, raw TX, serial protocol)
windows/owl/   OWL for Windows: the ESP32 as its radio, Wintun as awdl0, a libev shim
tools/spike/   Phase 0 host tests: capture to pcap, ACK test, link speed
patches/       patches to OWL from omdrop-owl (-F fixed radio, -S pin, -E, -R, -m)
pins           the upstream commits this builds from
docs/PLAN.md   the plan: firmware → OWL on Windows → service → native AirDrop app → installer
```

## Phase 0: try the spike

You need [ESP-IDF](https://docs.espressif.com/projects/esp-idf/) 6.0, Python 3
with `pyserial`, and the S3 connected by its **COM** (CH343/UART) port:

```powershell
cd firmware
idf.py set-target esp32s3
idf.py -p COM9 build flash
cd ..\tools\spike
python spike.py -p COM9 hello
python spike.py -p COM9 ack-test       # does the ESP32 ACK while unassociated? (turn on a phone hotspot)
python spike.py -p COM9 capture -s 60  # open AirDrop on an iPhone nearby; open awdl.pcap in Wireshark
python spike.py -p COM9 link
python spike.py -p COM9 unicast       # our unicast ACKed? (--dst another ESP32's MAC)
```

`just` runs the same steps (`just --list`).

## Phase 2: OWL on Windows

`tools/build-owl.ps1` fetches OWL at the pinned commit, applies `patches/`,
downloads Wintun (checksum-verified) and builds `windows/owl/build/owl.exe`
with MinGW GCC, CMake and Ninja. Upstream's protocol code, `core.c` and `owl.c`
build unchanged. The radio, `awdl0` and the event loop are replaced:

- `io_esp.c`: the ESP32 over serial, standing in for a monitor interface with
  injection. It also makes `awdl0` a Wintun adapter, translating between
  OWL's Ethernet frames and Wintun's IPv6 packets, and answering neighbour
  solicitations as a kernel would.
- `ev_win.c`: the part of libev OWL uses, over Windows wait handles, with
  high-resolution timers.
- `netutils_win.c`, `compat/`: the rest of what POSIX gave OWL.

```powershell
tools\build-owl.ps1
# from an administrator terminal (creating awdl0 needs it):
windows\owl\build\owl.exe -i COM9 -c 6 -N -F -E -S pin -T -v
# without awdl0, no admin needed: add -h ""
```

OWL ignores peers weaker than −65 dBm; `-f` turns that off.

## Receiving AirDrop

```powershell
~\.dotnet\dotnet build windows\omdrop -c Release   # .NET 8 SDK
tools\airdrop-test.ps1                               # owl.exe (elevated) + omdrop.exe receive
```

`omdrop.exe receive` announces this PC on awdl0 (mDNS) and accepts everything
sent in Everyone mode into Downloads. It speaks HTTP itself (Apple's sender
sends no Host header) and unpacks both cpio and iOS 26's DVZip uploads.

## Sending AirDrop

```powershell
tools\airdrop-test.ps1 -Send photo.jpg -To "Vaibhav's iPhone"
# or, with owl.exe already running:
windows\omdrop\bin\Release\omdrop.exe send photo.jpg --to "Vaibhav's iPhone"
```

`send` wakes nearby Apple devices with a Bluetooth LE AirDrop advert, finds
them over mDNS on awdl0, and sends only to the device named exactly by `--to`;
without `--to` it lists the devices that answer and you pick one. The other
person accepts on their device. It sends as an iPhone does: a TransferID
shared by /Ask and /Upload, and a chunked DVZip upload.

## Known issues

- **WCH's CH343 driver can crash Windows.** On the ESP32-S3 DevKitC-1, the
  "COM" port is a WCH CH343 USB-serial bridge (`USB\VID_1A86&PID_55D3`). Its
  vendor driver, `CH343S64.SYS` v2.1.2025.7, bugchecked one PC three times in
  a day (0xD1 DRIVER_IRQL_NOT_LESS_OR_EQUAL at `CH343S64+0x6191`), each time
  soon after the board re-enumerated (a flash, a reset into the bootloader, a
  replug). The CH343 is CDC-compatible, so Windows' own `usbser.sys` drives it
  too. Remove WCH's driver from an administrator terminal, after stopping
  whatever holds the port:
  ```powershell
  Stop-Service OmdropOwl
  pnputil /enum-drivers | Select-String -Context 1,4 CH343   # find its oemNN.inf
  pnputil /delete-driver oemNN.inf /uninstall /force
  Start-Service OmdropOwl
  ```
  From a 32-bit PowerShell, `pnputil` isn't found (32-bit programs see
  SysWOW64 as System32): use `$env:windir\Sysnative\pnputil.exe`.
  Then replug the board, or `pnputil /scan-devices`: Device Manager should
  show "USB Serial Device (COMx)", on usbser.inf. 3 Mbit/s works on it.
  The service finds the board by its USB IDs, so a new COM number needs no
  change there; flashing commands (`idf.py -p`) need the new one.
- **Stop the service before flashing:** it keeps the board's serial port open.
  `Stop-Service OmdropOwl`, flash, `Start-Service OmdropOwl`.
- The ESP32-C5 (and the S3's other, native USB port) use Espressif's own
  USB serial, `VID_303A`, on Windows' built-in driver, and avoid the CH343.

## Credits

This is a standalone project, not a fork. Its design and starting point come from:

- **[omdrop-owl](https://github.com/t4t5/omdrop-owl)** by t4t5: the Linux radio backend
  this ports. `patches/owl-*.patch` and the fixed-radio approach (exclusive mode, `-F`,
  `-S pin`) are from there.
- **[omdrop](https://github.com/brentkearney/omdrop-plugin)** and
  **[omdrop-awdl](https://github.com/brentkearney/omdrop-awdl)** by Brent Kearney: the
  AirDrop receiver, sender, mDNS and BLE tools, and the radio-backend contract.
- **[OWL](https://github.com/seemoo-lab/owl)** by Milan Stute and the Open Wireless Link
  project (SEEMOO, TU Darmstadt), via the
  [jedbillyb/owl](https://github.com/jedbillyb/owl) fork: the userspace AWDL implementation.
- **[OpenDrop](https://github.com/seemoo-lab/opendrop)**: the open AirDrop implementation
  the others build on, and the protocol `windows/omdrop` follows.
- **[WinDrop](https://github.com/UvejsGj/WinDrop)** (MIT): its reverse-engineering of
  DVZip, the upload format iOS 26 uses, which `windows/omdrop/DvZip.cs` decodes.

## License

GPL-3.0-or-later ([LICENSE](LICENSE)), as omdrop-owl and OWL are. Code ported from
omdrop-awdl, which is GPL-2.0-only, will stay in a separate program from OWL.
