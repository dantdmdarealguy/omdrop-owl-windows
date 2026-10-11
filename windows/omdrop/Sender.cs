using System.IO.Compression;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Omdrop;

public sealed record SenderOptions(string ComputerName, string ModelName, string? To, TimeSpan Timeout, bool Ble, bool LegacyBle,
    bool IncludeSelf = false);

/// <summary>
/// Sending, as OpenDrop's client does it: wake nearby devices over Bluetooth,
/// browse _airdrop._tcp on awdl0, POST /Discover to each until one answers
/// with its name, then /Ask (the person on the other end accepts) and /Upload
/// (a gzipped cpio archive).
/// </summary>
public sealed class Sender(AwdlInterface iface, X509Certificate2 cert, SenderOptions opt)
{
    readonly string senderId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    public async Task<int> SendAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        using var advert = opt.Ble ? new AirDropAdvert(opt.LegacyBle) : null;
        advert?.Start();
        using var browser = new Browser(iface, opt.IncludeSelf);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(opt.Timeout);
        Log.Info($"looking for {(opt.To != null ? $"\"{opt.To}\"" : "AirDrop receivers")} on {iface.Name} " +
                 $"for up to {opt.Timeout.TotalSeconds:F0} s; on an iPhone, set AirDrop to Everyone and keep it unlocked");

        // Never send to a device nobody chose: with --to, only an exact name match;
        // without it, list what answers and let the user pick. /Discover is the only
        // thing other devices see, and it's silent: what any open share sheet sends.
        var found = new List<(AirDropService Service, string Name)>();
        async Task Browse(Func<AirDropService, string, Task<bool>> onReceiver)
        {
            await foreach (var service in browser.BrowseAsync(deadline.Token))
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
                    Log.Debug($"{service.Instance}: /Discover failed: {e.Message}");
                    continue;
                }
                if (name != null && await onReceiver(service, name)) // null: Contacts Only, not us
                    return;
            }
        }

        if (opt.To != null)
        {
            (AirDropService Service, string Name)? match = null;
            await Browse((service, name) =>
            {
                if (!name.Equals(opt.To, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"found \"{name}\" (not \"{opt.To}\")");
                    return Task.FromResult(false);
                }
                match = (service, name);
                return Task.FromResult(true);
            });
            if (match is not { } m)
            {
                Log.Error($"no device called \"{opt.To}\" answered");
                return 1;
            }
            return await SendToAsync(m.Service, m.Name, files, ct) ? 0 : 1;
        }

        Console.WriteLine("Devices appear below as they answer. Type a number and Enter to send to it; Enter alone cancels.");
        var listing = Browse((service, name) =>
        {
            lock (found)
            {
                found.Add((service, name));
                Console.WriteLine($"  [{found.Count}] {name}");
            }
            return Task.FromResult(false);
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
        return await SendToAsync(chosen.Service, chosen.Name, files, ct) ? 0 : 1;
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

    async Task<bool> SendToAsync(AirDropService service, string name, IReadOnlyList<string> files, CancellationToken ct)
    {
        Log.Info($"asking \"{name}\" to accept {string.Join(", ", files.Select(Path.GetFileName))}; waiting for them to tap Accept");
        using var conn = await Connect(service, ct);
        conn.TimeoutMs = 120_000; // the other person has to accept

        var ask = new Dictionary<string, object?>
        {
            ["SenderComputerName"] = opt.ComputerName,
            ["BundleID"] = "com.apple.finder",
            ["SenderModelName"] = opt.ModelName,
            ["SenderID"] = senderId,
            ["ConvertMediaFormats"] = false,
            ["Files"] = files.Select(f => (object?)new Dictionary<string, object?>
            {
                ["FileName"] = Path.GetFileName(f),
                ["FileType"] = Uti(f),
                ["FileBomPath"] = "./" + Path.GetFileName(f),
                ["FileIsDirectory"] = false,
                ["ConvertMediaFormats"] = 0,
            }).ToList(),
        };
        var (status, _) = conn.Post("/Ask", "application/octet-stream", Plist.Write(ask));
        if (status != 200)
        {
            Log.Error($"\"{name}\" declined (HTTP {status})");
            return false;
        }
        Log.Info($"\"{name}\" accepted; sending");

        // The archive, gzipped, to a temporary file: its length goes first.
        var tmp = Path.GetTempFileName();
        try
        {
            using (var gz = new GZipStream(File.Create(tmp), CompressionLevel.Fastest))
                Cpio.Write(gz, files);
            long length = new FileInfo(tmp).Length;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var archive = File.OpenRead(tmp);
            (status, _) = conn.Post("/Upload", "application/x-cpio", archive, length);
            double s = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            if (status != 200)
            {
                Log.Error($"upload refused (HTTP {status})");
                return false;
            }
            Log.Info($"sent {files.Count} file(s) to \"{name}\": {length / 1024.0:F0} kB in {s:F1} s ({length / 1024.0 / s:F0} kB/s)");
            return true;
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    Task<HttpConnection> Connect(AirDropService service, CancellationToken ct) =>
        HttpConnection.OpenAsync(new IPEndPoint(service.Address, service.Port), cert, TimeSpan.FromSeconds(20), ct);

    /// The type an Apple device uses to pick an app for the file.
    static string Uti(string path) => Path.GetExtension(path).ToLowerInvariant() switch
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
