using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Omdrop;

namespace Omdrop.Service;

/// <summary>
/// The omdrop-owl named pipe: one JSON request per line, answered with the
/// radio's status. Signed-in users may connect, since the tray app runs as one.
/// </summary>
public sealed class PipeServer(Radio radio) : BackgroundService
{
    // Only these may be allowed through the firewall on a user's say-so.
    static readonly string[] Allowable = ["OmdropTray.exe", "omdrop.exe"];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // Whoever runs this (SYSTEM as a service; an administrator testing it in
        // a terminal) must be able to make the next instance of the pipe.
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(RadioPipe.Name, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    0, 0, security);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logged and retried: a fault here would stop the whole service.
                Log.Error($"pipe: {e.Message}");
                await Task.Delay(5000, ct);
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            _ = Task.Run(() => Serve(pipe), CancellationToken.None);
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var r = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var w = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await r.ReadLineAsync();
                var req = line == null ? null : RadioPipe.Deserialize<RadioRequest>(line);
                switch (req?.Cmd)
                {
                    case "start":
                        radio.SetEnabled(true);
                        break;
                    case "stop":
                        radio.SetEnabled(false);
                        break;
                    case "set":
                        radio.Configure(req.Port, req.Channel);
                        break;
                    case "firewall" when req.Exe != null && Allowable.Contains(Path.GetFileName(req.Exe), StringComparer.OrdinalIgnoreCase)
                                         && File.Exists(req.Exe):
                        Firewall.Ensure(8771, req.Exe);
                        break;
                }
                await w.WriteLineAsync(RadioPipe.Serialize(radio.Status));
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
                Log.Debug($"pipe: {e.Message}");
            }
        }
    }
}
