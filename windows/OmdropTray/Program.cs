using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;

namespace Omdrop.Tray;

/// <summary>
/// One tray app per signed-in user. A second launch (Explorer's Send To, or
/// "OmdropTray.exe --send FILE...") hands its files to the running one over a
/// pipe and exits.
/// </summary>
public static class Program
{
    static string PipeName => $"omdrop-tray-{Process.GetCurrentProcess().SessionId}";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--render-demo")
        {
            new Application();
            Overlay.RenderDemo(args[1], args.Length > 2 ? args[2] : null);
            return 0;
        }
        var files = args.SkipWhile(a => a != "--send").Skip(1).Where(File.Exists).Select(Path.GetFullPath).ToArray();

        using var single = new Mutex(true, @"Local\OmdropTray", out bool first);
        if (!first)
        {
            if (files.Length > 0)
                Forward(files);
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var tray = new TrayApp(app);
        tray.ListenForForwards(PipeName);
        if (files.Length > 0)
            app.Dispatcher.BeginInvoke(() => tray.Send(files));
        app.Run();
        return 0;
    }

    /// Hands files to the running instance, which opens its device picker.
    static void Forward(string[] files)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            using var w = new StreamWriter(pipe, new UTF8Encoding(false));
            w.WriteLine("send\t" + string.Join('\t', files));
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
        }
    }
}
