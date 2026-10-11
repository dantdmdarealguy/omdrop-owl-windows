#!/usr/bin/env python3
"""Phase 0 feasibility tests for an ESP32 running owl-radio (firmware/).

  spike.py hello                       what's on the port
  spike.py capture [-c 6] [-s 60]      AWDL frames to a pcap (Wireshark has an AWDL dissector)
  spike.py ack-test                    does the ESP32 ACK unicast while unassociated?
  spike.py unicast [--dst MAC]         are our unicast frames ACKed? (another ESP32: does it ACK?)
  spike.py link                        serial round trip and transmit rate

Protocol: firmware/main/proto.h. Needs pyserial (pip install pyserial).
"""
import argparse
import queue
import statistics
import struct
import sys
import threading
import time

import serial

MSG_HELLO, MSG_SET_CHANNEL, MSG_SET_MAC, MSG_SET_FILTER, MSG_TX, MSG_TIME, MSG_STATS = range(1, 8)
MSG_ACK, MSG_HELLO_RESP, MSG_TX_DONE, MSG_TIME_RESP, MSG_STATS_RESP = 0x80, 0x81, 0x86, 0x87, 0x88
MSG_RX, MSG_LOG = 0x90, 0x91
FILTER_AWDL, FILTER_MGMT_DATA, FILTER_ALL = range(3)
RATE_1M, RATE_6M, RATE_54M = 0x00, 0x0B, 0x0C  # wifi_phy_rate_t

AWDL_BSSID = bytes.fromhex("002500ff9473")
APPLE_OUI = bytes.fromhex("0017f2")
AWDL_SUBTYPES = {0: "PSF", 3: "MIF"}
# ESP legacy rate codes to radiotap's 500 kbit/s units.
ESP_RATE = {0: 2, 1: 4, 2: 11, 3: 22, 5: 4, 6: 11, 7: 22, 8: 96, 9: 48, 10: 24, 11: 12, 12: 108, 13: 72, 14: 36, 15: 18}
STATS_FIELDS = "rx rx_drop tx_ok tx_err tx_acked tx_noack bad_msg".split()


def crc16(data):
    crc = 0xFFFF
    for b in data:
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) if crc & 0x8000 else crc << 1
        crc &= 0xFFFF
    return crc


def cobs_encode(data):
    out, block = bytearray(), bytearray()
    for b in data:
        if b:
            block.append(b)
        if not b or len(block) == 254:
            out += bytes([len(block) + 1]) + block
            block = bytearray()
    out += bytes([len(block) + 1]) + block
    return bytes(out) + b"\0"


def cobs_decode(data):
    out, i = bytearray(), 0
    while i < len(data):
        code = data[i]
        if not code or i + code > len(data):
            raise ValueError("bad COBS")
        out += data[i + 1:i + code]
        i += code
        if code != 0xFF and i < len(data):
            out.append(0)
    return bytes(out)


def mac_str(b):
    return ":".join(f"{x:02x}" for x in b)


def freq_of(ch):
    return 2484 if ch == 14 else 2407 + 5 * ch if ch < 14 else 5000 + 5 * ch


