using System.IO.Compression;

namespace Omdrop;

/// <summary>
/// Apple's DVZip, the /Upload body iOS sends as application/x-dvzip: blocks of a
/// 4-byte big-endian header, then that many bytes. Bit 31 of the header marks a
/// block stored raw; otherwise the block is a self-contained zlib stream. No
/// container header and no terminator: the body ends when the HTTP body does.
/// The decoded bytes are a cpio archive.
///
/// Format as reverse-engineered by WinDrop (github.com/UvejsGj/WinDrop, MIT),
/// against iOS 26; this is our own decoder, read as a stream so neither a block
/// nor its output is held in memory.
/// </summary>
public sealed class DvZipStream(Stream source) : Stream
{
    /// Encodes `input` as DVZip: blocks of up to 64 KiB of input, each zlib
    /// unless that doesn't make it smaller, then stored.
    public static void Encode(Stream input, Stream output, int blockSize = 64 * 1024)
    {
        var raw = new byte[blockSize];
        while (true)
        {
            int n = 0, r;
            while (n < raw.Length && (r = input.Read(raw, n, raw.Length - n)) > 0)
                n += r;
            if (n == 0)
                return;
            var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
                zs.Write(raw, 0, n);
            bool stored = z.Length >= n;
            uint header = stored ? (uint)n | Stored : (uint)z.Length;
            output.Write([(byte)(header >> 24), (byte)(header >> 16), (byte)(header >> 8), (byte)header]);
            if (stored)
                output.Write(raw, 0, n);
            else
                z.WriteTo(output);
            if (n < raw.Length)
                return;
        }
    }

    const uint Stored = 0x8000_0000;
    const int MaxBlock = 16 * 1024 * 1024; // refuse absurd lengths from a hostile peer

    Stream? block;   // the current block's bytes (bounded)
    Stream? decoded; // what reads come from: the block itself, or zlib over it
    public int Blocks { get; private set; }
    public int StoredBlocks { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (decoded == null && !NextBlock())
                return 0;
            int n = decoded!.Read(buffer, offset, count);
            if (n > 0)
                return n;
            // Done with this block: whatever zlib didn't consume is skipped.
            block!.CopyTo(Stream.Null);
            decoded.Dispose();
            decoded = block = null;
        }
    }

    bool NextBlock()
    {
        var header = new byte[4];
        int got = 0;
        while (got < 4)
        {
            int r = source.Read(header, got, 4 - got);
            if (r == 0)
                return got == 0 ? false : throw new InvalidDataException("dvzip: truncated block header");
            got += r;
        }
        uint h = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
        long length = h & ~Stored;
        if (length > MaxBlock)
            throw new InvalidDataException($"dvzip: block of {length} bytes");
        Blocks++;
        block = new Bounded(source, length);
        if ((h & Stored) != 0)
        {
            StoredBlocks++;
            decoded = block;
        }
        else
        {
            decoded = new ZLibStream(block, CompressionMode.Decompress, leaveOpen: true);
        }
        return true;
    }

    /// The next `length` bytes of a stream.
    sealed class Bounded(Stream inner, long length) : Stream
    {
        long left = length;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (left <= 0)
                return 0;
            int n = inner.Read(buffer, offset, (int)Math.Min(count, left));
            if (n == 0)
                throw new InvalidDataException("dvzip: truncated block");
            left -= n;
            return n;
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
