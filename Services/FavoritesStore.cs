using System.Text.Json;

namespace ZenFiles.Services;

public sealed class FavoriteEntry
{
    public string Label { get; set; } = "";
    public string Glyph { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>Lagrer favoritter i sidepanelet. Finnes ingen fil, brukes standardlisten.</summary>
public static class FavoritesStore
{
    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenFiles", "favorites.json");

    public static List<FavoriteEntry>? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<List<FavoriteEntry>>(File.ReadAllText(FilePath));
        }
        catch { return null; }
    }

    public static void Save(IEnumerable<FavoriteEntry> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(items.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void Reset()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
