using System.Text;

namespace Omdrop;

/// <summary>
/// Just enough HTTP/1.1 for AirDrop, and lenient where Apple's sender needs it:
/// its requests carry no Host header, which Kestrel (rightly, per RFC 9112)
/// refuses. Request bodies: Content-Length or chunked; Expect: 100-continue.
/// </summary>
public sealed class HttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required Stream Body { get; init; }

    public string? Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant());

    /// Reads one request's head from `input`; null at a clean end of the connection.
    public static HttpRequest? Read(HttpInput input)
    {
        string? line;
        do
        {
            line = input.ReadLine();
            if (line == null)
                return null;
        } while (line.Length == 0); // tolerate stray CRLFs between requests

        var parts = line.Split(' ');
        if (parts.Length < 2)
            throw new InvalidDataException($"bad request line \"{line}\"");
        var headers = new Dictionary<string, string>();
        while ((line = input.ReadLine()) is { Length: > 0 })
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        if (line == null)
            throw new InvalidDataException("connection closed inside the request head");

        Stream body;
        if (headers.GetValueOrDefault("transfer-encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
            body = new ChunkedStream(input);
        else if (long.TryParse(headers.GetValueOrDefault("content-length"), out long length))
            body = new LengthStream(input, length);
        else
            body = new LengthStream(input, 0);
        return new HttpRequest { Method = parts[0], Path = parts[1], Headers = headers, Body = body };
    }

    /// Reads whatever of the body is left, so the next request starts in the right place.
    public void Drain() => Body.CopyTo(Stream.Null);
}

public static class HttpResponse
{
    public static void Write(Stream s, int status, byte[]? body = null, string? contentType = null, bool close = false)
    {
        var head = new StringBuilder($"HTTP/1.1 {status} {Reason(status)}\r\n");
        head.Append($"Content-Length: {body?.Length ?? 0}\r\n");
        if (contentType != null)
            head.Append($"Content-Type: {contentType}\r\n");
        if (close)
            head.Append("Connection: close\r\n");
        head.Append("\r\n");
        s.Write(Encoding.ASCII.GetBytes(head.ToString()));
        if (body != null)
            s.Write(body);
        s.Flush();
    }

    public static void Continue(Stream s)
    {
        s.Write("HTTP/1.1 100 Continue\r\n\r\n"u8);
        s.Flush();
    }

    static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        406 => "Not Acceptable",
        _ => "Status",
    };
}

/// <summary>A connection's bytes, buffered, readable as lines (the head) and as a stream (bodies).</summary>
public sealed class HttpInput(Stream inner) : Stream
{
    readonly byte[] buf = new byte[65536];
    int pos, len;

    bool Fill()
    {
        if (pos < len)
            return true;
        len = inner.Read(buf, 0, buf.Length);
        pos = 0;
        return len > 0;
    }

    /// A line without its CRLF (or LF); null at end of stream before any byte.
    public string? ReadLine()
    {
        var line = new List<byte>();
        while (true)
        {
            if (!Fill())
                return line.Count == 0 ? null : Encoding.Latin1.GetString(line.ToArray());
            byte b = buf[pos++];
            if (b == '\n')
                break;
            if (line.Count > 16384)
                throw new InvalidDataException("header line too long");
            line.Add(b);
        }
        if (line.Count > 0 && line[^1] == '\r')
            line.RemoveAt(line.Count - 1);
        return Encoding.Latin1.GetString(line.ToArray());
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!Fill())
            return 0;
        int n = Math.Min(count, len - pos);
        Array.Copy(buf, pos, buffer, offset, n);
        pos += n;
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

/// <summary>A Content-Length body.</summary>
sealed class LengthStream(HttpInput input, long length) : ReadOnlyBody
{
    long left = length;

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (left <= 0)
            return 0;
        int n = input.Read(buffer, offset, (int)Math.Min(count, left));
        if (n == 0)
            throw new InvalidDataException("connection closed inside the body");
        left -= n;
        return n;
    }
}

