using System.Buffers.Binary;
using System.Text;

namespace Omdrop;

/// <summary>
/// Binary property lists (bplist00), the body of every AirDrop request and
/// reply. Values map to: Dictionary&lt;string, object?&gt;, List&lt;object?&gt;,
/// string, byte[], long, double, bool, DateTime, and null.
/// </summary>
public static class Plist
{
    static readonly DateTime Epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------- reading

    public static object? Read(byte[] data)
    {
        if (data.Length < 40 || Encoding.ASCII.GetString(data, 0, 8) != "bplist00")
            throw new FormatException("not a binary plist");
        ReadOnlySpan<byte> trailer = data.AsSpan(data.Length - 32);
        int offsetSize = trailer[6], refSize = trailer[7];
        long count = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        long top = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        long tableAt = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
        if (count <= 0 || count > data.Length || tableAt + count * offsetSize > data.Length - 32)
            throw new FormatException("bad plist trailer");
        var offsets = new long[count];
        for (long i = 0; i < count; i++)
            offsets[i] = (long)ReadUInt(data, tableAt + i * offsetSize, offsetSize);
        return new Reader(data, offsets, refSize).Object(top, 0);
    }

    static ulong ReadUInt(byte[] d, long at, int size)
    {
        ulong v = 0;
        for (int i = 0; i < size; i++)
            v = v << 8 | d[at + i];
        return v;
    }

