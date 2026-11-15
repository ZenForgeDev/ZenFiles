using System.Text.Json;
using Windows.UI;

namespace ZenFiles.Services;

/// <summary>Navngitt fargevalg for mapper.</summary>
public sealed record FolderSwatch(string Name, string Hex);

/// <summary>
/// Mappefarger: en global farge (innstilling) og egne farger per mappe (lagres i foldercolors.json,
/// nøklet på sti). En farge per mappe går alltid foran den globale. Bare lokale mapper får farge.
/// </summary>
public static class FolderColors
{
    /// <summary>Ferdige valg i menyen. Første (blå) er standard og lagres ikke som valg.</summary>
    public static readonly FolderSwatch[] Swatches =
    {
        new("Blå",     "#35A0FF"),
        new("Turkis",  "#2CC4C4"),
        new("Grønn",   "#3FC060"),
        new("Gul",     "#F5C030"),
        new("Oransje", "#FF8F2E"),
        new("Rød",     "#F0524F"),
        new("Rosa",    "#F06AA8"),
        new("Lilla",   "#A06CF0"),
        new("Grå",     "#8E96A3"),
    };

    public const string DefaultHex = "#35A0FF";

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenFiles", "foldercolors.json");

    static Dictionary<string, string>? _map;
    static Dictionary<string, string> Map => _map ??= Read();

    /// <summary>Utløses når en mappefarge eller den globale fargen er endret. Ikonene tegner seg på nytt.</summary>
    public static event Action? Changed;

    static Dictionary<string, string> Read()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                if (d != null) return new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    static void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    static string Key(string path)
    {
        var p = path.Trim();
        if (p.Length > 3) p = p.TrimEnd('\\', '/');
        return p;
    }

    /// <summary>Fargen mappen skal ha: egen farge, ellers global farge, ellers standard blå.</summary>
    public static Color Resolve(string path)
    {
        if (Map.TryGetValue(Key(path), out var own) && TryParse(own, out var c)) return c;
        if (AppSettings.FolderColor is { Length: > 0 } g && TryParse(g, out var gc)) return gc;
        return Parse(DefaultHex);
    }

    public static bool HasOwn(string path) => Map.ContainsKey(Key(path));

    /// <summary>Setter farge på mapper. Tom/null fjerner fargen (mappen følger da den globale).</summary>
    public static void Set(IEnumerable<string> paths, string? hex)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(hex)) Map.Remove(Key(p));
            else Map[Key(p)] = hex!;
        }
        Write();
        Changed?.Invoke();
    }

    /// <summary>Setter den globale mappefargen. Tom/null = standard blå.</summary>
    public static void SetGlobal(string? hex)
    {
        AppSettings.FolderColor = string.IsNullOrEmpty(hex) || hex!.Equals(DefaultHex, StringComparison.OrdinalIgnoreCase) ? "" : hex;
        Changed?.Invoke();
    }

    // ───────── Fargeregning ─────────

    public static Color Parse(string hex) => TryParse(hex, out var c) ? c : Color.FromArgb(255, 0x35, 0xA0, 0xFF);

    public static bool TryParse(string hex, out Color c)
    {
        c = default;
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 6) h = "FF" + h;
        if (h.Length != 8) return false;
        try
        {
            c = Color.FromArgb(
                Convert.ToByte(h[..2], 16), Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16), Convert.ToByte(h.Substring(6, 2), 16));
            return true;
        }
        catch { return false; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    static readonly Color Black = Color.FromArgb(255, 0, 0, 0);

    /// <summary>
    /// Fire gradientstopp ut fra én grunnfarge: bakre flik (topp, bunn) og fremre flate (topp, bunn).
    /// For standard blå gir dette omtrent samme utseende som det opprinnelige ikonet.
    /// </summary>
    public static (Color BackTop, Color BackBottom, Color FrontTop, Color FrontBottom) Shades(Color baseColor) =>
        (Mix(baseColor, Black, 0.20), Mix(baseColor, Black, 0.38), Mix(baseColor, White, 0.40), baseColor);
}
