using System.Text.Json;

namespace ZenFiles.Services;

public sealed class SessionTab
{
    public string Path { get; set; } = "";
    public bool Dual { get; set; }
    public string RightPath { get; set; } = "";
    public bool LeftGrid { get; set; }    // ikonvisning i stedet for liste
    public bool RightGrid { get; set; }
}

public sealed class SessionWindow
{
    public List<SessionTab> Tabs { get; set; } = new();
    public int Selected { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Maximized { get; set; }
}

public sealed class SessionData
{
    public List<SessionWindow> Windows { get; set; } = new();
}

/// <summary>Lagrer hvilke vinduer og faner (lokale mapper) som var åpne. Brukes bare når innstillingen er på.</summary>
public static class SessionStore
{
    static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenFiles", "session.json");

    public static SessionData? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<SessionData>(File.ReadAllText(FilePath));
        }
        catch { return null; }
    }

    public static void Save(SessionData data)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
