using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using Omdrop;

namespace Omdrop.Service;

/// <summary>%ProgramData%\omdrop-owl\config.json.</summary>
public sealed record RadioConfig
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "omdrop-owl");
    static string FilePath => Path.Combine(Dir, "config.json");

    /// The ESP32's serial port, or "auto" to find it by its USB bridge.
    public string Port { get; init; } = "auto";
    /// 6 on a 2.4 GHz-only ESP32 (S3); 149 or 44 with an ESP32-C5.
    public int Channel { get; init; } = 6;
    public bool Enabled { get; init; } = true;
    /// owl.exe, if not beside this service.
    public string? OwlPath { get; init; }

    public static RadioConfig Load()
    {
        try
        {
            return File.Exists(FilePath) ? RadioPipe.Deserialize<RadioConfig>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (JsonException e)
        {
            Log.Warn($"config: {e.Message}; using defaults");
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// owl.exe, kept running while enabled: found its radio, started with the
/// channel's flags, restarted (with backoff) if it exits, and stopped through
/// OWL_STOP_EVENT so it removes awdl0 on the way out.
/// </summary>
public sealed class Radio : BackgroundService
{
    // USB serial bridges ESP32 boards use. Others (a Flipper Zero is 0483) are never probed.
    static readonly string[] EspVids = ["VID_1A86", "VID_10C4", "VID_0403", "VID_303A"];

    readonly object gate = new();
    RadioConfig config = RadioConfig.Load();
    Process? owl;
    EventWaitHandle? stopEvent;
    string state = "stopped";
    string? port, error;
    CancellationTokenSource? restart = new();

    public RadioStatus Status
    {
        get
        {
            lock (gate)
                return new RadioStatus(state, port, config.Channel, AwdlInterface.Find("awdl0") != null, error, config.Enabled);
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (gate)
            config = config with { Enabled = enabled };
        config.Save();
        Kick();
    }

    public void Configure(string? newPort, int? channel)
    {
        lock (gate)
            config = config with { Port = newPort ?? config.Port, Channel = channel ?? config.Channel };
        config.Save();
        StopOwl();
        Kick();
    }

    /// Wake the loop now instead of at its next check.
    void Kick()
    {
        lock (gate)
            restart?.Cancel();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Log.Info($"service up; config {RadioPipe.Serialize(config)}");
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            CancellationTokenSource wake;
            lock (gate)
                wake = restart = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, wake.Token);

            if (!config.Enabled)
            {
                StopOwl();
                SetState("stopped", null);
            }
            else if (owl is not { HasExited: false })
            {
                if (owl != null)
                {
                    Log.Warn($"owl.exe exited ({owl.ExitCode}); restarting in {backoff.TotalSeconds:F0} s");
                    owl = null;
                    await Delay(backoff, linked.Token);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
                }
                if (!linked.IsCancellationRequested && config.Enabled)
                    StartOwl();
            }
            else if (state == "starting" && AwdlInterface.Find("awdl0") != null)
            {
                SetState("running", null);
                backoff = TimeSpan.FromSeconds(2);
            }
            await Delay(TimeSpan.FromSeconds(2), linked.Token);
        }
        StopOwl();
    }

    void StartOwl()
    {
        var exe = config.OwlPath ?? Path.Combine(AppContext.BaseDirectory, "owl.exe");
        if (!File.Exists(exe))
        {
            SetState("error", $"owl.exe not found at {exe}");
            return;
        }
        var found = config.Port == "auto" ? FindEsp() : config.Port;
        lock (gate)
            port = found;
        if (found == null)
        {
            SetState("no-radio", "no ESP32 found on a USB serial port; plug it in, or set the port");
            return;
        }

        var stopName = $"Global\\omdrop-owl-stop-{Environment.ProcessId}";
        stopEvent ??= new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        stopEvent.Reset();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RadioConfig.Dir,
        };
        foreach (var a in new[] { "-i", found, "-c", config.Channel.ToString(), "-N", "-F", "-E", "-S", "pin", "-T", "-v" })
            psi.ArgumentList.Add(a);
        psi.Environment["OWL_LOG_FILE"] = Path.Combine(RadioConfig.Dir, "owl.log");
        psi.Environment["OWL_STOP_EVENT"] = stopName;
        try
        {
            owl = Process.Start(psi);
            SetState("starting", null);
            Log.Info($"owl.exe started: {found}, channel {config.Channel}");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            SetState("error", $"owl.exe: {e.Message}");
        }
    }

    void StopOwl()
    {
        var p = owl;
        owl = null;
        if (p is not { HasExited: false })
            return;
        stopEvent?.Set();
        if (!p.WaitForExit(5000))
        {
            Log.Warn("owl.exe didn't stop; killing it (awdl0 may linger until Windows removes it)");
            p.Kill();
        }
        Log.Info("owl.exe stopped");
    }

    void SetState(string s, string? err)
    {
        lock (gate)
        {
            if (state != s || error != err)
                Log.Info($"radio: {s}{(err != null ? $" ({err})" : "")}");
            state = s;
            error = err;
        }
    }

    /// The first present serial port behind a USB bridge an ESP32 board uses.
    static string? FindEsp()
    {
        using var map = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        var present = map?.GetValueNames().Select(n => map.GetValue(n) as string).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb == null)
            return null;
        foreach (var dev in usb.GetSubKeyNames().Where(d => EspVids.Any(v => d.StartsWith(v, StringComparison.OrdinalIgnoreCase))))
        {
            using var devKey = usb.OpenSubKey(dev);
            foreach (var inst in devKey?.GetSubKeyNames() ?? [])
            {
                using var p = devKey!.OpenSubKey($@"{inst}\Device Parameters");
                if (p?.GetValue("PortName") is string name && present.Contains(name))
                    return name;
            }
        }
        return null;
    }

    static async Task Delay(TimeSpan t, CancellationToken ct)
    {
        try
        {
            await Task.Delay(t, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
