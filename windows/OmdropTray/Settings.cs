using System;
using System.IO;
using System.Text.Json;

namespace Omdrop.Tray;

public enum Receiving { Off, ContactsOnly, Everyone10, Everyone }

/// <summary>%APPDATA%\omdrop\settings.json: the user's AirDrop choices.</summary>
public sealed record Settings
{
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "omdrop");
    static string FilePath => Path.Combine(Dir, "settings.json");

    /// What Apple devices see us as.
    public string Name { get; init; } = Environment.MachineName;
    public string SaveDir { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public Receiving Receiving { get; init; } = Receiving.Everyone10;
    /// When "Everyone for 10 Minutes" ends.
    public DateTime? EveryoneUntil { get; init; }
    /// A .vcf of the people Contacts Only lets in.
    public string? ContactsFile { get; init; }
    /// Send a thumbnail with images, so the other person's prompt shows the picture.
    public bool SendThumbnails { get; init; } = true;

    public static Settings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings()
                : new Settings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