    sealed class Reader(byte[] d, long[] offsets, int refSize)
    {
        public object? Object(long index, int depth)
        {
            if (index < 0 || index >= offsets.Length || depth > 64)
                throw new FormatException("bad plist reference");
            long at = offsets[index];
            byte marker = d[at];
            int hi = marker >> 4, lo = marker & 0xf;
            switch (hi)
            {
                case 0x0:
                    return lo switch { 0x8 => false, 0x9 => true, _ => null };
                case 0x1:
                {
                    int n = 1 << lo;
                    return (long)ReadUInt(d, at + 1, n); // 1, 2 and 4 bytes unsigned; 8 signed
                }
                case 0x2:
                    return lo == 2
                        ? BinaryPrimitives.ReadSingleBigEndian(d.AsSpan((int)at + 1))
                        : BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan((int)at + 1));
                case 0x3:
                    return Epoch.AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan((int)at + 1)));
                case 0x4:
                {
                    var (len, start) = Length(at, lo);
                    return d.AsSpan((int)start, (int)len).ToArray();
                }
                case 0x5:
                {
                    var (len, start) = Length(at, lo);
                    return Encoding.ASCII.GetString(d, (int)start, (int)len);
                }
                case 0x6:
                {
                    var (len, start) = Length(at, lo);
                    return Encoding.BigEndianUnicode.GetString(d, (int)start, (int)len * 2);
                }
                case 0x8:
                    return (long)ReadUInt(d, at + 1, lo + 1); // UID
                case 0xA:
                {
                    var (len, start) = Length(at, lo);
                    var list = new List<object?>((int)len);
                    for (long i = 0; i < len; i++)
                        list.Add(Object((long)ReadUInt(d, start + i * refSize, refSize), depth + 1));
                    return list;
                }
                case 0xD:
                {
                    var (len, start) = Length(at, lo);
                    var dict = new Dictionary<string, object?>((int)len);
                    for (long i = 0; i < len; i++)
                    {
                        var key = Object((long)ReadUInt(d, start + i * refSize, refSize), depth + 1) as string
                                  ?? throw new FormatException("plist dictionary key isn't a string");
                        dict[key] = Object((long)ReadUInt(d, start + (len + i) * refSize, refSize), depth + 1);
                    }
                    return dict;
                }
            }
            throw new FormatException($"unsupported plist marker 0x{marker:x2}");
        }

        /// The count after a marker: in its low nibble, or an int object after it.
        (long len, long start) Length(long at, int lo)
        {
            if (lo != 0xf)
                return (lo, at + 1);
            byte m = d[at + 1];
            if (m >> 4 != 0x1)
                throw new FormatException("bad plist length");
            int n = 1 << (m & 0xf);
            return ((long)ReadUInt(d, at + 2, n), at + 2 + n);
        }
    }

    // ------------------------------------------------------------- writing

    public static byte[] Write(object root)
    {
        var nodes = new List<Node>();
        Flatten(root, nodes);
        int refSize = nodes.Count < 256 ? 1 : nodes.Count < 65536 ? 2 : 4;

        var body = new MemoryStream();
        body.Write("bplist00"u8);
        var offsets = new long[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            offsets[i] = body.Position;
            WriteObject(body, nodes[i], refSize);
        }

        long tableAt = body.Position;
        int offsetSize = tableAt < 256 ? 1 : tableAt < 65536 ? 2 : tableAt < 1L << 32 ? 4 : 8;
        foreach (var off in offsets)
            WriteUInt(body, (ulong)off, offsetSize);
        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)nodes.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0); // the root is object 0
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)tableAt);
        body.Write(trailer);
        return body.ToArray();
    }

    /// One object in the output, with its children's indices for collections.
    sealed record Node(object? Value, int[] Kids);

    static int Flatten(object? o, List<Node> nodes)
    {
        int index = nodes.Count;
        var kids = Array.Empty<int>();
        nodes.Add(new Node(o, kids));
        switch (o)
        {
            case IDictionary<string, object?> dict:
            {
                kids = new int[dict.Count * 2];
                int k = 0;
                foreach (var key in dict.Keys)
                    kids[k++] = Flatten(key, nodes);
                foreach (var value in dict.Values)
                    kids[k++] = Flatten(value, nodes);
                break;
            }
            case IList<object?> list:
            {
                kids = new int[list.Count];
                for (int i = 0; i < list.Count; i++)
                    kids[i] = Flatten(list[i], nodes);
                break;
            }
        }
        nodes[index] = new Node(o, kids);
        return index;
    }

    static void WriteObject(Stream s, Node node, int refSize)
    {
        object? o = node.Value;
        switch (o)
        {
            case null:
                s.WriteByte(0x00);
                break;
            case bool b:
                s.WriteByte(b ? (byte)0x09 : (byte)0x08);
                break;
            case int or long or short or byte or uint:
            {
                long v = Convert.ToInt64(o);
                if (v >= 0 && v <= 0xff) { s.WriteByte(0x10); s.WriteByte((byte)v); }
                else if (v >= 0 && v <= 0xffff) { s.WriteByte(0x11); WriteUInt(s, (ulong)v, 2); }
                else if (v >= 0 && v <= 0xffffffffL) { s.WriteByte(0x12); WriteUInt(s, (ulong)v, 4); }
                else { s.WriteByte(0x13); WriteUInt(s, (ulong)v, 8); }
                break;
            }
            case double or float:
            {
                s.WriteByte(0x23);
                Span<byte> b = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(b, Convert.ToDouble(o));
                s.Write(b);
                break;
            }
            case DateTime t:
            {
                s.WriteByte(0x33);
                Span<byte> b = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(b, (t.ToUniversalTime() - Epoch).TotalSeconds);
                s.Write(b);
                break;
            }
            case byte[] data:
                WriteHeader(s, 0x4, data.Length);
                s.Write(data);
                break;
            case string str:
                if (str.All(c => c < 0x80))
                {
                    WriteHeader(s, 0x5, str.Length);
                    s.Write(Encoding.ASCII.GetBytes(str));
                }
                else
                {
                    var b = Encoding.BigEndianUnicode.GetBytes(str);
                    WriteHeader(s, 0x6, b.Length / 2);
                    s.Write(b);
                }
                break;
            case IDictionary<string, object?> dict:
                WriteHeader(s, 0xD, dict.Count);
                foreach (var kid in node.Kids)
                    WriteUInt(s, (ulong)kid, refSize);
                break;
            case IList<object?> list:
                WriteHeader(s, 0xA, list.Count);
                foreach (var kid in node.Kids)
                    WriteUInt(s, (ulong)kid, refSize);
                break;
            default:
                throw new ArgumentException($"can't write {o.GetType().Name} to a plist");
        }
    }

    static void WriteHeader(Stream s, int type, int count)
    {
        if (count < 15)
        {
            s.WriteByte((byte)(type << 4 | count));
            return;
        }
        s.WriteByte((byte)(type << 4 | 0xf));
        if (count <= 0xff) { s.WriteByte(0x10); s.WriteByte((byte)count); }
        else if (count <= 0xffff) { s.WriteByte(0x11); WriteUInt(s, (ulong)count, 2); }
        else { s.WriteByte(0x12); WriteUInt(s, (ulong)count, 4); }
    }

    static void WriteUInt(Stream s, ulong v, int size)
    {
        for (int i = size - 1; i >= 0; i--)
            s.WriteByte((byte)(v >> (8 * i)));
    }
}