class Radio:
    def __init__(self, port, baud=3_000_000):
        # DTR and RTS drive the DevKit's EN and IO0: hold both off so opening
        # the port doesn't reset the chip into the bootloader.
        self.ser = serial.Serial()
        self.ser.port, self.ser.baudrate, self.ser.timeout = port, baud, 0.1
        self.ser.dtr = self.ser.rts = False
        self.ser.open()
        self.replies, self.rx, self.tx_done = queue.Queue(), queue.Queue(), queue.Queue()
        self.bad = 0
        threading.Thread(target=self._reader, daemon=True).start()
        self.hello = self._hello()

    def _reader(self):
        buf = bytearray()
        while True:
            chunk = self.ser.read(self.ser.in_waiting or 1)
            for b in chunk:
                if b:
                    buf.append(b)
                    continue
                if buf:
                    self._dispatch(bytes(buf))
                buf.clear()

    def _dispatch(self, enc):
        try:
            raw = cobs_decode(enc)
        except ValueError:
            self.bad += 1
            return
        if len(raw) < 3 or crc16(raw[:-2]) != struct.unpack("<H", raw[-2:])[0]:
            self.bad += 1  # boot ROM text and line noise land here
            return
        t, body = raw[0], raw[1:-2]
        if t == MSG_RX:
            ts, rssi, ch, rate, sig_mode, mcs, cwb = struct.unpack("<IbBBBBB", body[:10])
            self.rx.put(dict(ts=ts, rssi=rssi, ch=ch, rate=rate, sig_mode=sig_mode, mcs=mcs, cwb=cwb,
                             frame=body[10:], host_t=time.time()))
        elif t == MSG_TX_DONE:
            self.tx_done.put((bool(body[0]), body[1:7]))
        elif t == MSG_LOG:
            print(f"[radio] {body.decode(errors='replace')}", file=sys.stderr)
        else:
            self.replies.put((t, body))

    def send(self, t, body=b""):
        raw = bytes([t]) + body
        self.ser.write(cobs_encode(raw + struct.pack("<H", crc16(raw))))

    def request(self, t, body=b"", expect=MSG_ACK, timeout=2.0):
        self.send(t, body)
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                rt, rbody = self.replies.get(timeout=max(0.01, deadline - time.time()))
            except queue.Empty:
                break
            if rt == expect:
                if rt == MSG_ACK:
                    req, err = struct.unpack("<Bi", rbody[:5])
                    if req != t:
                        continue
                    return err
                return rbody
        raise TimeoutError(f"no reply to message {t:#x}")

    def _hello(self):
        for _ in range(10):
            try:
                b = self.request(MSG_HELLO, expect=MSG_HELLO_RESP, timeout=0.5)
            except TimeoutError:
                continue
            return dict(proto=b[0], mac=b[1:7], target=b[7:23].rstrip(b"\0").decode(),
                        idf=b[23:55].rstrip(b"\0").decode())
        raise SystemExit(f"no owl-radio on {self.ser.port} (flashed? right port? the CH343 one at 3 Mbit/s)")

    def check(self, what, err):
        if err:
            raise SystemExit(f"{what} failed: esp_err {err:#x}")

    def channel(self, ch):
        self.check(f"set channel {ch}", self.request(MSG_SET_CHANNEL, bytes([ch])))

    def filter(self, mode):
        self.check("set filter", self.request(MSG_SET_FILTER, bytes([mode])))

    def set_mac(self, mac):
        self.check("set mac", self.request(MSG_SET_MAC, mac, timeout=5))
        self.hello = self._hello()

    def tx(self, frame, rate=RATE_6M, count=1, gap_ms=0):
        return self.request(MSG_TX, struct.pack("<BBH", rate, count, gap_ms) + frame)

    def stats(self):
        return dict(zip(STATS_FIELDS, struct.unpack("<7I", self.request(MSG_STATS, expect=MSG_STATS_RESP))))

    def time_us(self):
        return struct.unpack("<Q", self.request(MSG_TIME, expect=MSG_TIME_RESP))[0]

    def drain(self, seconds):
        """Received frames for the next `seconds`."""
        end, out = time.time() + seconds, []
        while time.time() < end:
            try:
                out.append(self.rx.get(timeout=max(0.01, end - time.time())))
            except queue.Empty:
                pass
        return out


