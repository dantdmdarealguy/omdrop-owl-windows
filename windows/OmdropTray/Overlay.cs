using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Omdrop.Tray;

/// <summary>
/// AirDrop's notifications: dark, rounded cards that slide in at the top right
/// and stack, like a Mac's. A card has a picture (a thumbnail, or the AirDrop
/// glyph), a title, a line of text, maybe a progress bar, and buttons.
/// </summary>
public sealed class Overlay : Window
{
    static readonly List<Overlay> Open = new();
    const double CardWidth = 372, EdgeMargin = 14, Gap = 10;

    public static readonly Brush Card = new SolidColorBrush(Color.FromArgb(0xF2, 0x24, 0x24, 0x28));
    public static readonly Brush Edge = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    public static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA6));
    public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
    public static readonly Brush Secondary = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");

    readonly TextBlock title, message;
    readonly ContentControl picture;
    readonly ProgressBar bar;
    readonly StackPanel buttons;
    DispatcherTimer? autoClose;
    bool closing;

    Overlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = CardWidth + 24; // room for the shadow
        FontFamily = Font;

        picture = new ContentControl { Width = 52, Height = 52, VerticalAlignment = VerticalAlignment.Top };
        var header = new TextBlock
        {
            Text = "AIRDROP", Foreground = Muted, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        };
        title = new TextBlock { Foreground = Text, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        message = new TextBlock { Foreground = Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        bar = new ProgressBar
        {
            Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed,
            Foreground = Accent, Background = Secondary, BorderThickness = new Thickness(0),
        };
        bar.Template = RoundedBar();
        buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(header);
        text.Children.Add(title);
        text.Children.Add(message);
        text.Children.Add(bar);
        var row = new DockPanel();
        DockPanel.SetDock(picture, Dock.Left);
        row.Children.Add(picture);
        row.Children.Add(text);
        var body = new StackPanel();
        body.Children.Add(row);
        body.Children.Add(buttons);

        Content = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(16),
            Background = Card,
            BorderBrush = Edge,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black },
            Child = body,
        };
        Opacity = 0;
        Loaded += (_, _) => Animate(true);
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
    }

    /// Clicking the card itself (not a button).
    public event Action? Clicked;

    public string Heading { set => title.Text = value; }
    public string Message { set => message.Text = value; }

    /// 0..1, or null to hide the bar.
    public double? Progress
    {
        set
        {
            bar.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
            bar.Value = value ?? 0;
        }
    }

    public ImageSource? Picture
    {
        set => picture.Content = value == null ? Glyph(52) : new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new ImageBrush(value) { Stretch = Stretch.UniformToFill },
        };
    }

    /// Replaces the buttons; each closes the card unless keepOpen.
    public void SetButtons(params (string Label, bool Primary, Action Click)[] defs)
    {
        buttons.Children.Clear();
        buttons.Visibility = defs.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (label, primary, click) in defs)
            buttons.Children.Add(MakeButton(label, primary, () =>
            {
                click();
                Dismiss();
            }));
    }

    /// Closes the card after `after`, unless the mouse is over it then.
    public void CloseAfter(TimeSpan after)
    {
        autoClose?.Stop();
        autoClose = new DispatcherTimer { Interval = after };
        autoClose.Tick += (_, _) =>
        {
            if (IsMouseOver)
                return;
            autoClose.Stop();
            Dismiss();
        };
        autoClose.Start();
    }

    public void Dismiss()
    {
        if (closing)
            return;
        closing = true;
        autoClose?.Stop();
        Animate(false);
    }

    public static Overlay Show(string title, string message, ImageSource? picture = null)
    {
        var o = new Overlay { Heading = title, Message = message, Picture = picture };
        o.SetButtons();
        Open.Add(o);
        o.Closed += (_, _) =>
        {
            Open.Remove(o);
            Layout();
        };
        o.Show();
        Layout();
        return o;
    }

    /// Asks a question with two buttons; true for the primary one. Cancelling
    /// `ct` (a timeout) closes the card with false.
    public static Task<bool> Ask(string title, string message, ImageSource? picture, string no, string yes,
        System.Threading.CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>();
        var o = Show(title, message, picture);
        o.SetButtons((no, false, () => done.TrySetResult(false)), (yes, true, () => done.TrySetResult(true)));
        o.Closed += (_, _) => done.TrySetResult(false);
        ct.Register(() => o.Dispatcher.BeginInvoke(() =>
        {
            done.TrySetResult(false);
            o.Dismiss();
        }));
        return done.Task;
    }

    /// Stacks the open cards down from the top right of the work area.
    static void Layout()
    {
        var area = SystemParameters.WorkArea;
        double top = area.Top + EdgeMargin - 12;
        foreach (var o in Open.ToList())
        {
            o.Left = area.Right - o.Width - EdgeMargin + 12;
            if (double.IsNaN(o.Top) || o.Top == 0 || Math.Abs(o.Top - top) > 0.5)
                o.BeginAnimation(TopProperty, new DoubleAnimation(top, TimeSpan.FromMilliseconds(o.IsLoaded ? 180 : 0))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            o.UpdateLayout();
            top += (o.ActualHeight > 0 ? o.ActualHeight : 120) - 24 + Gap;
        }
    }

    void Animate(bool appear)
    {
        var ease = new CubicEase { EasingMode = appear ? EasingMode.EaseOut : EasingMode.EaseIn };
        var time = TimeSpan.FromMilliseconds(appear ? 240 : 180);
        var fade = new DoubleAnimation(appear ? 1 : 0, time) { EasingFunction = ease };
        if (!appear)
            fade.Completed += (_, _) => Close();
        var slide = new TranslateTransform(appear ? 28 : 0, 0);
        ((Border)Content).RenderTransform = slide;
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(appear ? 0 : 28, time) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, fade);
        if (appear)
            Layout();
    }

    public static Button MakeButton(string label, bool primary, Action click)
    {
        var b = new Button
        {
            Content = label,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = Text,
            FontFamily = Font,
            FontSize = 12.5,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = RoundedButton(primary ? Accent : Secondary),
        };
        b.Click += (_, _) => click();
        return b;
    }

    static ControlTemplate RoundedButton(Brush fill)
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, fill);
        border.SetValue(Border.PaddingProperty, new Thickness(16, 6, 16, 6));
        border.Name = "b";
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(text);
        t.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.85, "b"));
        t.Triggers.Add(hover);
        return t;
    }

    static ControlTemplate RoundedBar()
    {
        var t = new ControlTemplate(typeof(ProgressBar));
        var track = new FrameworkElementFactory(typeof(Border), "PART_Track");
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        track.SetValue(Border.BackgroundProperty, Secondary);
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.AppendChild(track);
        var fill = new FrameworkElementFactory(typeof(Border), "PART_Indicator");
        fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        fill.SetValue(Border.BackgroundProperty, Accent);
        fill.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        grid.AppendChild(fill);
        t.VisualTree = grid;
        return t;
    }

    /// Renders sample cards off-screen to a PNG (OmdropTray.exe --render-demo FILE),
    /// for checking how they look without showing anything.
    public static void RenderDemo(string png, string? photo)
    {
        var thumb = photo != null ? Thumbnails.FromFile(photo) : null;
        var cards = new List<Overlay>();
        var ask = new Overlay { Heading = "Vaibhav's iPhone", Message = "would like to share a photo.", Picture = thumb };
        ask.SetButtons(("Decline", false, () => { }), ("Accept", true, () => { }));
        cards.Add(ask);
        var prog = new Overlay { Heading = "Vaibhav's iPhone", Message = "Receiving… 96 KB of 214 KB", Picture = thumb, Progress = 0.45 };
        prog.SetButtons();
        cards.Add(prog);
        var done = new Overlay { Heading = "IMG_2760.jpg", Message = "Received from Vaibhav's iPhone", Picture = null };
        done.SetButtons(("Show in Folder", false, () => { }), ("Open", true, () => { }));
        cards.Add(done);

        var stack = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x6E, 0x8C)) };
        foreach (var c in cards)
        {
            var content = (Border)c.Content!;
            c.Content = null;
            content.Width = CardWidth;
            stack.Children.Add(content);
        }
        stack.Measure(new Size(CardWidth + 24, double.PositiveInfinity));
        stack.Arrange(new Rect(stack.DesiredSize));
        stack.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(stack.ActualWidth * 2), (int)Math.Ceiling(stack.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(stack);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = System.IO.File.Create(png);
        enc.Save(f);
    }

    /// The AirDrop glyph: a blue disc with three arcs over a dot.
    public static FrameworkElement Glyph(double size)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse
        {
            Fill = new LinearGradientBrush(Color.FromRgb(0x2A, 0xC8, 0xFF), Color.FromRgb(0x0A, 0x6C, 0xFF), 90),
        });
        var canvas = new Canvas { Width = size, Height = size };
        double cx = size / 2, cy = size * 0.62;
        foreach (var r in new[] { size * 0.13, size * 0.24, size * 0.35 })
        {
            var arc = new PathFigure { StartPoint = new Point(cx - r * 0.87, cy - r * 0.5) };
            arc.Segments.Add(new ArcSegment(new Point(cx + r * 0.87, cy - r * 0.5), new Size(r, r), 0, false,
                SweepDirection.Clockwise, true));
            canvas.Children.Add(new Path
            {
                Data = new PathGeometry([arc]), Stroke = Brushes.White, StrokeThickness = size * 0.055,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        }
        var dot = new Ellipse { Width = size * 0.1, Height = size * 0.1, Fill = Brushes.White };
        Canvas.SetLeft(dot, cx - size * 0.05);
        Canvas.SetTop(dot, cy - size * 0.05);
        canvas.Children.Add(dot);
        g.Children.Add(canvas);
        return g;
    }
}
