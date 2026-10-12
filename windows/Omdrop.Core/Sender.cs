using System.IO.Compression;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Omdrop;

public enum SendResult { Sent, Declined, Failed }

public sealed record SenderOptions(string ComputerName, string ModelName, string? To, TimeSpan Timeout, bool Ble, bool LegacyBle,
    bool IncludeSelf = false, string? Format = null);

/// <summary>
/// Sending, as an iPhone does it: wake nearby devices over Bluetooth, browse
/// _airdrop._tcp on awdl0, POST /Discover to each for its name, then, to the
/// one the user chose, /Ask (its owner accepts) and /Upload (DVZip or gzipped
/// cpio, named by the /Ask's TransferID).
/// </summary>
public sealed class Sender(AwdlInterface iface, X509Certificate2 cert, SenderOptions opt)
{
    readonly string senderId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>
    /// Nearby devices that answer /Discover with a name, as they turn up, while
    /// the caller keeps listening: the Bluetooth wake advert runs as long as this
    /// does. Nothing reaches anyone's screen; /Discover is what any open share
    /// sheet sends.
    /// </summary>
    public async IAsyncEnumerable<(AirDropService Service, string Name)> FindAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var advert = opt.Ble ? new AirDropAdvert(opt.LegacyBle) : null;
        advert?.Start();
        using var browser = new Browser(iface, opt.IncludeSelf);
        await foreach (var service in browser.BrowseAsync(ct))
        {
            Log.Debug($"found {service.Instance} at [{service.Address}]:{service.Port}" +
                      (service.Flags is { } f ? $", flags 0x{f:x}" : ""));
            string? name;
            try
            {
                name = await DiscoverAsync(service, ct);
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException
                                           or System.Security.Authentication.AuthenticationException or InvalidDataException)
            {
                if (ct.IsCancellationRequested)
                    yield break;
                Log.Debug($"{service.Instance}: /Discover failed: {e.Message}");
                continue;
            }
            if (name != null) // null: Contacts Only, and we're not a contact
                yield return (service, name);
        }
    }

    /// <summary>The command line's send: to --to's exact name, else the one the user picks.</summary>
    public async Task<int> SendAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(opt.Timeout);
        Log.Info($"looking for {(opt.To != null ? $"\"{opt.To}\"" : "AirDrop receivers")} on {iface.Name} " +
                 $"for up to {opt.Timeout.TotalSeconds:F0} s; on an iPhone, set AirDrop to Everyone and keep it unlocked");

        // Never send to a device nobody chose: with --to, only an exact name match;
        // without it, list what answers and let the user pick.
        if (opt.To != null)
        {
            (AirDropService Service, string Name)? match = null;
            try
            {
                await foreach (var (service, name) in FindAsync(deadline.Token))
                {
                    if (name.Equals(opt.To, StringComparison.OrdinalIgnoreCase))
                    {
                        match = (service, name);
                        break;
                    }
                    Log.Info($"found \"{name}\" (not \"{opt.To}\")");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            if (match is not { } m)
            {
                Log.Error($"no device called \"{opt.To}\" answered");
                return 1;
            }
            return await SendToAsync(m.Service, m.Name, files, null, null, ct) == SendResult.Sent ? 0 : 1;
        }

        Console.WriteLine("Devices appear below as they answer. Type a number and Enter to send to it; Enter alone cancels.");
        var found = new List<(AirDropService Service, string Name)>();
        var listing = Task.Run(async () =>
        {
            try
            {
                await foreach (var d in FindAsync(deadline.Token))
                {
                    lock (found)
                    {
                        found.Add(d);
                        Console.WriteLine($"  [{found.Count}] {d.Name}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        var read = Task.Run(Console.ReadLine);
        await Task.WhenAny(read, listing);
        deadline.Cancel(); // stop browsing either way
        if (!read.IsCompleted)
        {
            Log.Error(found.Count == 0 ? "no device answered" : "timed out waiting for a choice");
            return 1;
        }
        var answer = read.Result?.Trim();
        (AirDropService Service, string Name) chosen;
        lock (found)
        {
            if (!int.TryParse(answer, out int n) || n < 1 || n > found.Count)
            {
                Log.Info("not sent");
                return 1;
            }
            chosen = found[n - 1];
        }
        return await SendToAsync(chosen.Service, chosen.Name, files, null, null, ct) == SendResult.Sent ? 0 : 1;
    }

    async Task<string?> DiscoverAsync(AirDropService service, CancellationToken ct)
    {
        using var conn = await Connect(service, ct);
        var (status, body) = conn.Post("/Discover", "application/octet-stream",
            Plist.Write(new Dictionary<string, object?>()));
        if (status != 200)
        {
            Log.Debug($"/Discover: HTTP {status}");
            return null;
        }
        if (Plist.Read(body) is not Dictionary<string, object?> reply)
            return null;
        Log.Debug($"/Discover keys: {string.Join(", ", reply.Keys)}");
        return reply.GetValueOrDefault("ReceiverComputerName") as string;
    }

    /// <summary>
    /// Offers `files` to one device the user chose, then sends them once its
    /// owner accepts. `icon` is the thumbnail their prompt shows (JPEG); progress
    /// reports archive bytes sent of the total.
    /// </summary>
    public async Task<SendResult> SendToAsync(AirDropService service, string name, IReadOnlyList<string> files,
        byte[]? icon, IProgress<(long Sent, long Total)>? progress, CancellationToken ct)
    {
        try
        {
            return await TrySendToAsync(service, name, files, icon, progress, ct);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidDataException
                                       or System.Security.Authentication.AuthenticationException or OperationCanceledException)
        {
            Log.Error($"sending to \"{name}\" failed: {e.Message}");
            return SendResult.Failed;
        }
    }

    async Task<SendResult> TrySendToAsync(AirDropService service, string name, IReadOnlyList<string> files,
        byte[]? icon, IProgress<(long Sent, long Total)>? progress, CancellationToken ct)
    {
        Log.Info($"asking \"{name}\" to accept {string.Join(", ", files.Select(Path.GetFileName))}; waiting for them to tap Accept");
        using var conn = await Connect(service, ct);
        conn.TimeoutMs = 120_000; // the other person has to accept

        // As an iPhone sends it (iOS 26). TransferID ties /Ask to /Upload: the upload
        // names it in a header, and an upload without it is dropped on sight.
        var transferId = Guid.NewGuid().ToString().ToUpperInvariant();
        long totalBytes = files.Sum(f => new FileInfo(f).Length);
        var ask = new Dictionary<string, object?>
        {
            ["TransferID"] = new Dictionary<string, object?> { ["id"] = transferId },
            ["TransferType"] = new Dictionary<string, object?> { ["files"] = new Dictionary<string, object?>() },
            ["SenderID"] = senderId,
            ["BundleID"] = "com.apple.finder",
            ["SenderComputerName"] = opt.ComputerName,
            ["SenderModelName"] = opt.ModelName,
            ["Items"] = new List<object?>(),
            ["Files"] = files.Select(f => (object?)new Dictionary<string, object?>
            {
                ["FileName"] = Path.GetFileName(f),
                ["FileType"] = Uti(f),
                ["FileSize"] = new FileInfo(f).Length,
                ["FileBomPath"] = "./" + Path.GetFileName(f),
                ["FileIsDirectory"] = false,
                ["ShouldConvertMediaFormats"] = false,
            }).ToList(),
            ["ConvertMediaFormats"] = false,
        };
        if (icon != null)
            ask["FileIcon"] = icon;
        var (status, _) = conn.Post("/Ask", "application/octet-stream", Plist.Write(ask));
        if (status != 200)
        {
            Log.Info($"\"{name}\" declined (HTTP {status})");
            return SendResult.Declined;
        }
        Log.Info($"\"{name}\" accepted; sending");

        // DVZip if the receiver says it takes it (flag 0x02, as iOS does; Apple's
        // senders then use it), else gzip, as OpenDrop. To a temporary file: its
        // length goes first.
        bool dvzip = opt.Format == "dvzip" || (opt.Format == null && service.Flags is { } fl && (fl & 0x02) != 0);
        var tmp = Path.GetTempFileName();
        try
        {
            if (dvzip)
            {
                var cpio = new MemoryStream();
                Cpio.Write(cpio, files);
                cpio.Position = 0;
                using var outFile = File.Create(tmp);
                DvZipStream.Encode(cpio, outFile);
            }
            else
            {
                using var gz = new GZipStream(File.Create(tmp), CompressionLevel.Fastest);
                Cpio.Write(gz, files);
            }
            long length = new FileInfo(tmp).Length;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var archive = new ProgressStream(File.OpenRead(tmp), length, progress);
            // On the /Ask connection, as OpenDrop does, unless the receiver closed it.
            var upload = conn;
            if (!conn.IsOpen)
            {
                Log.Debug("upload: the /Ask connection is closed; opening another");
                upload = await Connect(service, ct);
                upload.TimeoutMs = 120_000;
            }
            using var extra = upload == conn ? null : upload;
            Log.Debug($"upload: {(dvzip ? "dvzip" : "gzip")} cpio, {length} bytes");
            (status, _) = upload.Post("/Upload", dvzip ? "application/x-dvzip" : "application/x-cpio", archive, -1,
                expectContinue: true, extraHeaders: [("TransferID", transferId), ("TotalBytes", totalBytes.ToString())]);
            double s = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            if (status != 200)
            {
                Log.Error($"upload refused (HTTP {status})");
                return SendResult.Failed;
            }
            Log.Info($"sent {files.Count} file(s) to \"{name}\": {length / 1024.0:F0} kB in {s:F1} s ({length / 1024.0 / s:F0} kB/s)");
            return SendResult.Sent;
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// Reports how far through a stream reading has got.
    sealed class ProgressStream(Stream inner, long total, IProgress<(long, long)>? progress) : Stream
    {
        long done, lastReport;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            done += n;
            if (progress != null && (done - lastReport >= 32 * 1024 || n == 0))
            {
                lastReport = done;
                progress.Report((done, total));
            }
            return n;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => total;
        public override long Position { get => done; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    Task<HttpConnection> Connect(AirDropService service, CancellationToken ct) =>
        HttpConnection.OpenAsync(new IPEndPoint(service.Address, service.Port), cert, TimeSpan.FromSeconds(20), ct);

    /// The type an Apple device uses to pick an app for the file.
    public static string Uti(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "public.jpeg",
        ".png" => "public.png",
        ".heic" => "public.heic",
        ".gif" => "com.compuserve.gif",
        ".mov" => "com.apple.quicktime-movie",
        ".mp4" => "public.mpeg-4",
        ".m4a" => "com.apple.m4a-audio",
        ".mp3" => "public.mp3",
        ".pdf" => "com.adobe.pdf",
        ".txt" => "public.plain-text",
        ".zip" => "public.zip-archive",
        ".vcf" => "public.vcard",
        _ => "public.data",
    };
}
