using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Omdrop;

/// <summary>
/// Our AirDrop service in mDNS on awdl0: answers queries for it, and announces
/// it unprompted. Announcing matters with one radio on one channel: an iPhone
/// visits channel 6 one slot in sixteen, so each round is a burst of copies
/// spread over a whole AWDL period (16 slots of 64 TU), one of which lands
/// while it's listening.
///
///   PTR  _airdrop._tcp.local          -> &lt;id&gt;._airdrop._tcp.local
///   SRV  &lt;id&gt;._airdrop._tcp.local    -> &lt;host&gt;.local:port
///   TXT  &lt;id&gt;._airdrop._tcp.local    flags=N
///   AAAA &lt;host&gt;.local               -> awdl0's fe80:: address
/// </summary>
public sealed class Mdns : IDisposable
{
    const int Port = 5353;
    const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeAaaa = 28, TypeSrv = 33, TypeAny = 255;
    static readonly IPAddress Group = IPAddress.Parse("ff02::fb");
    const string ServiceType = "_airdrop._tcp.local";

    readonly AwdlInterface iface;
    readonly string instance, host;
    readonly int servicePort, flags;
    readonly Socket socket;
    readonly IPEndPoint group; // ff02::fb%awdl0

    public Mdns(AwdlInterface iface, string serviceId, int servicePort, int flags)
    {
        this.iface = iface;
        this.servicePort = servicePort;
        this.flags = flags;
        instance = $"{serviceId}.{ServiceType}";
        // Not the computer's name: Windows' own mDNS responder answers for that,
        // with every interface's addresses.
        host = $"omdrop-{serviceId}.local";
        group = new IPEndPoint(new IPAddress(Group.GetAddressBytes(), iface.Index), Port);

        socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        socket.ExclusiveAddressUse = false;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, Port));
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
            new IPv6MulticastOption(Group, iface.Index));
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, iface.Index);
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
        // On Windows this governs receiving: off, the socket hears no multicast
        // sent from this machine, tests' queries included. Our own announcements
        // loop back too, as responses, which Handle ignores.
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, true);
    }

    public string Instance => instance;
    public string Host => host;

    public Task RunAsync(int burst, TimeSpan burstGap, TimeSpan interval, CancellationToken ct) =>
        Task.WhenAll(AnswerAsync(ct), AnnounceAsync(burst, burstGap, interval, ct));

    async Task AnnounceAsync(int burst, TimeSpan gap, TimeSpan interval, CancellationToken ct)
    {
        var packet = Response(answerPtr: true, answerSrv: true, answerTxt: true, answerAaaa: true);
        while (!ct.IsCancellationRequested)
        {
            for (int i = 0; i < burst && !ct.IsCancellationRequested; i++)
            {
                await socket.SendToAsync(packet, SocketFlags.None, group, ct);
                await Task.Delay(gap, ct);
            }
            await Task.Delay(interval, ct);
        }
    }

    async Task AnswerAsync(CancellationToken ct)
    {
        var buf = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.IPv6Any, 0);
        Log.Debug($"mdns: listening on {socket.LocalEndPoint}, interface {iface.Index}");
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult r;
            try
            {
                r = await socket.ReceiveMessageFromAsync(buf, SocketFlags.None, any, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // Logged, not thrown: a fault here would vanish into Task.WhenAll
                // while the announcer kept running.
                Log.Error($"mdns: receive failed: {e.GetType().Name}: {e.Message}");
                await Task.Delay(1000, ct);
                continue;
            }
            if (r.PacketInformation.Interface != iface.Index)
            {
                Log.Debug($"mdns: ignored {r.ReceivedBytes} bytes from {r.RemoteEndPoint} on interface {r.PacketInformation.Interface}");
                continue; // another interface's mDNS
            }
            try
            {
                Handle(buf.AsSpan(0, r.ReceivedBytes), (IPEndPoint)r.RemoteEndPoint);
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException)
            {
                Log.Debug($"mdns: malformed packet from {r.RemoteEndPoint}");
            }
        }
    }

    void Handle(ReadOnlySpan<byte> p, IPEndPoint from)
    {
        if (p.Length < 12)
            return;
        ushort flagsWord = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
        if ((flagsWord & 0x8000) != 0)
            return; // a response, not a query
        int qd = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        int at = 12;
        bool ptr = false, srv = false, txt = false, aaaa = false, unicast = false;
        for (int i = 0; i < qd; i++)
        {
            string name = ReadName(p, ref at).ToLowerInvariant();
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(p[at..]);
            ushort cls = BinaryPrimitives.ReadUInt16BigEndian(p[(at + 2)..]);
            at += 4;
            bool any = type == TypeAny;
            if (name == ServiceType.ToLowerInvariant() && (type == TypePtr || any))
                ptr = true;
            else if (name == instance.ToLowerInvariant())
            {
                srv |= type == TypeSrv || any;
                txt |= type == TypeTxt || any;
            }
            else if (name == host.ToLowerInvariant() && (type == TypeAaaa || type == TypeA || any))
                aaaa = true;
            else
                continue;
            unicast |= (cls & 0x8000) != 0; // QU: the asker wants a unicast reply
        }
        if (!(ptr || srv || txt || aaaa))
            return;
        Log.Debug($"mdns: query from {from.Address} for{(ptr ? " PTR" : "")}{(srv ? " SRV" : "")}{(txt ? " TXT" : "")}{(aaaa ? " AAAA" : "")}");
        var reply = Response(ptr, srv || ptr, txt || ptr, aaaa || srv || ptr);
        socket.SendTo(reply, group);
        if (unicast || from.Port != Port)
            socket.SendTo(reply, from); // legacy one-shot queriers listen on their own port
    }

    byte[] Response(bool answerPtr, bool answerSrv, bool answerTxt, bool answerAaaa)
    {
        var records = new List<byte[]>();
        if (answerPtr)
            records.Add(Record(ServiceType, TypePtr, cacheFlush: false, 4500, Name(instance)));
        if (answerSrv)
        {
            var rd = new byte[6].Concat(Name(host)).ToArray();
            BinaryPrimitives.WriteUInt16BigEndian(rd.AsSpan(4), (ushort)servicePort); // priority 0, weight 0
            records.Add(Record(instance, TypeSrv, cacheFlush: true, 120, rd));
        }
        if (answerTxt)
        {
            var entry = Encoding.ASCII.GetBytes($"flags={flags}");
            records.Add(Record(instance, TypeTxt, cacheFlush: true, 4500, [(byte)entry.Length, .. entry]));
        }
        if (answerAaaa)
            records.Add(Record(host, TypeAaaa, cacheFlush: true, 120, iface.LinkLocal.GetAddressBytes()));

        var packet = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x8400); // response, authoritative
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)records.Count);
        packet.Write(header);
        foreach (var r in records)
            packet.Write(r);
        return packet.ToArray();
    }

    static byte[] Record(string name, ushort type, bool cacheFlush, uint ttl, byte[] rdata)
    {
        var n = Name(name);
        var r = new byte[n.Length + 10 + rdata.Length];
        n.CopyTo(r, 0);
        var s = r.AsSpan(n.Length);
        BinaryPrimitives.WriteUInt16BigEndian(s, type);
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)(cacheFlush ? 0x8001 : 0x0001));
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], ttl);
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], (ushort)rdata.Length);
        rdata.CopyTo(r, n.Length + 10);
        return r;
    }

    static byte[] Name(string name)
    {
        var b = new List<byte>();
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var l = Encoding.UTF8.GetBytes(label);
            b.Add((byte)l.Length);
            b.AddRange(l);
        }
        b.Add(0);
        return b.ToArray();
    }

    /// A name at `at`, following compression pointers; `at` ends past it.
    static string ReadName(ReadOnlySpan<byte> p, ref int at)
    {
        var labels = new List<string>();
        int pos = at, jumps = 0;
        bool jumped = false;
        while (true)
        {
            byte len = p[pos];
            if (len == 0)
            {
                pos++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                int target = (len & 0x3F) << 8 | p[pos + 1];
                if (!jumped)
                    at = pos + 2;
                jumped = true;
                if (++jumps > 32)
                    throw new FormatException("mdns: pointer loop");
                pos = target;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(p.Slice(pos + 1, len)));
            pos += 1 + len;
        }
        if (!jumped)
            at = pos;
        return string.Join('.', labels);
    }

    public void Dispose() => socket.Dispose();
}
