using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Omdrop.Tray;

/// <summary>
/// The AirDrop share sheet: the files, the devices nearby as they answer, and
/// a click on one to send. Nothing is offered to anyone until the user clicks.
/// </summary>
public sealed class SendWindow : Window
{
    readonly IReadOnlyList<string> files;
    readonly AirDropHost host;
    readonly Settings settings;
    readonly WrapPanel devices = new() { Margin = new Thickness(0, 14, 0, 0) };
    readonly TextBlock status;
    readonly CancellationTokenSource cts = new();
    readonly HashSet<string> shown = new();
    bool sending;

    public SendWindow(IReadOnlyList<string> files, AirDropHost host, Settings settings)
    {
        this.files = files;
        this.host = host;
        this.settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        FontFamily = Overlay.Font;
        Title = "AirDrop";

        var preview = new ContentControl { Width = 64, Height = 64 };
        var thumb = Thumbnails.FromFile(files[0]);
        preview.Content = thumb == null ? Overlay.Glyph(64) : new Border
        {
            CornerRadius = new CornerRadius(12), Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill },
        };
        long total = files.Sum(f => new FileInfo(f).Length);
        var what = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        what.Children.Add(new TextBlock
        {
            Text = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} items",
            Foreground = Overlay.Text, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        what.Children.Add(new TextBlock { Text = AirDropHost.Size(total), Foreground = Overlay.Muted, FontSize = 12.5 });
        var top = new DockPanel();
        DockPanel.SetDock(preview, Dock.Left);
        top.Children.Add(preview);
        top.Children.Add(what);

        status = new TextBlock
        {
            Text = "Looking for nearby devices… Apple devices need AirDrop on, and the screen on.",
            Foreground = Overlay.Muted, FontSize = 12.5, Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        var close = Overlay.MakeButton("Close", false, Close);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 14, 0, 0);

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "AIRDROP", Foreground = Overlay.Muted, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(top);
        body.Children.Add(status);
        body.Children.Add(devices);
        body.Children.Add(close);
        Content = new Border
        {
            Margin = new Thickness(16), Padding = new Thickness(20), CornerRadius = new CornerRadius(18),
            Background = Overlay.Card, BorderBrush = Overlay.Edge, BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.5, Color = Colors.Black },
            Child = body,
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        Loaded += (_, _) => _ = Browse();
        Closed += (_, _) => cts.Cancel();
    }

    async Task Browse()
    {
        if (host.Awdl == null || host.Cert == null)
        {
            status.Text = "AirDrop's radio isn't up: check the ESP32 is plugged in and the omdrop-owl service is running.";
            return;
        }
        var sender = new Sender(host.Awdl, host.Cert,
            new SenderOptions(settings.Name, "omdrop", null, TimeSpan.FromMinutes(5), Ble: true, LegacyBle: false));
        try
        {
            await foreach (var (service, name) in sender.FindAsync(cts.Token))
            {
                if (!shown.Add(name))
                    continue;
                devices.Children.Add(Tile(sender, service, name));
                if (!sending)
                    status.Text = "Click a device to send.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            status.Text = $"Couldn't look for devices: {e.Message}";
        }
    }

    FrameworkElement Tile(Sender sender, AirDropService service, string name)
    {
        var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        var avatar = new Grid { Width = 60, Height = 60 };
        avatar.Children.Add(new Ellipse { Fill = new LinearGradientBrush(Color.FromRgb(0x8E, 0x8E, 0x93), Color.FromRgb(0x63, 0x63, 0x66), 90) });
        avatar.Children.Add(new TextBlock
        {
            Text = initials, Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        var ring = new Ellipse { Stroke = Overlay.Accent, StrokeThickness = 3, Visibility = Visibility.Collapsed };
        avatar.Children.Add(ring);
        var label = new TextBlock
        {
            Text = name, Foreground = Overlay.Text, FontSize = 12, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 100, Margin = new Thickness(0, 6, 0, 0),
        };
        var state = new TextBlock { Foreground = Overlay.Muted, FontSize = 11, TextAlignment = TextAlignment.Center };
        var panel = new StackPanel { Width = 108, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        panel.Children.Add(avatar);
        panel.Children.Add(label);
        panel.Children.Add(state);
        panel.ToolTip = $"Send to {name}";

        panel.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            if (sending)
                return;
            sending = true;
            ring.Visibility = Visibility.Visible;
            state.Text = "Waiting…";
            status.Text = $"Waiting for {name} to accept…";
            byte[]? icon = settings.SendThumbnails ? files.Select(f => Thumbnails.Jpeg(f)).FirstOrDefault(i => i != null) : null;
            var progress = new Progress<(long Sent, long Total)>(p =>
            {
                state.Text = p.Total > 0 ? $"{100.0 * p.Sent / p.Total:F0}%" : "Sending…";
                status.Text = $"Sending to {name}…";
            });
            var result = await Task.Run(() => sender.SendToAsync(service, name, files, icon, progress, cts.Token));
            sending = false;
            ring.Visibility = Visibility.Collapsed;
            (state.Text, status.Text) = result switch
            {
                SendResult.Sent => ("Sent", $"Sent to {name}."),
                SendResult.Declined => ("Declined", $"{name} declined."),
                _ => ("Failed", $"Couldn't send to {name}. Try again, or move closer."),
            };
            if (result == SendResult.Sent)
                _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.BeginInvoke(Close));
        };
        return panel;
    }
}
