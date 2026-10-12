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

/// <summary>A file an Apple device offers in /Ask.</summary>
public sealed record IncomingFile(string Name, string Type, long? Size);

/// <summary>One offer: who, what, and the thumbnail they sent (JPEG or JPEG 2000).</summary>
public sealed record IncomingTransfer(
    string Id,
    string SenderName,
    string? SenderModel,
    IReadOnlyList<IncomingFile> Files,
    IReadOnlyList<string> Urls,
    byte[]? Icon,
    byte[]? SenderRecordData);

/// <summary>
/// The HTTPS side of an AirDrop receiver, on awdl0's address only.
///   POST /Discover  who we are (binary plist); empty for senders AllowSender refuses
///   POST /Ask       a sender's offer: Decide says yes (200) or no (403)
///   POST /Upload    the files: chunked cpio, gzipped or DVZip; named by TransferID
/// HTTP is Http.cs's, not Kestrel's: Apple's sender sends no Host header.
/// </summary>
public sealed class Receiver(AwdlInterface iface, X509Certificate2 cert, ReceiverOptions opt)
{
    /// Whether to accept an offer; null accepts everything. Undecided after
    /// AskTimeout counts as no, as on an Apple device.
    public Func<IncomingTransfer, CancellationToken, Task<bool>>? Decide { get; set; }

    /// Whether this sender may see and send to us at all (Contacts Only), from
    /// its Apple-signed record (null: it sent none). Null allows everyone.
    public Func<byte[]?, bool>? AllowSender { get; set; }

    public TimeSpan AskTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// Bytes written so far, and the total the sender announced (if it did).
    public event Action<IncomingTransfer, long, long?>? Progress;
    public event Action<IncomingTransfer, IReadOnlyList<string>>? Completed;
    public event Action<IncomingTransfer, string>? Failed;
    /// URLs accepted (an /Ask with Items and no files; nothing is uploaded).
    public event Action<IncomingTransfer>? LinksReceived;

