using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Omdrop.Tray;

/// <summary>
/// AirDrop for the tray: watches for awdl0, and while it's there and receiving
/// is on, announces us and runs the receiver, asking the user about each offer
/// through an overlay. Sending borrows the same interface and identity.
/// </summary>
public sealed class AirDropHost
{
    readonly Dispatcher ui;
    readonly Func<Settings> settings;
    readonly string serviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    readonly Dictionary<string, Overlay> progressCards = new();
    CancellationTokenSource? running;
    Receiving runningAs = Tray.Receiving.Off;

    public AwdlInterface? Awdl { get; private set; }
    public X509Certificate2? Cert { get; private set; }
    public event Action? Changed;

    public AirDropHost(Dispatcher ui, Func<Settings> settings)
    {
        this.ui = ui;
        this.settings = settings;
    }

    /// Called every few seconds: picks up awdl0 coming and going, and the
    /// receiving setting changing.
    public void Tick()
    {
        var s = settings();
        var awdl = AwdlInterface.Find("awdl0");
        bool want = awdl != null && s.Receiving != Tray.Receiving.Off;
        bool same = running != null && Awdl != null && awdl != null && Awdl.Index == awdl.Index && runningAs == s.Receiving;
        if (want && same)
            return;
        if (running != null && (!want || !same))
        {
            running.Cancel();
            running = null;
            Log.Info("receiving: off");
        }
        Awdl = awdl;
        if (awdl != null)
            Cert ??= Identity.Load(s.Name);
        if (want)
            Start(awdl!, s);
        Changed?.Invoke();
    }

    public bool IsReceiving => running != null;

    void Start(AwdlInterface awdl, Settings s)
    {
        var cts = running = new CancellationTokenSource();
        runningAs = s.Receiving;
        var receiver = new Receiver(awdl, Cert!, new ReceiverOptions(s.Name, "omdrop", s.SaveDir, 8771, false));
        receiver.Decide = Decide;
        receiver.AllowSender = s.Receiving == Tray.Receiving.ContactsOnly ? Contacts.Allow(s.ContactsFile) : null;
        receiver.Progress += (t, done, total) => ui.BeginInvoke(() => OnProgress(t, done, total));
        receiver.Completed += (t, files) => ui.BeginInvoke(() => OnCompleted(t, files));
        receiver.Failed += (t, why) => ui.BeginInvoke(() => OnFailed(t, why));
        receiver.LinksReceived += t => ui.BeginInvoke(() => OnLinks(t));
        Log.Info($"receiving: {s.Receiving}, as \"{s.Name}\"");
        _ = Task.Run(async () =>
        {
            try
            {
                using var mdns = new Mdns(awdl, serviceId, 8771, 137);
                var gap = TimeSpan.FromMilliseconds(1048.576 / 64);
                await Task.WhenAll(receiver.RunAsync(cts.Token), mdns.RunAsync(64, gap, TimeSpan.FromSeconds(3), cts.Token));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Log.Error($"receiving stopped: {e.Message}");
                _ = ui.BeginInvoke(() =>
                {
                    if (running == cts)
                        running = null;
                    Changed?.Invoke();
                });
            }
        });
    }

    Task<bool> Decide(IncomingTransfer t, CancellationToken ct) =>
        ui.InvokeAsync(() => Overlay.Ask(t.SenderName, Describe(t), Thumbnails.Decode(t.Icon), "Decline", "Accept", ct))
          .Task.Unwrap();

    static string Describe(IncomingTransfer t)
    {
        if (t.Files.Count == 0)
            return t.Urls.Count == 1 ? $"would like to share a link: {t.Urls[0]}" : $"would like to share {t.Urls.Count} links";
        string kind = t.Files.All(f => f.Type is "public.jpeg" or "public.heic" or "public.png") ? "photo"
                    : t.Files.All(f => f.Type.Contains("movie") || f.Type.Contains("mpeg-4")) ? "video" : "file";
        return t.Files.Count == 1
            ? $"would like to share {(kind == "file" ? $"\"{t.Files[0].Name}\"" : $"a {kind}")}."
            : $"would like to share {t.Files.Count} {kind}s.";
    }

    void OnProgress(IncomingTransfer t, long done, long? total)
    {
        if (!progressCards.TryGetValue(t.Id, out var card))
        {
            card = Overlay.Show(t.SenderName, "Receiving…", Thumbnails.Decode(t.Icon));
            progressCards[t.Id] = card;
        }
        card.Progress = total is > 0 ? Math.Min(1, (double)done / total.Value) : null;
        card.Message = total is > 0 ? $"Receiving… {Size(done)} of {Size(total.Value)}" : $"Receiving… {Size(done)}";
    }

    void OnCompleted(IncomingTransfer t, IReadOnlyList<string> files)
    {
        if (progressCards.Remove(t.Id, out var old))
            old.Dismiss();
        if (files.Count == 0)
            return;
        var first = files[0];
        var card = Overlay.Show(files.Count == 1 ? Path.GetFileName(first) : $"{files.Count} items",
            $"Received from {t.SenderName}", Thumbnails.Decode(t.Icon) ?? Thumbnails.FromFile(first));
        card.SetButtons(("Show in Folder", false, () => ShowInFolder(first)), ("Open", true, () => Open(first)));
        card.Clicked += () => Open(first);
        card.CloseAfter(TimeSpan.FromSeconds(12));
    }

    void OnFailed(IncomingTransfer t, string why)
    {
        if (progressCards.Remove(t.Id, out var old))
            old.Dismiss();
        var card = Overlay.Show(t.SenderName, $"The transfer failed: {why}", Thumbnails.Decode(t.Icon));
        card.CloseAfter(TimeSpan.FromSeconds(10));
    }

    void OnLinks(IncomingTransfer t)
    {
        var url = t.Urls[0];
        var card = Overlay.Show(t.SenderName, url);
        card.SetButtons(("Copy", false, () => System.Windows.Clipboard.SetText(url)), ("Open", true, () => Open(url)));
        card.CloseAfter(TimeSpan.FromSeconds(20));
    }

    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"can't open {target}: {e.Message}");
        }
    }

    static void ShowInFolder(string file) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = false });

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:F1} MB",
        _ => $"{bytes / 1073741824.0:F2} GB",
    };
}
