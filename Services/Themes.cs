using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ZenFiles.Services;

/// <summary>
/// Et tema: lys/mørk base, bakgrunn (null = Windows Mica), panelfarge, accentfarge og markeringsfarge.
/// Markeringsfargen brukes bak valgte rader med hvit tekst, så den må være mørk nok.
/// </summary>
public sealed record ZenTheme(string Id, string Name, ElementTheme Base, string? Background, string? Panel, string Accent, string Select);

public static class Themes
{
    public static readonly ZenTheme[] All =
    {
        new("standard",   "Standard (følger Windows)", ElementTheme.Default, null,      null,      "#3D9BFF", "#0A6CFF"),
        new("amoled",     "AMOLED svart",              ElementTheme.Dark,    "#000000", "#0B0B0B", "#3D9BFF", "#0A6CFF"),
        new("midnatt",    "Midnatt",                   ElementTheme.Dark,    "#0F1424", "#171E36", "#7C93FF", "#4A66E0"),
        new("nord",       "Nord",                      ElementTheme.Dark,    "#2E3440", "#3B4252", "#88C0D0", "#5E81AC"),
        new("skog",       "Skog",                      ElementTheme.Dark,    "#101A14", "#18271D", "#4CC27A", "#2E9E5B"),
        new("solnedgang", "Solnedgang",                ElementTheme.Dark,    "#1D1417", "#2A1B20", "#FF8A55", "#E0612F"),
        new("rose",       "Rosé (lys)",                ElementTheme.Light,   "#FBEFF2", "#FFF9FA", "#E0558A", "#D6457A"),
    };

    public static ZenTheme Current => All.FirstOrDefault(t => t.Id == AppSettings.Theme) ?? All[0];

    /// <summary>Velger tema, oppdaterer felles farger og alle åpne vinduer.</summary>
    public static void SetCurrent(string id)
    {
        AppSettings.Theme = id;
        ApplyResources();
        foreach (var w in App.Windows.ToList()) w.ApplyTheme();
    }

    static SolidColorBrush B(string key) => (SolidColorBrush)Application.Current.Resources[key];

    static Color Parse(string hex) => FolderColors.Parse(hex);

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    static readonly Color Black = Color.FromArgb(255, 0, 0, 0);

    /// <summary>Setter de felles brushene og Windows-accenten ut fra valgt tema. Kalles ved oppstart og ved bytte.</summary>
    public static void ApplyResources()
    {
        var t = Current;
        var res = Application.Current.Resources;

        var select = Parse(t.Select);
        B("ZenSelectBrush").Color = select;
        B("ZenSelectHoverBrush").Color = Mix(select, White, 0.12);
        B("ZenSelectPressedBrush").Color = Mix(select, Black, 0.15);
        B("ZenSelectSoftBrush").Color = Alpha(select, 0x33);
        B("ZenSelectSoftHoverBrush").Color = Alpha(select, 0x44);
        B("ZenIconBrush").Color = Parse(t.Accent);

        if (t.Panel != null)
            B("ZenPanelBrush").Color = Parse(t.Panel);
        else   // Standard: samme utseende som Windows' lagfarge over Mica
            B("ZenPanelBrush").Color = Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? Color.FromArgb(0x4C, 0x3A, 0x3A, 0x3A)
                : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);

        // Windows-accenten (knapper, brytere, fokus). Standard fjerner overstyringen så systemfargen gjelder.
        string[] keys =
        {
            "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
            "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"
        };
        if (t.Id == "standard")
        {
            foreach (var k in keys) if (res.ContainsKey(k)) res.Remove(k);
        }
        else
        {
            var a = Parse(t.Accent);
            res["SystemAccentColor"] = a;
            res["SystemAccentColorLight1"] = Mix(a, White, 0.15);
            res["SystemAccentColorLight2"] = Mix(a, White, 0.30);
            res["SystemAccentColorLight3"] = Mix(a, White, 0.45);
            res["SystemAccentColorDark1"] = Mix(a, Black, 0.15);
            res["SystemAccentColorDark2"] = Mix(a, Black, 0.30);
            res["SystemAccentColorDark3"] = Mix(a, Black, 0.45);
        }
    }

    /// <summary>Tekstfarge på vindusknappene (minimer/maksimer/lukk) for solide temaer.</summary>
    public static Color CaptionForeground(ZenTheme t) => t.Base == ElementTheme.Light ? Black : White;
}