    /// Accepted offers awaiting their /Upload, by TransferID.
    readonly Dictionary<string, IncomingTransfer> accepted = new();
    IncomingTransfer? lastAccepted;

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
                _ = Task.Run(() => Serve(client, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (ct.IsCancellationRequested)
        {
        }
    }

    void Serve(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint;
        var sw = Stopwatch.StartNew();
        Log.Debug($"connection from {remote}");
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
                    bool close = Handle(req, tls, remote, ct);
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
        Log.Debug($"connection from {remote} closed after {sw.Elapsed.TotalSeconds:F1} s");
    }

    /// Answers one request; true when the connection should close after it.
    bool Handle(HttpRequest req, Stream s, EndPoint? remote, CancellationToken ct)
    {
        if (req.Method == "POST" && req.Path == "/Discover")
        {
            var body = ReadAll(req.Body);
            var record = Field(body, "SenderRecordData") as byte[];
            if (AllowSender != null && !AllowSender(record))
            {
                Log.Info($"discover from {remote}: not a contact; not answering with our name");
                Reply(s, new Dictionary<string, object?>());
                return false;
            }
            Log.Info($"discover from {remote}{(record != null ? " (with a sender record)" : "")}");
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
            var offer = ParseAsk(ReadAll(req.Body));
            bool yes = offer != null && (AllowSender?.Invoke(offer.SenderRecordData) ?? true) && Ask(offer, ct);
            if (!yes)
            {
                HttpResponse.Write(s, 403);
                return false;
            }
            lock (accepted)
            {
                accepted[offer!.Id] = offer;
                lastAccepted = offer;
            }
            Reply(s, new Dictionary<string, object?>
            {
                ["ReceiverModelName"] = opt.ModelName,
                ["ReceiverComputerName"] = opt.ComputerName,
            });
            if (offer!.Files.Count == 0 && offer.Urls.Count > 0)
                LinksReceived?.Invoke(offer);
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

    bool Ask(IncomingTransfer offer, CancellationToken ct)
    {
        var what = string.Join(", ", offer.Files.Select(f => f.Name).Concat(offer.Urls));
        if (Decide == null)
        {
            Log.Info($"{offer.SenderName} wants to send: {what}; accepted");
            return true;
        }
        Log.Info($"{offer.SenderName} wants to send: {what}; asking");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AskTimeout);
        bool yes;
        try
        {
            yes = Decide(offer, timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            yes = false;
        }
        Log.Info(yes ? "accepted" : "declined");
        return yes;
    }

    static IncomingTransfer? ParseAsk(byte[] body)
    {
        Dictionary<string, object?> ask;
        try
        {
            if (Plist.Read(body) is not Dictionary<string, object?> d)
                return null;
            ask = d;
        }
        catch (FormatException e)
        {
            Log.Warn($"ask: unreadable plist ({e.Message})");
            return null;
        }
        if (Log.Verbose)
            foreach (var (k, v) in ask)
                Log.Debug($"ask {k} = {Describe(k, v)}");

        string id = (ask.GetValueOrDefault("TransferID") as Dictionary<string, object?>)?.GetValueOrDefault("id") as string
                    ?? Guid.NewGuid().ToString().ToUpperInvariant();
        var files = (ask.GetValueOrDefault("Files") as List<object?> ?? [])
            .OfType<Dictionary<string, object?>>()
            .Select(f => new IncomingFile(
                f.GetValueOrDefault("FileName") as string ?? "?",
                f.GetValueOrDefault("FileType") as string ?? "public.data",
                f.GetValueOrDefault("FileSize") as long?))
            .ToList();
        var urls = (ask.GetValueOrDefault("Items") as List<object?> ?? []).OfType<string>().ToList();
        return new IncomingTransfer(
            id,
            ask.GetValueOrDefault("SenderComputerName") as string ?? "Someone",
            ask.GetValueOrDefault("SenderModelName") as string,
            files,
            urls,
            ask.GetValueOrDefault("FileIcon") as byte[],
            ask.GetValueOrDefault("SenderRecordData") as byte[]);
    }

    void Upload(HttpRequest req, Stream s)
    {
        IncomingTransfer? offer;
        lock (accepted)
        {
            var id = req.Header("transferid");
            offer = id != null && accepted.Remove(id, out var o) ? o : lastAccepted;
            if (offer != null && id == null)
                accepted.Remove(offer.Id);
            lastAccepted = null;
        }
        if (offer == null)
        {
            // Nothing accepted to go with it: refuse, rather than take files
            // nobody said yes to.
            Log.Warn("upload without an accepted /Ask; refused");
            req.Drain();
            HttpResponse.Write(s, 403, close: true);
            return;
        }

        var type = req.Header("content-type")?.ToLowerInvariant() ?? "";
        // cpio (OpenDrop, older iOS; maybe gzipped), or DVZip around cpio (iOS 26).
        if (type != "application/x-cpio" && type != "application/x-dvzip")
        {
            Log.Warn($"upload: Content-Type \"{type}\", not application/x-cpio or x-dvzip");
            HttpResponse.Write(s, 406, close: true);
            Failed?.Invoke(offer, $"unsupported upload type {type}");
            return;
        }
        long? total = long.TryParse(req.Header("totalbytes"), out long t) ? t : offer.Files.Sum(f => f.Size ?? 0) is > 0 and var sum ? sum : null;
        var sw = Stopwatch.StartNew();
        var counted = new CountingStream(req.Body);
        DvZipStream? dvzip = type == "application/x-dvzip" ? new DvZipStream(counted) : null;
        try
        {
            long lastReport = 0;
            var files = Cpio.Extract(dvzip ?? (Stream)counted, opt.SaveDir, written =>
            {
                if (written - lastReport >= 64 * 1024 || (total is { } tt && written >= tt))
                {
                    lastReport = written;
                    Progress?.Invoke(offer, written, total);
                }
            });
            req.Drain();
            double secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            Log.Info($"received {files.Count} file(s) from {offer.SenderName}: {counted.Count / 1024.0:F0} kB in {secs:F1} s " +
                     $"({counted.Count / 1024.0 / secs:F0} kB/s)");
            if (dvzip != null)
                Log.Debug($"dvzip: {dvzip.Blocks} block(s), {dvzip.StoredBlocks} stored");
            foreach (var f in files)
                Log.Info($"  {f}");
            HttpResponse.Write(s, 200, close: true);
            Completed?.Invoke(offer, files);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            Log.Error($"upload failed after {counted.Count / 1024.0:F0} kB: {e.Message}");
            try
            {
                HttpResponse.Write(s, 400, close: true);
            }
            catch (IOException)
            {
            }
            Failed?.Invoke(offer, e.Message);
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

    static object? Field(byte[] body, string key)
    {
        try
        {
            return body.Length > 0 && Plist.Read(body) is Dictionary<string, object?> d ? d.GetValueOrDefault(key) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// A plist value for the log: whole when it's small and says nothing about
    /// anyone; type and size for identities, records and icons.
    static string Describe(string key, object? v) => v switch
    {
        _ when key is "SenderRecordData" or "SenderIdentityAuthTag" or "FileIcon" =>
            $"<{v?.GetType().Name} of {(v is byte[] b ? b.Length : v?.ToString()?.Length ?? 0)}" +
            (key == "FileIcon" && v is byte[] icon && icon.Length >= 12 ? $", starts {Convert.ToHexString(icon, 0, 12)}" : "") + ">",
        byte[] b => b.Length <= 32 ? $"<{b.Length} bytes {Convert.ToHexString(b)}>" : $"<{b.Length} bytes>",
        string s => $"\"{s}\"",
        Dictionary<string, object?> d => "{" + string.Join(", ", d.Select(kv => $"{kv.Key}: {Describe(kv.Key, kv.Value)}")) + "}",
        List<object?> l => "[" + string.Join(", ", l.Select(x => Describe("", x))) + "]",
        null => "null",
        _ => $"{v} ({v.GetType().Name})",
    };

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