class Pcap:
    """LINKTYPE_IEEE802_11_RADIOTAP, with the ESP's timestamp as TSFT."""

    def __init__(self, path):
        self.f = open(path, "wb")
        self.f.write(struct.pack("<IHHiIII", 0xA1B2C3D4, 2, 4, 0, 0, 65535, 127))

    def write(self, r):
        # TSFT, Flags, Rate, Channel, dBm signal, MCS. Offsets keep each field aligned.
        present = 1 << 0 | 1 << 1 | 1 << 2 | 1 << 3 | 1 << 5
        ch = r["ch"]
        rate = ESP_RATE.get(r["rate"], 0) if r["sig_mode"] == 0 else 0
        rt = struct.pack("<QBBHHb", r["ts"], 0, rate, freq_of(ch), 0x0080 if ch <= 14 else 0x0100, r["rssi"])
        if r["sig_mode"] == 1:  # HT
            present |= 1 << 19
            rt += struct.pack("<BBB", 0x07, r["cwb"], r["mcs"])
        hdr = struct.pack("<BBHI", 0, 0, 8 + len(rt), present) + rt
        pkt = hdr + r["frame"]
        t = r["host_t"]
        self.f.write(struct.pack("<IIII", int(t), int(t % 1 * 1e6), len(pkt), len(pkt)) + pkt)

    def close(self):
        self.f.close()


def is_awdl_action(f):
    return len(f) > 30 and f[0] == 0xD0 and f[24] == 127 and f[25:28] == APPLE_OUI and f[28] == 0x08


def cmd_hello(radio, args):
    h = radio.hello
    print(f"{h['target']}  mac {mac_str(h['mac'])}  proto {h['proto']}  IDF {h['idf']}")
    print(radio.stats())


def cmd_capture(radio, args):
    radio.filter({"awdl": FILTER_AWDL, "mgmt": FILTER_MGMT_DATA, "all": FILTER_ALL}[args.filter])
    radio.channel(args.channel)
    pcap = Pcap(args.out)
    print(f"capturing on channel {args.channel} for {args.seconds}s into {args.out}; "
          "open AirDrop on an iPhone nearby (share sheet, or Settings > AirDrop > Everyone)")
    peers, data, n = {}, {}, 0
    end = time.time() + args.seconds
    while time.time() < end:
        for r in radio.drain(min(1.0, end - time.time())):
            pcap.write(r)
            n += 1
            f = r["frame"]
            if is_awdl_action(f):
                p = peers.setdefault(mac_str(f[10:16]), {"PSF": 0, "MIF": 0, "other": 0, "rssi": []})
                p[AWDL_SUBTYPES.get(f[30], "other")] += 1
                p["rssi"].append(r["rssi"])
            elif len(f) >= 24 and f[0] & 0x0C == 0x08:
                key = (mac_str(f[10:16]), mac_str(f[4:10]))
                d = data.setdefault(key, [0, 0])
                d[0] += 1
                d[1] += bool(f[1] & 0x08)
        print(f"\r{n} frames, {len(peers)} AWDL peers", end="", flush=True)
    pcap.close()
    print()
    for mac, p in peers.items():
        print(f"AWDL {mac}: PSF {p['PSF']} MIF {p['MIF']} other {p['other']}, "
              f"rssi {statistics.median(p['rssi']):.0f} dBm")
    for (src, dst), (cnt, retries) in data.items():
        print(f"data {src} -> {dst}: {cnt} frames, {retries} retries")
    if not peers:
        print("no AWDL action frames: is an iPhone's AirDrop active, and is it using this channel?")
    print(radio.stats())


def ies(tagged):
    out, i = {}, 0
    while i + 2 <= len(tagged):
        t, n = tagged[i], tagged[i + 1]
        out.setdefault(t, tagged[i + 2:i + 2 + n])
        i += 2 + n
    return out


def scan(radio, channels):
    radio.filter(FILTER_MGMT_DATA)
    aps = {}
    for ch in channels:
        radio.channel(ch)
        for r in radio.drain(0.35):
            f = r["frame"]
            if len(f) > 36 and f[0] == 0x80:
                e = ies(f[36:])
                if 3 in e and e[3] and e[3][0] == ch:
                    ap = aps.setdefault(f[16:22], dict(ssid=e.get(0, b"").decode(errors="replace"), ch=ch, rssi=[]))
                    ap["rssi"].append(r["rssi"])
    return aps


