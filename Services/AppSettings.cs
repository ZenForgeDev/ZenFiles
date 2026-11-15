using System.Text.Json;

namespace ZenFiles.Services;

public sealed class SettingsData
{
    /// <summary>Lagre åpne vinduer og faner og åpne dem igjen ved neste oppstart. Av som standard.</summary>
    public bool RestoreSession { get; set; }

    /// <summary>Global mappefarge som hex (#RRGGBB). Tom = standard blå.</summary>
    public string FolderColor { get; set; } = "";

    /// <summary>Id til valgt tema (se Themes.cs). «standard» følger Windows.</summary>
    public string Theme { get; set; } = "standard";

    /// <summary>Vis mobiler/nettbrett koblet til med USB i sidepanelet.</summary>
    public bool MobileDevices { get; set; } = true;

    public bool PhoneAndroid { get; set; } = true;

    /// <summary>Spør GitHub om nyere versjon ved oppstart og hver 12. time.</summary>
    public bool UpdateCheck { get; set; } = true;

    /// <summary>Mapper som er festet som faner. Åpnes igjen ved oppstart, uavhengig av «husk vinduer og faner».</summary>
    public List<string> PinnedTabs { get; set; } = new();
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

    public static string Theme
    {
        get => string.IsNullOrEmpty(Data.Theme) ? "standard" : Data.Theme;
        set { Data.Theme = value; Write(); }
    }

    public static List<string> PinnedTabs
    {
        get => Data.PinnedTabs ??= new();
        set { Data.PinnedTabs = value; Write(); }
    }

    public static bool UpdateCheck
    {
        get => Data.UpdateCheck;
        set { Data.UpdateCheck = value; Write(); }
    }

    public static bool MobileDevices
    {
        get => Data.MobileDevices;
        set { Data.MobileDevices = value; Write(); }
    }

    public static string FolderColor
    {
        get => Data.FolderColor ?? "";
        set { Data.FolderColor = value ?? ""; Write(); }
    }
}
