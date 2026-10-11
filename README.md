# omdrop-owl-windows

AirDrop between Windows and Apple devices, with an ESP32 as the Wi-Fi radio.

AirDrop runs over AWDL, Apple's Wi-Fi peer-to-peer link. On Linux,
[omdrop-owl](https://github.com/t4t5/omdrop-owl) runs AWDL in userspace with
[OWL](https://github.com/seemoo-lab/owl), on a Wi-Fi card that can do monitor
mode and inject frames. Windows Wi-Fi drivers can do neither. So this project
moves the radio onto a USB-attached ESP32, which receives in promiscuous mode
and sends raw 802.11 frames. OWL runs on Windows against it, and the AirDrop
layer is rewritten natively. Your PC's own Wi-Fi is never touched.

> **Status: early. Phase 0, the feasibility spike, is under way.** Nothing here
> does AirDrop yet. So far the ESP32-S3 captures real AWDL traffic from nearby
> iPhones and injects raw frames, with a 0.3 ms serial round trip. The one open
> question is whether it ACKs a peer's unicast while unassociated. See
> [docs/PLAN.md](docs/PLAN.md) for the full plan, and what could still stop it.

## Hardware

| Board | Bands | AWDL channels | Status |
|---|---|---|---|
| ESP32-S3 (DevKitC-1, N16R8) | 2.4 GHz | 6 | 🔬 Phase 0: RX ✅ TX ✅ ACK ⏳ |
| ESP32-C5 (Waveshare dev board) | 2.4 + 5 GHz | 6, 44, 149 | ⏳ Planned. iPhones spend far more AWDL time on 44 and 149 |

On channel 6 only, expect slow transfers: an Intel AX201 on Linux took
10–17 s for a 2.4 MB photo there.

## Layout

```
firmware/      owl-radio, the ESP-IDF firmware (promiscuous RX, raw TX, serial protocol)
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
python spike.py -p COM9 ack-test       # does the ESP32 ACK while unassociated?
python spike.py -p COM9 capture -s 60  # open AirDrop on an iPhone nearby; open awdl.pcap in Wireshark
python spike.py -p COM9 link
python spike.py -p COM9 unicast       # our unicast ACKed? (--dst another ESP32's MAC)
```

`just` runs the same steps (`just --list`).

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
  the others build on.

## License

GPL-3.0-or-later ([LICENSE](LICENSE)), as omdrop-owl and OWL are. Code ported from
omdrop-awdl, which is GPL-2.0-only, will stay in a separate program from OWL.
