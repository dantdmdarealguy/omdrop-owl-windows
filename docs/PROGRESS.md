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
- [x] Sending (Phase 4b): `omdrop send FILE [--to NAME]`
  - [x] BLE wake advert via WinRT (Ble.cs): Windows accepts Apple manufacturer data and
        reports Started (`omdrop ble`). iOS 26 shape by default, `--legacy-ble` for the old.
  - [x] mDNS browse on awdl0 (Browser.cs), HTTP client over SslStream (Http.cs), cpio writer,
        /Discover /Ask /Upload (Sender.cs, as OpenDrop's client).
  - [x] Local loop: send to our own receiver on VMnet1 (`--include-self --no-ble`): identical.
  - [x] Live run 1 (2026-10-11): the BLE advert works on air, and browse + /Discover found a
        stranger's Mac, which the first version then sent an /Ask (file name only, cancelled
        within a second). Fixed: `send` never picks a recipient itself. `--to` needs an exact
        name; without it, devices are listed and the user picks a number. Tested locally.
  - [x] Live run 2: with `--to "Vaibhav's iPhone"` the phone showed the prompt and accepted,
        then reset the upload: iOS closes the connection (FIN) after its /Ask reply, and we
        wrote /Upload into it. Upload now opens its own connection; failures are logged,
        not a crash.
  - [x] Live run 3: the phone answered the new upload connection with a ~240-byte HTTP
        response (an error, unread) and closed it after ~10 kB of gzip cpio. It advertises
        flags 0x1b3fb, which has DVZip (0x02). Now: DVZip upload when the receiver has that
        flag (`--format cpio|dvzip` overrides), and `Expect: 100-continue` so a refusal is
        read as a status. Both formats loop-tested.
  - [x] Live run 4: DVZip made no difference. The packet log shows the iPhone sending a TLS
        close-notify right after our /Upload *headers*, with no HTTP response, on every
        attempt. (The 266-byte record before it is a TLS 1.3 session ticket, not a reply.)
        Apple's senders and OpenDrop's receiver use Transfer-Encoding: chunked for /Upload;
        we sent a Content-Length. Now chunked, and on the /Ask connection unless it's closed.
  - [x] Live run 5: still closed on our /Upload headers. The iPhone's own /Ask (as a sender)
        has keys ours lacks: TransferID, TransferType, Items, FileIcon, SenderIdentityAuthTag.
  - [x] Capture (receive run, phone -> PC, with header/value logging). An Apple sender's
        /Ask: `TransferID = {id: <UUID>}`, `TransferType = {files: {}}`, `Items = []`, files
        with `FileSize` and `ShouldConvertMediaFormats`. Its /Upload, on the /Ask connection:
        headers `TransferID: <same UUID>`, `TotalBytes: <sum of file sizes>`,
        `SenderPseudonym`, `SenderPushToken`; chunked DVZip around odc cpio (070707).
        `send` now sends TransferID (both places), TotalBytes, TransferType, Items, FileSize
        and ShouldConvertMediaFormats; not the pseudonym or push token (Apple ID), nor
        FileIcon (unless it proves needed). Loop-tested.
  - [x] Live run 6 (2026-10-12): **sent**. The iPhone accepted, and IMG_2760.jpg (215 kB,
        DVZip) arrived in 11.5 s, ~19 kB/s. Slower than receiving (75-150 kB/s): on channel 6
        we can transmit only in the phone's one channel-6 slot in sixteen.

## Now: Phase 3 + polish (asked 2026-10-12): service, tray app, overlays, thumbnail, Contacts Only

Split: the service (SYSTEM) only runs the radio side; everything that's about the user runs
in the tray app in their session.

- [x] Projects: `windows/Omdrop.Core` (library: everything but Program.cs), `windows/omdrop`
      (CLI), `windows/OmdropService`, `windows/OmdropTray` (WPF + WinForms NotifyIcon).
- [x] Receiver: ask the user instead of auto-accepting (/Ask waits for a decision; decline =
      HTTP 403), progress and completion events, keep the sender's FileIcon.
- [x] Service `OmdropOwl` (LocalSystem): config in %ProgramData%\omdrop-owl\config.json (COM
      port or auto by USB bridge VID, channel), runs and restarts owl.exe (stops it through
      OWL_STOP_EVENT, so awdl0 goes), firewall rule for the tray exe, named pipe
      `omdrop-owl` (status / start / stop / set / firewall). Tested in the foreground
      (tools/test/pipe_status.py); `tools/install-service.ps1` installs it (admin), untested.
- [ ] Tray app: icon with state (radio, awdl0), menu (Receiving: Off / Everyone for 10
      minutes / Everyone; Send files...; Open Downloads; Settings; Quit), single instance,
      `--send FILES` forwarded to the running instance, Explorer "Send to > AirDrop".
- [ ] Overlays (WPF, dark, rounded, top-right like macOS): incoming request with thumbnail +
      Accept/Decline, receive progress, "received" with Open/Show in folder, device picker
      for sending, send progress/result.
- [ ] Thumbnail: send FileIcon for images (check what format the iPhone's own is: JPEG vs
      JPEG 2000) and show incoming FileIcons in the request overlay.
- [ ] Contacts Only, receiving: accept only senders whose Apple-signed SenderRecordData
      (PKCS#7, validated phone/email hashes) matches an imported contacts .vcf. Sending to
      Contacts-Only devices needs an Apple-issued identity of our own (only obtainable from a
      Mac signed into the Apple ID), so that part is import-only if the user has one.
- [ ] Installer (Phase 5) afterwards.

## Next
- ESP32-C5 (arrives Tuesday 2026-10-13): flash, run OWL on channel 149; expect sending to
  gain most (the phone spends most slots on 149).
- Phase 3: Windows service + tray app; Phase 5: installer.

## How to run what exists
- Firmware: `just flash` (ESP-IDF env loaded; S3 on COM9).
- owl.exe: `tools/build-owl.ps1`, then from an admin terminal
  `windows\owl\build\owl.exe -i COM9 -c 6 -N -F -E -S pin -T -v` (set OWL_LOG_FILE for a log).
