using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Omdrop;

/// <summary>What the service says about the radio.</summary>
public sealed record RadioStatus(
    string State,          // "stopped", "starting", "running", "no-radio", "error"
    string? Port,          // the ESP32's serial port, once found
    int Channel,
    bool Awdl0,            // awdl0 is up
    string? Error,
    bool Enabled);

/// <summary>A request to the service, one JSON line each way.</summary>
public sealed record RadioRequest(string Cmd, string? Port = null, int? Channel = null, string? Exe = null);

/// <summary>
/// The named pipe between the tray app (or anyone) and the OmdropOwl service:
/// status, start, stop, set (port, channel), firewall (an exe to allow in).
/// </summary>
public static class RadioPipe
{
    public const string Name = "omdrop-owl";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<RadioStatus?> SendAsync(RadioRequest req, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
            using var w = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var r = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await w.WriteLineAsync(JsonSerializer.Serialize(req, Json));
            var line = await r.ReadLineAsync(ct);
            return line == null ? null : JsonSerializer.Deserialize<RadioStatus>(line, Json);
        }
        catch (Exception e) when (e is TimeoutException or IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // the service isn't installed or isn't running
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
}
