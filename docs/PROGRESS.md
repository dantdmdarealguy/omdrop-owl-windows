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
- [x] mDNS responder + unsolicited announcements on awdl0 (Mdns.cs)
- [x] HTTPS server: /Discover, /Ask, /Upload (cpio odc/newc, gzip), safe extraction (Receiver.cs, Cpio.cs)
- [x] Firewall rule (link-local only) when run elevated, else prints the netsh lines
- [x] Local test against another interface: `tools/test/receiver_test.py` passes on the VMware
      host-only adapter (mDNS answer, /Discover, /Ask, chunked gzip cpio /Upload, traversal
      entry skipped). Found: on Windows, MulticastLoopback=false stops a socket *receiving*
      multicast sent from this machine.
- [x] Test: the iPhone lists the PC in its AirDrop sheet; then a photo arrives
  - Run 1 (2026-10-11): not listed. The phone found our service (asked for our host's AAAA
    ~20 times in 2 min) but never POSTed /Discover; OWL carried ~2,300 unicast frames out and
    ~3,700 in. Suspect: TLS handshake failing, which Kestrel logs only at Debug.
  - Run 2 (`tools/airdrop-test.ps1`, OWL_PKTLOG + omdrop -v): TCP over AWDL works and TLS 1.3
    completes, but the iPhone's HTTP/1.1 requests have **no Host header**, which Kestrel
    rejects (92 attempts). Replaced Kestrel with Http.cs (minimal, lenient HTTP/1.1 over
    SslStream); the local test now sends /Discover without Host, and passes.
  - Run 3: **listed in the AirDrop sheet**; /Discover and /Ask work ("Vaibhav's iPhone wants
    to send: IMG_2760.jpg"), but /Upload came as `application/x-dvzip` (iOS 26), which was
    refused. Added DvZip.cs (format per WinDrop: BE u32 per block, bit 31 = stored, else
    zlib; cpio inside); the local test covers it.
  - Run 4 (2026-10-11): **two photos received** from the iPhone, 214 kB each, 1.5 s and 2.9 s
    (~150 and ~73 kB/s), intact; the second saved as "IMG_2760 (1).jpg".
- [ ] Sending (Phase 4b): `omdrop send FILE [--to NAME]`
  - [x] BLE wake advert via WinRT (Ble.cs): Windows accepts Apple manufacturer data and
        reports Started (`omdrop ble`). iOS 26 shape by default, `--legacy-ble` for the old.
  - [x] mDNS browse on awdl0 (Browser.cs), HTTP client over SslStream (Http.cs), cpio writer,
        /Discover /Ask /Upload (Sender.cs, as OpenDrop's client).
  - [x] Local loop: send to our own receiver on VMnet1 (`--include-self --no-ble`): identical.
  - [ ] Live: `toolsirdrop-test.ps1 -Send photo.jpg` to the iPhone. Unknowns: does the BLE
        advert wake it; does iOS 26 take a gzipped cpio upload (else DVZip); is FileIcon needed.

## Next
- ESP32-C5 (arrives Tuesday 2026-10-13): flash, run OWL on channel 149.
- Phase 3: Windows service + tray app; Phase 5: installer.

## How to run what exists
- Firmware: `just flash` (ESP-IDF env loaded; S3 on COM9).
- owl.exe: `tools/build-owl.ps1`, then from an admin terminal
  `windows\owl\build\owl.exe -i COM9 -c 6 -N -F -E -S pin -T -v` (set OWL_LOG_FILE for a log).
