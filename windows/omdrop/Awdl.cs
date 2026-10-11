using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Omdrop;

/// <summary>awdl0 as Windows sees it: owl.exe's Wintun adapter.</summary>
public sealed record AwdlInterface(string Name, int Index, IPAddress LinkLocal)
{
    public static AwdlInterface? Find(string name)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!nic.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || nic.OperationalStatus != OperationalStatus.Up)
                continue;
            var props = nic.GetIPProperties();
            var ll = props.UnicastAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6LinkLocal);
            if (ll != null)
                return new AwdlInterface(nic.Name, props.GetIPv6Properties().Index, ll);
        }
        return null;
    }

    /// Waits for owl.exe to bring the interface up.
    public static async Task<AwdlInterface> WaitAsync(string name, CancellationToken ct)
    {
        bool told = false;
        while (true)
        {
            if (Find(name) is { } found)
                return found;
            if (!told)
            {
                Log.Info($"waiting for {name}; start owl.exe (windows/owl) from an administrator terminal");
                told = true;
            }
            await Task.Delay(1000, ct);
        }
    }
}
