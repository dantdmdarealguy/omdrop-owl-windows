using System.Diagnostics;
using System.Security.Principal;
using Omdrop;

// omdrop: AirDrop over awdl0, which owl.exe (windows/owl) brings up.
//   receive  be a receiver: accept everything sent (Everyone mode) into a folder
//   send     send files to a nearby Apple device
//   ble      only the Bluetooth wake advert, to check Windows sends it
//   firewall add the inbound rule receiving and sending need (administrator)
//   radio    ask the OmdropOwl service: status, start, stop

const string Usage = """
    usage: omdrop receive [--name NAME] [--save DIR] [--iface awdl0] [--port 8771]
                          [--model NAME] [--flags N] [--burst N] [-v]
           omdrop send FILE... [--to NAME] [--timeout SECONDS] [--no-ble] [--legacy-ble]
                               [--name NAME] [--iface awdl0] [-v]
           omdrop ble [SECONDS] [--legacy-ble]
           omdrop firewall      (administrator: allow AirDrop in, link-local only)
           omdrop radio [status|start|stop]   (the OmdropOwl service)
      --name     what Apple devices see us as (default: this computer's name)
      --save     where received files go (default: Downloads)
      --burst    announcement copies per round; 64 suits one channel-6 radio (default)
      --to       send only to the device with exactly this name (default: list devices, you pick)
      --no-ble   don't send the Bluetooth advert that wakes Apple devices
    """;

if (args.Length > 0 && args[0] == "radio")
{
    var cmd = args.Length > 1 ? args[1] : "status";
    var status = await RadioPipe.SendAsync(new RadioRequest(cmd), TimeSpan.FromSeconds(3));
    Console.WriteLine(status == null ? "the OmdropOwl service isn't running (tools\install-service.ps1)" : RadioPipe.Serialize(status));
    return status == null ? 1 : 0;
}

if (args.Length == 0 || args[0] is not ("receive" or "send" or "ble" or "firewall"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}
string command = args[0];

string name = Environment.MachineName, iface = "awdl0", model = "omdrop", save =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
string? to = null;
int port = 8771, flags = 137, burst = 64, timeout = 60, bleSeconds = 10;
bool ble = true, legacyBle = false, includeSelf = false;
string? format = null;
var files = new List<string>();
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
        case "--to": to = Next(); break;
        case "--timeout": timeout = int.Parse(Next()); break;
        case "--no-ble": ble = false; break;
        case "--legacy-ble": legacyBle = true; break;
        case "--include-self": includeSelf = true; break; // tests: send to our own receiver
        case "--format": format = Next(); break;              // dvzip or cpio (default: by the receiver's flags)
        case "-v": Log.Verbose = true; break;
        case var a when a.StartsWith('-'):
            Console.Error.WriteLine($"unknown option {a}\n{Usage}");
            return 2;
        case var a when command == "ble" && int.TryParse(a, out int s):
            bleSeconds = s;
            break;
        case var a when command == "send":
            if (!File.Exists(a))
            {
                Console.Error.WriteLine($"no such file: {a}");
                return 2;
            }
            files.Add(Path.GetFullPath(a));
            break;
        default:
            Console.Error.WriteLine($"unexpected argument {args[i]}\n{Usage}");
            return 2;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (command == "firewall")
{
    Firewall.Ensure(port);
    return 0;
}

if (command == "ble")
{
    using var adv = new AirDropAdvert(legacyBle);
    Log.Verbose = true;
    adv.Start();
    for (int i = 0; i < bleSeconds && !cts.IsCancellationRequested; i++)
    {
        await Task.Delay(1000);
        Log.Info($"ble: {adv.Status}");
    }
    return 0;
}

if (command == "send" && files.Count == 0)
{
    Console.Error.WriteLine($"send what?\n{Usage}");
    return 2;
}

try
{
    var awdl = await AwdlInterface.WaitAsync(iface, cts.Token);
    Log.Info($"{awdl.Name}: interface {awdl.Index}, {awdl.LinkLocal}");
    Firewall.Ensure(port);
    var cert = Identity.Load(name);

    if (command == "send")
        return await new Sender(awdl, cert, new SenderOptions(name, model, to, TimeSpan.FromSeconds(timeout), ble, legacyBle, includeSelf, format))
            .SendAsync(files, cts.Token);

    var serviceId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    using var mdns = new Mdns(awdl, serviceId, port, flags);
    Log.Info($"announcing \"{name}\" as {mdns.Instance} ({mdns.Host}), flags {flags}");
    var receiver = new Receiver(awdl, cert, new ReceiverOptions(name, model, save, port, Log.Verbose));
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
