using System.Diagnostics;
using System.Security.Principal;

namespace Omdrop;

public static class Firewall
{

    /// Inbound TCP to the receiver and UDP 5353 (mDNS, both directions' answers),
    /// from link-local addresses only, for `exe` (default: this one), under a rule
    /// of its own. Needs an administrator; otherwise says how.
    public static void Ensure(int port, string? exe = null)
    {
        exe ??= Environment.ProcessPath!;
        string Rule = $"omdrop AirDrop (AWDL, link-local only): {Path.GetFileName(exe)}";
        var (code, rules) = Netsh($"advfirewall firewall show rule name=\"{Rule}\" verbose");
        if (code == 0 && rules.Contains(exe, StringComparison.OrdinalIgnoreCase))
            return;
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        string tcp = $"advfirewall firewall add rule name=\"{Rule}\" dir=in action=allow program=\"{exe}\" protocol=TCP localport={port} remoteip=fe80::/10";
        string udp = $"advfirewall firewall add rule name=\"{Rule}\" dir=in action=allow program=\"{exe}\" protocol=UDP localport=5353 remoteip=fe80::/10";
        if (!admin)
        {
            Log.Warn("no firewall rule for this omdrop.exe yet, so Windows may block senders and answers. " +
                     "From an administrator terminal, run once:");
            Log.Warn($"  netsh {tcp}");
            Log.Warn($"  netsh {udp}");
            return;
        }
        if (code == 0)
            Netsh($"advfirewall firewall delete rule name=\"{Rule}\""); // for an older path
        if (Netsh(tcp).Code == 0 && Netsh(udp).Code == 0)
            Log.Info($"added firewall rule \"{Rule}\" for {exe}");
        else
            Log.Warn("could not add the firewall rule");
    }

    static (int Code, string Output) Netsh(string args)
    {
        var p = Process.Start(new ProcessStartInfo("netsh", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
