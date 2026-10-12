using Omdrop;
using Omdrop.Service;

// The OmdropOwl service: keeps owl.exe, and so awdl0, running, and answers the
// tray app on the omdrop-owl pipe. Runs as LocalSystem; install with
// tools\install-service.ps1. Run from a terminal (as an administrator), it
// does the same in the foreground, for testing.
Directory.CreateDirectory(RadioConfig.Dir);
Log.FilePath = Path.Combine(RadioConfig.Dir, "service.log");

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "OmdropOwl");
builder.Services.AddSingleton<Radio>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Radio>());
builder.Services.AddHostedService<PipeServer>();
builder.Build().Run();
