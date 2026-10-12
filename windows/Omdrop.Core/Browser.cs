using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Omdrop;

/// <summary>An AirDrop receiver found on awdl0.</summary>
public sealed record AirDropService(string Instance, string Host, int Port, IPAddress Address, int? Flags);

/// <summary>
/// Finds _airdrop._tcp services on awdl0: asks every second (an iPhone hears
/// channel 6 one slot in sixteen), and assembles PTR, SRV, TXT and AAAA from
/// whatever answers arrive. Answers come as multicast, so this shares port 5353
/// with a receiver running beside it.
/// </summary>
public sealed class Browser : IDisposable
{
    const string ServiceType = "_airdrop._tcp.local";
    const ushort TypePtr = 12, TypeTxt = 16, TypeAaaa = 28, TypeSrv = 33;
    readonly AwdlInterface iface;
    readonly Socket socket;
    readonly IPEndPoint group;

    readonly HashSet<string> instances = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (string host, int port)> srv = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> flags = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IPAddress> addresses = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);

    readonly bool includeSelf;

    public Browser(AwdlInterface iface, bool includeSelf = false)
    {
        this.iface = iface;
        this.includeSelf = includeSelf;
        group = new IPEndPoint(new IPAddress(IPAddress.Parse("ff02::fb").GetAddressBytes(), iface.Index), 5353);
        socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        socket.ExclusiveAddressUse = false;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 5353));
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(group.Address, iface.Index));
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, iface.Index);
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, true);
    }

    /// Services as they become complete (address and port known), each once.
    public async IAsyncEnumerable<AirDropService> BrowseAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var found = System.Threading.Channels.Channel.CreateUnbounded<AirDropService>();
        var ask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await socket.SendToAsync(Query(), SocketFlags.None, group, ct);
                await Task.Delay(1000, ct);
            }
        }, ct);
        var listen = Task.Run(async () =>
        {
            var buf = new byte[9000];
            EndPoint any = new IPEndPoint(IPAddress.IPv6Any, 0);
            while (!ct.IsCancellationRequested)
            {
                var r = await socket.ReceiveMessageFromAsync(buf, SocketFlags.None, any, ct);
                if (r.PacketInformation.Interface != iface.Index)
                    continue;
                try
                {
                    Absorb(buf.AsSpan(0, r.ReceivedBytes));
                }
                catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException)
                {
                    continue;
                }
                foreach (var s in Complete())
                    found.Writer.TryWrite(s);
            }
        }, ct);

        while (true)
        {
            AirDropService s;
            try
            {
                s = await found.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            yield return s;
        }
    }

    /// PTR for the service type, plus SRV/TXT/AAAA for what's still missing.
    byte[] Query()
    {
        var questions = new List<(string, ushort)> { (ServiceType, TypePtr) };
        foreach (var i in instances.Where(i => !srv.ContainsKey(i)))
            questions.Add((i, TypeSrv));
        foreach (var (host, _) in srv.Values.Where(v => !addresses.ContainsKey(v.host)))
            questions.Add((host, TypeAaaa));
        var p = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)questions.Count);
        p.Write(header);
        Span<byte> q = stackalloc byte[4];
        foreach (var (name, type) in questions)
        {
            p.Write(Dns.EncodeName(name));
            BinaryPrimitives.WriteUInt16BigEndian(q, type);
            BinaryPrimitives.WriteUInt16BigEndian(q[2..], 1);
            p.Write(q);
        }
        return p.ToArray();
    }

    void Absorb(ReadOnlySpan<byte> p)
    {
        if (p.Length < 12 || (p[2] & 0x80) == 0)
            return; // only responses
        foreach (var rr in Dns.ReadRecords(p))
        {
            switch (rr.Type)
            {
                case TypePtr when rr.Name.Equals(ServiceType, StringComparison.OrdinalIgnoreCase):
                {
                    int at = rr.DataAt;
                    instances.Add(Dns.ReadName(p, ref at));
                    break;
                }
                case TypeSrv when rr.DataLength >= 7:
                {
                    int port = BinaryPrimitives.ReadUInt16BigEndian(p[(rr.DataAt + 4)..]);
                    int at = rr.DataAt + 6;
                    srv[rr.Name] = (Dns.ReadName(p, ref at), port);
                    instances.Add(rr.Name);
                    break;
                }
                case TypeTxt:
                {
                    var txt = Encoding.ASCII.GetString(p.Slice(rr.DataAt, rr.DataLength));
                    var m = System.Text.RegularExpressions.Regex.Match(txt, @"flags=(\d+)");
                    if (m.Success)
                        flags[rr.Name] = int.Parse(m.Groups[1].Value);
                    break;
                }
                case TypeAaaa when rr.DataLength == 16:
                    addresses[rr.Name] = new IPAddress(p.Slice(rr.DataAt, 16).ToArray(), iface.Index);
                    break;
            }
        }
    }

    IEnumerable<AirDropService> Complete()
    {
        foreach (var i in instances)
        {
            if (reported.Contains(i) || !srv.TryGetValue(i, out var s) || !addresses.TryGetValue(s.host, out var addr))
                continue;
            if (!includeSelf && addr.Equals(new IPAddress(iface.LinkLocal.GetAddressBytes(), iface.Index)))
                continue; // our own receiver
            reported.Add(i);
            yield return new AirDropService(i, s.host, s.port, addr, flags.TryGetValue(i, out var f) ? f : null);
        }
    }

    public void Dispose() => socket.Dispose();
}

/// <summary>DNS message pieces the browser needs.</summary>
static class Dns
{
    public readonly record struct Record(string Name, ushort Type, int DataAt, int DataLength);

    public static List<Record> ReadRecords(ReadOnlySpan<byte> p)
    {
        int qd = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        int rrs = BinaryPrimitives.ReadUInt16BigEndian(p[6..]) + BinaryPrimitives.ReadUInt16BigEndian(p[8..]) +
                  BinaryPrimitives.ReadUInt16BigEndian(p[10..]);
        int at = 12;
        for (int i = 0; i < qd; i++)
        {
            ReadName(p, ref at);
            at += 4;
        }
        var list = new List<Record>();
        for (int i = 0; i < rrs; i++)
        {
            string name = ReadName(p, ref at);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(p[at..]);
            int len = BinaryPrimitives.ReadUInt16BigEndian(p[(at + 8)..]);
            at += 10;
            if (at + len > p.Length)
                break;
            list.Add(new Record(name, type, at, len));
            at += len;
        }
        return list;
    }

    public static string ReadName(ReadOnlySpan<byte> p, ref int at)
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
                if (!jumped)
                    at = pos + 2;
                jumped = true;
                if (++jumps > 32)
                    throw new FormatException("dns: pointer loop");
                pos = (len & 0x3F) << 8 | p[pos + 1];
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(p.Slice(pos + 1, len)));
            pos += 1 + len;
        }
        if (!jumped)
            at = pos;
        return string.Join('.', labels);
    }

    public static byte[] EncodeName(string name)
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
}
