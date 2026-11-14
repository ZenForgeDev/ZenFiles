using System.Text.Json;

namespace ZenFiles.Services;

public sealed class SettingsData
{
    /// <summary>Lagre åpne vinduer og faner og åpne dem igjen ved neste oppstart. Av som standard.</summary>
    public bool RestoreSession { get; set; }
}

/// <summary>Enkle innstillinger i %LOCALAPPDATA%\ZenFiles\settings.json.</summary>
public static class AppSettings
{
    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenFiles", "settings.json");

    static SettingsData? _data;
    static SettingsData Data => _data ??= Read();

    static SettingsData Read()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath)) ?? new SettingsData();
        }
        catch { }
        return new SettingsData();
    }

    static void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static bool RestoreSession
    {
        get => Data.RestoreSession;
        set { Data.RestoreSession = value; Write(); }
    }
}
