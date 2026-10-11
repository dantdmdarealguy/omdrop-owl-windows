# Progress

The running checklist, so work can resume from here (docs/PLAN.md has the why).
Update and commit it after each step.

## Done
- Phase 0: ESP32-S3 spike passed (RX, TX, ACK while unassociated, 0.3 ms link).
- Phase 1 (partial): owl-radio firmware, protocol 2, UART at 3 Mbit/s.
- Phase 2: owl.exe on Windows. An iPhone answers ping over awdl0 (33/40).

## Now: Phase 4, the AirDrop receiver (`windows/omdrop`, C# .NET 8)

Protocol from OpenDrop (GPL-3.0; omdrop-awdl is GPL-2.0-only, so not ported):
mDNS `<12-hex id>._airdrop._tcp.local`, TXT `flags=137`, SRV port 8771, AAAA awdl0's
fe80::; HTTPS (self-signed RSA 2048, client certs accepted unverified); POST /Discover,
/Ask (binary plist replies with ReceiverComputerName, ReceiverModelName,
ReceiverMediaCapabilities `{"Version":1}`), /Upload (chunked, application/x-cpio,
maybe gzip; extract).

- [x] .NET 8 SDK (installed per-user to `%USERPROFILE%\.dotnet`, no admin; build with `~/.dotnet/dotnet build windows/omdrop`)
- [x] Project skeleton, finds awdl0 (name, index, fe80:: address), waits for it
- [x] Binary plist reader/writer (Plist.cs)
- [x] mDNS responder + unsolicited announcements on awdl0 (Mdns.cs; untested)
- [x] HTTPS server: /Discover, /Ask, /Upload (cpio odc/newc, gzip), safe extraction (Receiver.cs, Cpio.cs; untested)
- [x] Firewall rule (link-local only) when run elevated, else prints the netsh lines
- [ ] Local test against another interface (tools/test/receiver_test.py)
- [ ] Test: the iPhone lists the PC in its AirDrop sheet; then a photo arrives
- [ ] Sending (Phase 4b): BLE wake advert (WinRT), browse, /Discover /Ask /Upload as client

## Next
- ESP32-C5 (arrives Tuesday 2026-10-13): flash, run OWL on channel 149.
- Phase 3: Windows service + tray app; Phase 5: installer.

## How to run what exists
- Firmware: `just flash` (ESP-IDF env loaded; S3 on COM9).
- owl.exe: `tools/build-owl.ps1`, then from an admin terminal
  `windows\owl\build\owl.exe -i COM9 -c 6 -N -F -E -S pin -T -v` (set OWL_LOG_FILE for a log).