def probe_request(bssid, src, ssid, seq):
    hdr = struct.pack("<BBH6s6s6sH", 0x40, 0, 0, bssid, src, bssid, seq << 4)
    body = bytes([0, len(ssid)]) + ssid + bytes.fromhex("010882848b960c121824" "320430486c60")
    return hdr + body


def probe_round(radio, ap, src, n):
    """Sends n unicast probe requests from `src`; counts the AP's responses to it and their retries."""
    while not radio.tx_done.empty():
        radio.tx_done.get()
    radio.drain(0.2)
    resp, retries, seqs, errs = 0, 0, set(), 0
    for i in range(n):
        if radio.tx(probe_request(ap, src, radio._ssid, i), rate=RATE_1M):
            errs += 1
        for r in radio.drain(0.1):
            f = r["frame"]
            if len(f) >= 24 and f[0] == 0x50 and f[4:10] == src and f[10:16] == ap:
                resp += 1
                retries += bool(f[1] & 0x08)
                seqs.add(struct.unpack("<H", f[22:24])[0] >> 4)
    acked = noack = 0
    while not radio.tx_done.empty():
        ok, _ = radio.tx_done.get()
        acked += ok
        noack += not ok
    return dict(sent=n, tx_errors=errs, responses=resp, unique=len(seqs), retries=retries,
                our_tx_acked=acked, our_tx_unacked=noack)


def cmd_ack_test(radio, args):
    """An AP retransmits a probe response, Retry bit set, until it's ACKed. Probe the
    AP from our own MAC and from one nobody owns: if the ESP32 ACKs, the first
    round's responses go out about once each and the second's are all retried."""
    if args.bssid:
        ap, ch = bytes.fromhex(args.bssid.replace(":", "")), args.channel
        radio._ssid = args.ssid.encode()
    else:
        aps = scan(radio, range(1, 14))
        if not aps:
            raise SystemExit("no access points heard on 2.4 GHz")
        for b, a in sorted(aps.items(), key=lambda kv: -statistics.median(kv[1]["rssi"])):
            print(f"  {mac_str(b)} ch{a['ch']:<3} {statistics.median(a['rssi']):>4.0f} dBm  {a['ssid']}")
        ap, info = max(aps.items(), key=lambda kv: statistics.median(kv[1]["rssi"]))
        ch, radio._ssid = info["ch"], info["ssid"].encode()
        print(f"probing {mac_str(ap)} ({info['ssid']}) on channel {ch}")
    radio.filter(FILTER_MGMT_DATA)
    radio.channel(ch)
    ours = radio.hello["mac"]
    nobody = bytes.fromhex("02") + bytes(reversed(ours[1:]))
    a = probe_round(radio, ap, ours, args.n)
    b = probe_round(radio, ap, nobody, args.n)
    print(f"from our MAC   {mac_str(ours)}: {a}")
    print(f"from no one's  {mac_str(nobody)}: {b}")

    def ratio(r):
        return r["retries"] / r["responses"] if r["responses"] else None

    ra, rb = ratio(a), ratio(b)
    if a["responses"] == 0:
        print("VERDICT: unknown. The AP didn't answer unicast probes from us; try another AP (--bssid, --ssid, -c).")
    elif b["responses"] and rb == 0:
        print("VERDICT: inconclusive. This AP doesn't retransmit probe responses even when nobody "
              "ACKs them (control round: 0 retries). Try another AP, e.g. a phone hotspot, or "
              "`unicast` from a second ESP32.")
    elif rb is None and b["tx_errors"] == b["sent"]:
        print(f"VERDICT: {'likely ACKs' if ra < 0.2 else 'probably no ACKs'} "
              f"(retry ratio {ra:.0%}); the control round couldn't send from a foreign MAC.")
    elif ra < 0.2 and (rb is None or rb > 0.5):
        print(f"VERDICT: PASS. The ESP32 ACKs unicast while unassociated (retries {ra:.0%} vs {rb if rb is None else f'{rb:.0%}'} in the control round).")
    else:
        print(f"VERDICT: FAIL or unclear. Retries {ra:.0%} from our MAC vs "
              f"{'n/a' if rb is None else f'{rb:.0%}'} in the control round.")
    print(radio.stats())


