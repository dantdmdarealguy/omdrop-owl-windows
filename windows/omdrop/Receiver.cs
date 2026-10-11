using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// </summary>
public sealed class Receiver(AwdlInterface iface, X509Certificate2 cert, ReceiverOptions opt)
{
    /// From the last /Ask, to say whose files arrived.
    string lastSender = "?";

    public async Task RunAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (opt.Verbose)
            builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Information);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AllowSynchronousIO = true; // the cpio extractor reads the body as a plain stream
            k.Limits.MaxRequestBodySize = null;
            k.Limits.MinRequestBodyDataRate = null; // AWDL on one channel is slow and bursty
            k.Listen(new IPAddress(iface.LinkLocal.GetAddressBytes(), iface.Index), opt.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = cert,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    // Senders present their own certificate; Apple doesn't verify
                    // it in Everyone mode, and neither do we.
                    ClientCertificateMode = ClientCertificateMode.AllowCertificate,
                    ClientCertificateValidation = (_, _, _) => true,
                });
            });
        });
        var app = builder.Build();

        // As Func<>s, so the returned IResult is what's sent (a bare method
        // group binds as a RequestDelegate and its result would be dropped).
        app.MapPost("/Discover", (Func<HttpContext, Task<IResult>>)Discover);
        app.MapPost("/Ask", (Func<HttpContext, Task<IResult>>)Ask);
        app.MapPost("/Upload", (Func<HttpContext, IResult>)Upload);
        app.MapMethods("/{**path}", ["GET", "HEAD"], () => Results.Text("\n"));

        Log.Info($"receiver: https://[{iface.LinkLocal}]:{opt.Port}, saving to {opt.SaveDir}");
        await app.StartAsync(ct);
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    async Task<IResult> Discover(HttpContext ctx)
    {
        var body = await ReadBody(ctx);
        var sender = Describe(body);
        Log.Info($"discover from {ctx.Connection.RemoteIpAddress}{sender}");
        return PlistResult(new Dictionary<string, object?>
        {
            ["ReceiverComputerName"] = opt.ComputerName,
            ["ReceiverModelName"] = opt.ModelName,
            ["ReceiverMediaCapabilities"] = Encoding.UTF8.GetBytes("{\"Version\":1}"),
        });
    }

    async Task<IResult> Ask(HttpContext ctx)
    {
        var body = await ReadBody(ctx);
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
            }
        }
        catch (FormatException e)
        {
            Log.Warn($"ask: unreadable plist ({e.Message})");
        }
        lastSender = from;
        Log.Info($"{from} wants to send: {(files.Length > 0 ? files : "(no file names)")}; accepted");
        return PlistResult(new Dictionary<string, object?>
        {
            ["ReceiverModelName"] = opt.ModelName,
            ["ReceiverComputerName"] = opt.ComputerName,
        });
    }

    IResult Upload(HttpContext ctx)
    {
        var type = ctx.Request.ContentType?.ToLowerInvariant() ?? "";
        if (type != "application/x-cpio")
        {
            Log.Warn($"upload: Content-Type \"{type}\", not application/x-cpio");
            return Results.StatusCode(406);
        }
        var sw = Stopwatch.StartNew();
        var counted = new CountingStream(ctx.Request.Body);
        try
        {
            var dir = Path.Combine(opt.SaveDir);
            var files = Cpio.Extract(counted, dir);
            double s = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            Log.Info($"received {files.Count} file(s) from {lastSender}: {counted.Count / 1024.0:F0} kB in {s:F1} s " +
                     $"({counted.Count / 1024.0 / s:F0} kB/s)");
            foreach (var f in files)
                Log.Info($"  {f}");
            return Results.Ok();
        }
        catch (InvalidDataException e)
        {
            Log.Error($"upload failed: {e.Message}");
            return Results.BadRequest();
        }
    }

    static async Task<byte[]> ReadBody(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

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

    static IResult PlistResult(Dictionary<string, object?> dict) =>
        Results.Bytes(Plist.Write(dict), "application/octet-stream");

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
