using System.IO.Compression;
using System.Text;

namespace Omdrop;

/// <summary>
/// The archive an AirDrop upload carries: cpio, in the old portable format
/// ("070707", what Apple's sender writes) or newc ("070701"/"070702"), maybe
/// gzip-compressed. Extracted as a stream, without a temporary copy.
/// </summary>
public static class Cpio
{
    /// <summary>Extracts every regular file and directory under `dir`; returns the files written.
    /// `onWritten` hears the running total of file bytes written.</summary>
    public static List<string> Extract(Stream input, string dir, Action<long>? onWritten = null)
    {
        long writtenTotal = 0;
        var s = MaybeGunzip(input);
        var written = new List<string>();
        Directory.CreateDirectory(dir);
        string root = Path.GetFullPath(dir);

        while (true)
        {
            var e = ReadHeader(s) ?? throw new InvalidDataException("cpio: truncated header");
            if (e.Name == "TRAILER!!!")
                break;

            var target = SafePath(root, e.Name);
            int type = e.Mode & 0xF000;
            if (target != null && type == 0x4000) // directory
            {
                Directory.CreateDirectory(target);
            }
            else if (target != null && type == 0x8000) // regular file
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                target = Unused(target);
                using (var f = File.Create(target))
                    Copy(s, f, e.Size, n =>
                    {
                        writtenTotal += n;
                        onWritten?.Invoke(writtenTotal);
                    });
                written.Add(target);
                e = e with { Size = 0 };
            }
            Skip(s, e.Size); // anything not extracted: symlinks, devices, unsafe names
            Skip(s, e.DataPad);
        }
        return written;
    }

    /// <summary>Writes `files` as an odc cpio archive, each as "./name", as Apple's sender does.</summary>
    public static void Write(Stream s, IEnumerable<string> files)
    {
        long ino = 1, mtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var path in files)
        {
            var info = new FileInfo(path);
            mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            WriteHeader(s, "./" + info.Name, 0x81A4, info.Length, ino++, mtime); // regular file, 0644
            using var f = File.OpenRead(path);
            f.CopyTo(s);
        }
        WriteHeader(s, "TRAILER!!!", 0, 0, 0, 0);
    }

    static void WriteHeader(Stream s, string name, int mode, long size, long ino, long mtime)
    {
        var n = Encoding.UTF8.GetBytes(name + "\0");
        string O(long v, int width) => Convert.ToString(v, 8).PadLeft(width, '0');
        var h = "070707" + O(0, 6) + O(ino, 6) + O(mode, 6) + O(0, 6) + O(0, 6) + O(1, 6) + O(0, 6) +
                O(mtime, 11) + O(n.Length, 6) + O(size, 11);
        s.Write(Encoding.ASCII.GetBytes(h));
        s.Write(n);
    }

    static string? LastMagic;

    record Entry(string Name, int Mode, long Size, int DataPad);

    static Entry? ReadHeader(Stream s)
    {
        var magic = ReadExactly(s, 6);
        if (magic == null)
            return null;
        string m = Encoding.ASCII.GetString(magic);
        if (LastMagic != m)
            Log.Debug($"cpio: format {m}");
        LastMagic = m;
        if (m == "070707")
        {
            // odc: octal fields; dev ino mode uid gid nlink rdev (6 each), mtime (11),
            // namesize (6), filesize (11); then the name with its NUL; no padding.
            var h = ReadExactly(s, 70) ?? throw new InvalidDataException("cpio: truncated header");
            string f = Encoding.ASCII.GetString(h);
            int mode = Convert.ToInt32(f.Substring(12, 6), 8);
            int nameSize = Convert.ToInt32(f.Substring(53, 6), 8);
            long size = Convert.ToInt64(f.Substring(59, 11), 8);
            return new Entry(ReadName(s, nameSize), mode, size, 0);
        }
        if (m == "070701" || m == "070702")
        {
            // newc: 13 hex fields of 8; header and name padded to 4, data to 4.
            var h = ReadExactly(s, 104) ?? throw new InvalidDataException("cpio: truncated header");
            string f = Encoding.ASCII.GetString(h);
            int Hex(int i) => Convert.ToInt32(f.Substring(i * 8, 8), 16);
            int mode = Hex(1), nameSize = Hex(11);
            long size = (uint)Hex(6);
            string name = ReadName(s, nameSize);
            Skip(s, (4 - (110 + nameSize) % 4) % 4);
            return new Entry(name, mode, size, (int)((4 - size % 4) % 4));
        }
        throw new InvalidDataException($"cpio: unknown format \"{m}\"");
    }

    static string ReadName(Stream s, int size)
    {
        if (size <= 0 || size > 4096)
            throw new InvalidDataException("cpio: bad name size");
        var b = ReadExactly(s, size) ?? throw new InvalidDataException("cpio: truncated name");
        return Encoding.UTF8.GetString(b, 0, size - 1); // without the NUL
    }

    /// Where an archive name lands under root, or null for names that would
    /// leave it (absolute, "..") or are empty.
    static string? SafePath(string root, string name)
    {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != ".").ToArray();
        if (parts.Length == 0 || parts.Any(p => p == ".." || p.Contains(':')))
            return null;
        var invalid = Path.GetInvalidFileNameChars();
        parts = parts.Select(p => new string(p.Select(c => invalid.Contains(c) ? '_' : c).ToArray())).ToArray();
        var full = Path.GetFullPath(Path.Combine([root, .. parts]));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// "photo.heic", else "photo (1).heic", "photo (2).heic", ...
    static string Unused(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p))
                return p;
        }
    }

    static Stream MaybeGunzip(Stream s)
    {
        var head = ReadExactly(s, 2) ?? throw new InvalidDataException("empty upload");
        var joined = new PrefixStream(head, s);
        return head[0] == 0x1f && head[1] == 0x8b ? new GZipStream(joined, CompressionMode.Decompress) : joined;
    }

    static byte[]? ReadExactly(Stream s, int n)
    {
        var b = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = s.Read(b, got, n - got);
            if (r == 0)
                return got == 0 ? null : throw new InvalidDataException("cpio: truncated");
            got += r;
        }
        return b;
    }

    static void Copy(Stream from, Stream to, long n, Action<int>? onChunk = null)
    {
        var buf = new byte[81920];
        while (n > 0)
        {
            int r = from.Read(buf, 0, (int)Math.Min(buf.Length, n));
            if (r == 0)
                throw new InvalidDataException("cpio: truncated file");
            to.Write(buf, 0, r);
            onChunk?.Invoke(r);
            n -= r;
        }
    }

    static void Skip(Stream s, long n) => Copy(s, Stream.Null, n);

    /// Bytes already read, then the rest of the stream.
    sealed class PrefixStream(byte[] prefix, Stream rest) : Stream
    {
        int pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (pos < prefix.Length)
            {
                int n = Math.Min(count, prefix.Length - pos);
                Array.Copy(prefix, pos, buffer, offset, n);
                pos += n;
                return n;
            }
            return rest.Read(buffer, offset, count);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
