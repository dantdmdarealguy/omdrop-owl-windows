#!/usr/bin/env python3
"""Exercise omdrop.exe's receiver the way an Apple sender would, on any
interface with an IPv6 link-local address (no AWDL or phone needed):

  omdrop receive --iface "<name>" --save <dir> --burst 2
  python tools/test/receiver_test.py <scope index> <receiver's fe80:: address> <save dir>

mDNS PTR query for _airdrop._tcp, then HTTPS /Discover, /Ask and a chunked,
gzipped cpio /Upload (with a path-traversal entry that must not land).
"""
import gzip
import http.client
import zlib
import os
import plistlib
import socket
import ssl
import struct
import sys
import time


def dns_name(name):
    return b"".join(bytes([len(l)]) + l.encode() for l in name.split(".")) + b"\0"


def mdns_query(scope):
    q = struct.pack(">HHHHHH", 0, 0, 1, 0, 0, 0) + dns_name("_airdrop._tcp.local") + struct.pack(">HH", 12, 1)
    s = socket.socket(socket.AF_INET6, socket.SOCK_DGRAM)
    s.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_MULTICAST_IF, scope)
    s.settimeout(3)
    s.bind(("::", 0))  # a one-shot querier: the answer comes back to this port
    s.sendto(q, ("ff02::fb", 5353, 0, scope))
    data, src = s.recvfrom(9000)
    assert data[2] & 0x80, "not a response"
    assert b"_airdrop" in data and b"flags=" in data, data
    print(f"mdns: {len(data)}-byte answer from {src[0]}, with PTR/SRV/TXT/AAAA")


def cpio_odc(entries):
    out = b""
    for name, data, mode in entries + [("TRAILER!!!", b"", 0)]:
        n = name.encode() + b"\0"
        out += b"070707" + "".join(f"{v:0{w}o}" for v, w in [
            (0, 6), (0, 6), (mode, 6), (0, 6), (0, 6), (1, 6), (0, 6), (int(time.time()), 11),
            (len(n), 6), (len(data), 11)]).encode() + n + data
    return out


def post(addr, scope, path, body, headers=None, chunked=False, host=True):
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE  # self-signed, as Apple's senders accept
    c = http.client.HTTPSConnection(f"{addr}%{scope}", 8771, context=ctx, timeout=10)
    if not host:
        # As Apple's sender does it: no Host header at all.
        c.putrequest("POST", path, skip_host=True, skip_accept_encoding=True)
        c.putheader("Content-Length", str(len(body)))
        c.endheaders(body)
    elif chunked:
        c.request("POST", path, body=iter([body[i:i + 1000] for i in range(0, len(body), 1000)]),
                  headers=headers or {}, encode_chunked=True)
    else:
        c.request("POST", path, body=body, headers=headers or {})
    r = c.getresponse()
    return r.status, r.read()


def main():
    scope, addr, save = int(sys.argv[1]), sys.argv[2], sys.argv[3]
    mdns_query(scope)

    st, body = post(addr, scope, "/Discover", plistlib.dumps({}, fmt=plistlib.FMT_BINARY), host=False)
    d = plistlib.loads(body)
    assert st == 200 and "ReceiverComputerName" in d, (st, d)
    print(f"discover (no Host header): {st} {d}")

    ask = {"SenderComputerName": "test sender", "BundleID": "com.apple.finder",
           "Files": [{"FileName": "hello.txt", "FileType": "public.plain-text", "FileIsDirectory": False}]}
    st, body = post(addr, scope, "/Ask", plistlib.dumps(ask, fmt=plistlib.FMT_BINARY))
    assert st == 200, st
    print(f"ask: {st} {plistlib.loads(body)}")

    payload = os.urandom(300_000)
    archive = gzip.compress(cpio_odc([
        ("./hello.txt", b"hello from the test\n", 0o100644),
        ("./pics", b"", 0o040755),
        ("./pics/random.bin", payload, 0o100644),
        ("../escaped.txt", b"must not be written\n", 0o100644),
    ]))
    st, _ = post(addr, scope, "/Upload", archive, {"Content-Type": "application/x-cpio"}, chunked=True)
    assert st == 200, st
    assert open(os.path.join(save, "hello.txt"), "rb").read() == b"hello from the test\n"
    assert open(os.path.join(save, "pics", "random.bin"), "rb").read() == payload
    assert not os.path.exists(os.path.join(os.path.dirname(os.path.abspath(save)), "escaped.txt"))
    print(f"upload: {st}, files intact, traversal entry skipped")

    # iOS 26's DVZip: big-endian length per block, bit 31 = stored, else a zlib stream.
    raw = cpio_odc([("./dv.bin", payload[:150_000], 0o100644)])
    dv, i, stored = b"", 0, False
    while i < len(raw):
        piece = raw[i:i + 40_000]
        block = piece if stored else zlib.compress(piece)
        dv += struct.pack(">I", len(block) | (0x80000000 if stored else 0)) + block
        i += len(piece)
        stored = not stored
    st, _ = post(addr, scope, "/Upload", dv, {"Content-Type": "application/x-dvzip"}, chunked=True)
    assert st == 200, st
    assert open(os.path.join(save, "dv.bin"), "rb").read() == payload[:150_000]
    print(f"upload (dvzip, zlib and stored blocks): {st}, file intact")
    print("PASS")


if __name__ == "__main__":
    main()
