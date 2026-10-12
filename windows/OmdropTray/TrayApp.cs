using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Omdrop.Tray;

/// <summary>The notification-area icon, its menu, and the loop that keeps both current.</summary>
public sealed class TrayApp
{
    readonly System.Windows.Application app;
    readonly Forms.NotifyIcon icon;
    readonly AirDropHost host;
    readonly DispatcherTimer timer;
    Settings settings = Settings.Load();
    RadioStatus? radio;
    bool radioAsked;
    string? lastIconKey;

    public TrayApp(System.Windows.Application app)
    {
        this.app = app;
        Log.FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "omdrop", "tray.log");
        Directory.CreateDirectory(Path.GetDirectoryName(Log.FilePath)!);
        host = new AirDropHost(app.Dispatcher, () => settings);
        host.Changed += Refresh;

        icon = new Forms.NotifyIcon { Visible = true, Text = "AirDrop" };
        icon.ContextMenuStrip = new Forms.ContextMenuStrip();
        icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowMenu();
        };
        icon.DoubleClick += (_, _) => PickAndSend();
        BuildMenu();

        Shell.EnsureSendTo();
        if (settings.Receiving == Receiving.Everyone10 && settings.EveryoneUntil == null)
            Update(settings with { EveryoneUntil = DateTime.Now.AddMinutes(10) });

        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) => await Poll();
        timer.Start();
        _ = Poll();
    }

    async Task Poll()
    {
        // The service adds our firewall rule (it runs as SYSTEM; we don't).
        if (!radioAsked)
        {
            radioAsked = true;
            await RadioPipe.SendAsync(new RadioRequest("firewall", Exe: Environment.ProcessPath), TimeSpan.FromSeconds(2));
        }
        radio = await RadioPipe.SendAsync(new RadioRequest("status"), TimeSpan.FromSeconds(2));
        if (settings.Receiving == Receiving.Everyone10 && settings.EveryoneUntil is { } until && DateTime.Now >= until)
        {
            // As on an iPhone: "Everyone for 10 Minutes" falls back to Contacts Only.
            Update(settings with { Receiving = settings.ContactsFile != null ? Receiving.ContactsOnly : Receiving.Off, EveryoneUntil = null });
            Overlay.Show("AirDrop", settings.Receiving == Receiving.Off ? "Receiving is off again." : "Receiving from Contacts Only again.")
                .CloseAfter(TimeSpan.FromSeconds(6));
        }
        host.Tick();
        Refresh();
    }

    void Update(Settings s)
    {
        settings = s;
        settings.Save();
        host.Tick();
        Refresh();
    }

    string StatusLine()
    {
        if (radio == null)
            return "Radio service isn't running";
        if (!radio.Enabled)
            return "Radio off";
        if (radio.State == "no-radio")
            return "No ESP32 found: plug it in";
        if (radio.State == "error")
            return $"Radio error: {radio.Error}";
        if (!radio.Awdl0)
            return "Radio starting…";
        return settings.Receiving switch
        {
            Receiving.Off => "Receiving off",
            Receiving.ContactsOnly => "Receiving from Contacts Only",
            Receiving.Everyone10 => $"Receiving from Everyone until {settings.EveryoneUntil:HH:mm}",
            _ => "Receiving from Everyone",
        };
    }

    void Refresh()
    {
        var status = StatusLine();
        icon.Text = ("AirDrop: " + status).Length > 63 ? ("AirDrop: " + status)[..63] : "AirDrop: " + status;
        bool ok = radio is { Awdl0: true };
        var key = $"{ok}{host.IsReceiving}";
        if (key != lastIconKey)
        {
            lastIconKey = key;
            var old = icon.Icon;
            icon.Icon = Draw(ok ? (host.IsReceiving ? Color.FromArgb(0x0A, 0x84, 0xFF) : Color.FromArgb(0x63, 0x63, 0x66)) : Color.FromArgb(0x8E, 0x8E, 0x93), !ok);
            old?.Dispose();
        }
    }

    void ShowMenu() =>
        typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(icon, null);

    void BuildMenu()
    {
        var m = icon.ContextMenuStrip!;
        m.Items.Clear();
        m.Items.Add(new Forms.ToolStripMenuItem(StatusLine()) { Enabled = false });
        m.Items.Add(new Forms.ToolStripSeparator());

        var receiving = new Forms.ToolStripMenuItem("Receiving");
        void Mode(string text, Receiving r, bool enabled = true)
        {
            var item = new Forms.ToolStripMenuItem(text) { Checked = settings.Receiving == r, Enabled = enabled };
            item.Click += (_, _) => Update(settings with
            {
                Receiving = r,
                EveryoneUntil = r == Receiving.Everyone10 ? DateTime.Now.AddMinutes(10) : null,
            });
            receiving.DropDownItems.Add(item);
        }
        Mode("Off", Receiving.Off);
        Mode(settings.ContactsFile == null ? "Contacts Only (choose a contacts file first)" : "Contacts Only",
            Receiving.ContactsOnly, settings.ContactsFile != null);
        Mode("Everyone for 10 Minutes", Receiving.Everyone10);
        Mode("Everyone", Receiving.Everyone);
        m.Items.Add(receiving);

        m.Items.Add("Send Files…", null, (_, _) => PickAndSend());
        m.Items.Add("Open Received Files", null, (_, _) => AirDropHost.Open(settings.SaveDir));
        m.Items.Add(new Forms.ToolStripSeparator());

        var options = new Forms.ToolStripMenuItem("Settings");
        options.DropDownItems.Add($"Name: {settings.Name}…", null, (_, _) => Rename());
        options.DropDownItems.Add($"Save To: {settings.SaveDir}…", null, (_, _) => ChooseFolder());
        options.DropDownItems.Add(settings.ContactsFile == null ? "Contacts File (.vcf)…" : $"Contacts File: {Path.GetFileName(settings.ContactsFile)}…",
            null, (_, _) => ChooseContacts());
        var thumbs = new Forms.ToolStripMenuItem("Send Thumbnails") { Checked = settings.SendThumbnails };
        thumbs.Click += (_, _) => Update(settings with { SendThumbnails = !settings.SendThumbnails });
        options.DropDownItems.Add(thumbs);
        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = Shell.StartsWithWindows };
        startup.Click += (_, _) => Shell.StartsWithWindows = !Shell.StartsWithWindows;
        options.DropDownItems.Add(startup);
        m.Items.Add(options);

        var radioItem = new Forms.ToolStripMenuItem("Radio");
        var on = new Forms.ToolStripMenuItem("On") { Checked = radio?.Enabled == true, Enabled = radio != null };
        on.Click += async (_, _) =>
        {
            radio = await RadioPipe.SendAsync(new RadioRequest(radio?.Enabled == true ? "stop" : "start"), TimeSpan.FromSeconds(3));
            Refresh();
        };
        radioItem.DropDownItems.Add(on);
        radioItem.DropDownItems.Add(new Forms.ToolStripMenuItem(
            radio == null ? "The omdrop-owl service isn't running (tools\\install-service.ps1)"
                          : $"{radio.State}{(radio.Port != null ? $" on {radio.Port}" : "")}, channel {radio.Channel}") { Enabled = false });
        radioItem.DropDownItems.Add("Service Log", null, (_, _) =>
            AirDropHost.Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "omdrop-owl", "service.log")));
        m.Items.Add(radioItem);

        m.Items.Add(new Forms.ToolStripSeparator());
        m.Items.Add("Quit", null, (_, _) =>
        {
            icon.Visible = false;
            app.Shutdown();
        });
    }

    void PickAndSend()
    {
        using var dlg = new Forms.OpenFileDialog { Multiselect = true, Title = "Send with AirDrop" };
        if (dlg.ShowDialog() == Forms.DialogResult.OK && dlg.FileNames.Length > 0)
            Send(dlg.FileNames);
    }

    public void Send(string[] files)
    {
        if (host.Awdl == null)
        {
            Overlay.Show("AirDrop", "The AirDrop radio isn't up yet: check the ESP32 and the omdrop-owl service.")
                .CloseAfter(TimeSpan.FromSeconds(8));
            return;
        }
        var w = new SendWindow(files, host, settings);
        w.Show();
        w.Activate();
    }

    void Rename()
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox("What Apple devices see this PC as:", "AirDrop", settings.Name);
        if (!string.IsNullOrWhiteSpace(name))
            Update(settings with { Name = name.Trim() });
    }

    void ChooseFolder()
    {
        using var dlg = new Forms.FolderBrowserDialog { SelectedPath = settings.SaveDir, Description = "Where received files go" };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
            Update(settings with { SaveDir = dlg.SelectedPath });
    }

    void ChooseContacts()
    {
        using var dlg = new Forms.OpenFileDialog
        {
            Title = "Contacts for Contacts Only (export them as a .vcf)",
            Filter = "vCard (*.vcf)|*.vcf|All files|*.*",
        };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
            Update(settings with { ContactsFile = dlg.FileName });
    }

    /// Files from a second launch (Explorer's Send To), over a pipe.
    public void ListenForForwards(string pipeName)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync();
                    using var r = new StreamReader(pipe, Encoding.UTF8);
                    var line = await r.ReadLineAsync();
                    var parts = line?.Split('\t') ?? [];
                    if (parts.Length > 1 && parts[0] == "send")
                        await app.Dispatcher.InvokeAsync(() => Send(parts.Skip(1).Where(File.Exists).ToArray()));
                }
                catch (IOException)
                {
                }
            }
        });
    }

    /// The tray icon: a disc with AirDrop's arcs; grey and hollow-dotted when the radio's down.
    static Icon Draw(Color fill, bool down)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            foreach (var r in new[] { 4f, 7.5f, 11f })
                g.DrawArc(pen, 16 - r, 19 - r, 2 * r, 2 * r, 210, 120);
            if (down)
                g.DrawEllipse(Pens.White, 14, 17, 4, 4);
            else
                g.FillEllipse(Brushes.White, 14, 17, 4, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
