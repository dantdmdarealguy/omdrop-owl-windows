using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Omdrop;

public sealed record ReceiverOptions(
    string ComputerName,
    string ModelName,
    string SaveDir,
    int Port,
    bool Verbose);

/// <summary>
/// The HTTPS side of an AirDrop receiver, on awdl0's address only.
///   POST /Discover  who we are (binary plist)
///   POST /Ask       a sender's offer; accepted, and logged
///   POST /Upload    the files: chunked application/x-cpio, maybe gzipped
/// HTTP is Http.cs's, not Kestrel's: Apple's sender sends no Host header.
/// </summary>
public sealed class Receiver(AwdlInterface iface, X509Certificate2 cert, ReceiverOptions opt)
{
    /// From the last /Ask, to say whose files arrived.
    string lastSender = "?";

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(new IPAddress(iface.LinkLocal.GetAddressBytes(), iface.Index), opt.Port);
        listener.Start();
        Log.Info($"receiver: https://[{iface.LinkLocal}]:{opt.Port}, saving to {opt.SaveDir}");
        using var stop = ct.Register(listener.Stop);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => Serve(client), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (ct.IsCancellationRequested)
        {
        }
    }

    void Serve(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint;
        var sw = Stopwatch.StartNew();
        Log.Info($"connection from {remote}");
        try
        {
            using (client)
            using (var tls = new SslStream(client.GetStream(), false))
            {
                client.ReceiveTimeout = client.SendTimeout = 120_000;
                tls.AuthenticateAsServer(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    // Senders present a certificate of their own; Apple doesn't
                    // verify it in Everyone mode, and neither do we.
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                });
                Log.Debug($"{remote}: {tls.SslProtocol}, {tls.NegotiatedCipherSuite}" +
                          (tls.RemoteCertificate is { } c ? $", sender certificate {c.Subject}" : ""));

                var input = new HttpInput(tls);
                while (HttpRequest.Read(input) is { } req)
                {
                    Log.Debug($"{remote}: {req.Method} {req.Path} | " +
                              string.Join(" | ", req.Headers.Select(h => $"{h.Key}: {h.Value}")));
                    if (req.Header("expect")?.Equals("100-continue", StringComparison.OrdinalIgnoreCase) == true)
                        HttpResponse.Continue(tls);
                    bool close = Handle(req, tls, remote);
                    if (close || req.Header("connection")?.Equals("close", StringComparison.OrdinalIgnoreCase) == true)
                        break;
                }
            }
        }
        catch (Exception e)
        {
            // Everything: this runs on its own task, where an exception would vanish.
            Log.Warn($"connection from {remote}: {e.GetType().Name}: {e.Message}" +
                     (e.InnerException is { } inner ? $" ({inner.GetType().Name}: {inner.Message})" : ""));
        }
        Log.Info($"connection from {remote} closed after {sw.Elapsed.TotalSeconds:F1} s");
    }

    /// Answers one request; true when the connection should close after it.
    bool Handle(HttpRequest req, Stream s, EndPoint? remote)
    {
        if (req.Method == "POST" && req.Path == "/Discover")
        {
            var body = ReadAll(req.Body);
            Log.Info($"discover from {remote}{Describe(body)}");
            Reply(s, new Dictionary<string, object?>
            {
                ["ReceiverComputerName"] = opt.ComputerName,
                ["ReceiverModelName"] = opt.ModelName,
                ["ReceiverMediaCapabilities"] = Encoding.UTF8.GetBytes("{\"Version\":1}"),
            });
            return false;
        }
        if (req.Method == "POST" && req.Path == "/Ask")
        {
            Ask(ReadAll(req.Body));
            Reply(s, new Dictionary<string, object?>
            {
                ["ReceiverModelName"] = opt.ModelName,
                ["ReceiverComputerName"] = opt.ComputerName,
            });
            return false;
        }
        if (req.Method == "POST" && req.Path == "/Upload")
        {
            Upload(req, s);
            return true;
        }
        if (req.Method is "GET" or "HEAD")
        {
            req.Drain();
            HttpResponse.Write(s, 200, req.Method == "GET" ? "\n"u8.ToArray() : null);
            return false;
        }
        Log.Warn($"{remote}: unexpected {req.Method} {req.Path}");
        req.Drain();
        HttpResponse.Write(s, 404, close: true);
        return true;
    }

    void Ask(byte[] body)
    {
        string from = "someone", files = "";
        try
        {
            if (Plist.Read(body) is Dictionary<string, object?> ask)
            {
                from = ask.GetValueOrDefault("SenderComputerName") as string ?? from;
                if (ask.GetValueOrDefault("Files") is List<object?> list)
                    files = string.Join(", ", list.OfType<Dictionary<string, object?>>()
                        .Select(f => f.GetValueOrDefault("FileName") as string ?? "?"));
                if (ask.GetValueOrDefault("Items") is List<object?> items && items.Count > 0)
                    files += (files.Length > 0 ? ", " : "") + string.Join(", ", items.OfType<string>());
                if (Log.Verbose)
                    foreach (var (k, v) in ask)
                        Log.Debug($"ask {k} = {Describe(k, v)}");
            }
        }
        catch (FormatException e)
        {
            Log.Warn($"ask: unreadable plist ({e.Message})");
        }
        lastSender = from;
        Log.Info($"{from} wants to send: {(files.Length > 0 ? files : "(no file names)")}; accepted");
    }

    void Upload(HttpRequest req, Stream s)
    {
        var type = req.Header("content-type")?.ToLowerInvariant() ?? "";
        // cpio (OpenDrop, older iOS; maybe gzipped), or DVZip around cpio (iOS 26).
        if (type != "application/x-cpio" && type != "application/x-dvzip")
        {
            Log.Warn($"upload: Content-Type \"{type}\", not application/x-cpio or x-dvzip");
            HttpResponse.Write(s, 406, close: true);
            return;
        }
        var sw = Stopwatch.StartNew();
        var counted = new CountingStream(req.Body);
        DvZipStream? dvzip = type == "application/x-dvzip" ? new DvZipStream(counted) : null;
        try
        {
            var files = Cpio.Extract(dvzip ?? (Stream)counted, opt.SaveDir);
            req.Drain();
            double secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            Log.Info($"received {files.Count} file(s) from {lastSender}: {counted.Count / 1024.0:F0} kB in {secs:F1} s " +
                     $"({counted.Count / 1024.0 / secs:F0} kB/s)");
            if (dvzip != null)
                Log.Debug($"dvzip: {dvzip.Blocks} block(s), {dvzip.StoredBlocks} stored");
            foreach (var f in files)
                Log.Info($"  {f}");
            HttpResponse.Write(s, 200, close: true);
        }
        catch (InvalidDataException e)
        {
            Log.Error($"upload failed after {counted.Count / 1024.0:F0} kB: {e.Message}");
            HttpResponse.Write(s, 400, close: true);
        }
    }

    static void Reply(Stream s, Dictionary<string, object?> dict) =>
        HttpResponse.Write(s, 200, Plist.Write(dict), "application/octet-stream");

    static byte[] ReadAll(Stream body)
    {
        using var ms = new MemoryStream();
        body.CopyTo(ms);
        return ms.ToArray();
    }

    /// A plist value for the log: whole when it's small and says nothing about
    /// anyone; type and size for identities, records and icons.
    static string Describe(string key, object? v) => v switch
    {
        _ when key is "SenderRecordData" or "SenderIdentityAuthTag" or "FileIcon" =>
            $"<{v?.GetType().Name} of {(v is byte[] b ? b.Length : v?.ToString()?.Length ?? 0)}>",
        byte[] b => b.Length <= 32 ? $"<{b.Length} bytes {Convert.ToHexString(b)}>" : $"<{b.Length} bytes>",
        string s => $"\"{s}\"",
        Dictionary<string, object?> d => "{" + string.Join(", ", d.Select(kv => $"{kv.Key}: {Describe(kv.Key, kv.Value)}")) + "}",
        List<object?> l => "[" + string.Join(", ", l.Select(x => Describe("", x))) + "]",
        null => "null",
        _ => $"{v} ({v.GetType().Name})",
    };

    static string Describe(byte[] body)
    {
        try
        {
            if (body.Length > 0 && Plist.Read(body) is Dictionary<string, object?> d)
                return d.ContainsKey("SenderRecordData") ? " (with a sender record)" : "";
        }
        catch (FormatException)
        {
        }
        return "";
    }

    sealed class CountingStream(Stream inner) : Stream
    {
        public long Count;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            Count += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
