using System.Diagnostics;
using System.Security.Principal;
using Omdrop;

// omdrop receive [options]: be an AirDrop receiver on awdl0, which owl.exe
// brings up. Accepts everything offered (Everyone mode) into the save folder.

const string Usage = """
    usage: omdrop receive [--name NAME] [--save DIR] [--iface awdl0] [--port 8771]
                          [--model NAME] [--flags N] [--burst N] [-v]
      --name    what Apple devices list us as (default: this computer's name)
      --save    where received files go (default: Downloads)
      --burst   announcement copies per round; 64 suits one channel-6 radio (default)
    """;

if (args.Length == 0 || args[0] != "receive")
{
    Console.Error.WriteLine(Usage);
    return 2;
}

string name = Environment.MachineName, iface = "awdl0", model = "omdrop", save =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
int port = 8771, flags = 137, burst = 64;
for (int i = 1; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--name": name = Next(); break;
        case "--save": save = Next(); break;
        case "--iface": iface = Next(); break;
        case "--port": port = int.Parse(Next()); break;
        case "--model": model = Next(); break;
        case "--flags": flags = int.Parse(Next()); break;
        case "--burst": burst = int.Parse(Next()); break;
        case "-v": Log.Verbose = true; break;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}\n{Usage}");
            return 2;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var awdl = await AwdlInterface.WaitAsync(iface, cts.Token);
    Log.Info($"{awdl.Name}: interface {awdl.Index}, {awdl.LinkLocal}");
    Firewall.Ensure(port);

    var serviceId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    using var mdns = new Mdns(awdl, serviceId, port, flags);
    Log.Info($"announcing \"{name}\" as {mdns.Instance} ({mdns.Host}), flags {flags}");

    var receiver = new Receiver(awdl, Identity.Load(name), new ReceiverOptions(name, model, save, port, Log.Verbose));
    // 16 slots of 64 TU: one AWDL period, over which a burst is spread.
    var gap = TimeSpan.FromMilliseconds(1048.576 / Math.Max(burst, 1));
    await Task.WhenAll(
        receiver.RunAsync(cts.Token),
        mdns.RunAsync(burst, gap, TimeSpan.FromSeconds(3), cts.Token));
}
catch (OperationCanceledException)
{
}
Log.Info("stopped");
return 0;

static class Firewall
{
    const string Rule = "omdrop AirDrop receiver (AWDL, link-local only)";

    /// Inbound TCP to the receiver and UDP 5353, from link-local addresses only.
    /// Needs an administrator once; otherwise says how.
    public static void Ensure(int port)
    {
        if (Netsh($"advfirewall firewall show rule name=\"{Rule}\"") == 0)
            return;
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var exe = Environment.ProcessPath!;
        string tcp = $"advfirewall firewall add rule name=\"{Rule}\" dir=in action=allow program=\"{exe}\" protocol=TCP localport={port} remoteip=fe80::/10";
        string udp = $"advfirewall firewall add rule name=\"{Rule}\" dir=in action=allow program=\"{exe}\" protocol=UDP localport=5353 remoteip=fe80::/10";
        if (!admin)
        {
            Log.Warn("no firewall rule for the receiver yet, so Windows may block senders. From an administrator terminal, run once:");
            Log.Warn($"  netsh {tcp}");
            Log.Warn($"  netsh {udp}");
            return;
        }
        if (Netsh(tcp) == 0 && Netsh(udp) == 0)
            Log.Info($"added firewall rule \"{Rule}\"");
        else
            Log.Warn("could not add the firewall rule");
    }

    static int Netsh(string args)
    {
        var p = Process.Start(new ProcessStartInfo("netsh", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