def cmd_unicast(radio, args):
    """Unicast data frames to --dst, counting how many the radio saw ACKed. A MAC
    nobody has should come back unACKed; another ESP32 running owl-radio, if
    it ACKs while unassociated, comes back ACKed."""
    dst = bytes.fromhex(args.dst.replace(":", ""))
    radio.channel(args.channel)
    while not radio.tx_done.empty():
        radio.tx_done.get()
    frame = struct.pack("<BBH6s6s6sH", 0x08, 0, 0, dst, radio.hello["mac"], AWDL_BSSID, 0) + bytes(64)
    errs = sum(bool(radio.tx(frame, rate=RATE_6M)) for _ in range(args.n))
    time.sleep(0.3)
    acked = noack = 0
    while not radio.tx_done.empty():
        ok, _ = radio.tx_done.get()
        acked += ok
        noack += not ok
    print(f"to {mac_str(dst)} on channel {args.channel}: sent {args.n}, tx errors {errs}, "
          f"ACKed {acked}, not ACKed {noack}")
    print(radio.stats())


def cmd_link(radio, args):
    rtts = []
    for _ in range(args.n):
        t0 = time.perf_counter()
        radio.time_us()
        rtts.append((time.perf_counter() - t0) * 1000)
    rtts.sort()
    print(f"round trip over {args.n}: median {statistics.median(rtts):.2f} ms, "
          f"p99 {rtts[int(len(rtts) * 0.99) - 1]:.2f} ms, max {rtts[-1]:.2f} ms")
    # Broadcast data frames at 54 Mbit/s: no ACKs and no retries, so this
    # measures how fast frames get to the air.
    frame = struct.pack("<BBH6s6s6sH", 0x08, 0, 0, b"\xff" * 6, radio.hello["mac"], AWDL_BSSID, 0) + bytes(1400)
    t0 = time.perf_counter()
    for _ in range(args.frames):
        radio.tx(frame, rate=RATE_54M)
    dt = time.perf_counter() - t0
    print(f"tx: {args.frames} x {len(frame)} B in {dt:.2f} s = {args.frames * len(frame) / dt / 1024:.0f} kB/s, "
          f"{args.frames / dt:.0f} frames/s (each waits for its ACK message)")
    print(radio.stats())


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("-p", "--port", default="COM9")
    ap.add_argument("-b", "--baud", type=int, default=3_000_000)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("hello")
    c = sub.add_parser("capture")
    c.add_argument("-c", "--channel", type=int, default=6)
    c.add_argument("-s", "--seconds", type=float, default=60)
    c.add_argument("-o", "--out", default="awdl.pcap")
    c.add_argument("-f", "--filter", choices=["awdl", "mgmt", "all"], default="awdl")
    a = sub.add_parser("ack-test")
    a.add_argument("-n", type=int, default=40)
    a.add_argument("--bssid")
    a.add_argument("--ssid", default="")
    a.add_argument("-c", "--channel", type=int, default=6)
    u = sub.add_parser("unicast")
    u.add_argument("--dst", default="02:de:ad:be:ef:01")
    u.add_argument("-n", type=int, default=40)
    u.add_argument("-c", "--channel", type=int, default=6)
    lk = sub.add_parser("link")
    lk.add_argument("-n", type=int, default=500)
    lk.add_argument("--frames", type=int, default=300)
    args = ap.parse_args()
    radio = Radio(args.port, args.baud)
    {"hello": cmd_hello, "capture": cmd_capture, "ack-test": cmd_ack_test, "unicast": cmd_unicast,
     "link": cmd_link}[args.cmd](radio, args)


if __name__ == "__main__":
    main()
