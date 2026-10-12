using System;
using System.IO;
using Microsoft.Win32;

namespace Omdrop.Tray;

/// <summary>Where the tray app meets Windows: Explorer's Send To, and starting with Windows.</summary>
public static class Shell
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "omdrop-owl";

    static string Exe => Environment.ProcessPath!;

    /// "Send to > AirDrop" in Explorer: a shortcut to us with --send, to which
    /// Explorer appends the selected files.
    public static void EnsureSendTo()
    {
        var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "AirDrop.lnk");
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null)
                return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = Exe;
            shortcut.Arguments = "--send";
            shortcut.IconLocation = Exe + ",0";
            shortcut.Description = "Send with AirDrop";
            shortcut.Save();
        }
        catch (Exception e)
        {
            Log.Warn($"couldn't add AirDrop to Send To: {e.Message}");
        }
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string s && s.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                k.SetValue(RunName, $"\"{Exe}\"");
            else
                k.DeleteValue(RunName, false);
        }
    }
}