/// <summary>A chunked body: hex size line, data, CRLF, ... 0, trailers, empty line.</summary>
sealed class ChunkedStream(HttpInput input) : ReadOnlyBody
{
    long left;
    bool done;

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (done)
            return 0;
        if (left == 0)
        {
            var line = input.ReadLine() ?? throw new InvalidDataException("connection closed inside a chunked body");
            int semi = line.IndexOf(';');
            left = Convert.ToInt64((semi >= 0 ? line[..semi] : line).Trim(), 16);
            if (left == 0)
            {
                while (input.ReadLine() is { Length: > 0 }) { } // trailers
                done = true;
                return 0;
            }
        }
        int n = input.Read(buffer, offset, (int)Math.Min(count, left));
        if (n == 0)
            throw new InvalidDataException("connection closed inside a chunk");
        left -= n;
        if (left == 0)
            input.ReadLine(); // the CRLF after the chunk
        return n;
    }
}

abstract class ReadOnlyBody : Stream
{
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

/// <summary>
/// The client side, over one TLS connection: requests with a Content-Length
/// body, responses read whole (Content-Length, chunked, or to close). Peers
/// present no usable certificate name (link-local, self-signed), so none is
/// checked; we present ours, as Apple's senders do.
/// </summary>
public sealed class HttpConnection : IDisposable
{
    readonly System.Net.Sockets.TcpClient tcp;
    readonly System.Net.Security.SslStream tls;
    readonly HttpInput input;

    HttpConnection(System.Net.Sockets.TcpClient tcp, System.Net.Security.SslStream tls)
    {
        this.tcp = tcp;
        this.tls = tls;
        input = new HttpInput(tls);
    }

    public static async Task<HttpConnection> OpenAsync(System.Net.IPEndPoint to,
        System.Security.Cryptography.X509Certificates.X509Certificate2 cert, TimeSpan timeout, CancellationToken ct)
    {
        var tcp = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetworkV6);
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            t.CancelAfter(timeout);
            await tcp.ConnectAsync(to, t.Token);
        }
        var tls = new System.Net.Security.SslStream(tcp.GetStream(), false);
        await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
        {
            TargetHost = "airdrop",
            ClientCertificates = [cert],
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        }, ct);
        Log.Debug($"{to}: {tls.SslProtocol}, {tls.NegotiatedCipherSuite}");
        return new HttpConnection(tcp, tls);
    }

    public int TimeoutMs
    {
        set => tcp.ReceiveTimeout = tcp.SendTimeout = value;
    }

    /// POSTs `body` (`length` bytes) and returns the status and response body.
    public (int Status, byte[] Body) Post(string path, string contentType, Stream body, long length)
    {
        var head = new StringBuilder($"POST {path} HTTP/1.1\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(length).Append("\r\n");
        head.Append("Connection: keep-alive\r\nAccept: */*\r\nUser-Agent: AirDrop/1.0\r\nAccept-Language: en-us\r\n\r\n");
        tls.Write(Encoding.ASCII.GetBytes(head.ToString()));
        body.CopyTo(tls);
        tls.Flush();

        while (true)
        {
            var status = input.ReadLine() ?? throw new IOException("connection closed before a response");
            var parts = status.Split(' ');
            if (parts.Length < 2 || !int.TryParse(parts[1], out int code))
                throw new InvalidDataException($"bad status line \"{status}\"");
            var headers = new Dictionary<string, string>();
            string? line;
            while ((line = input.ReadLine()) is { Length: > 0 })
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
            if (code == 100)
                continue; // interim
            Stream rbody;
            if (headers.GetValueOrDefault("transfer-encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
                rbody = new ChunkedStream(input);
            else if (long.TryParse(headers.GetValueOrDefault("content-length"), out long n))
                rbody = new LengthStream(input, n);
            else if (headers.GetValueOrDefault("connection")?.Equals("close", StringComparison.OrdinalIgnoreCase) == true)
                rbody = input;
            else
                rbody = new LengthStream(input, 0);
            var ms = new MemoryStream();
            rbody.CopyTo(ms);
            return (code, ms.ToArray());
        }
    }

    public (int Status, byte[] Body) Post(string path, string contentType, byte[] body) =>
        Post(path, contentType, new MemoryStream(body), body.Length);

    public void Dispose()
    {
        tls.Dispose();
        tcp.Dispose();
    }
}
