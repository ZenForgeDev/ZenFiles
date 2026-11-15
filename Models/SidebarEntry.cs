using Microsoft.UI.Xaml;

namespace ZenFiles.Models;

public sealed class SidebarEntry
{
    public string Label { get; set; } = "";
    public string Glyph { get; set; } = "\uE8B7";
    public string Path { get; set; } = "";
    public string Sub { get; set; } = "";
    public ConnectionProfile? Profile { get; set; }
    /// <summary>Satt for mobiler/nettbrett (USB). Da er oppføringen ikke en lokal mappe.</summary>
    public string? DeviceId { get; set; }

    public Visibility SubVisibility => string.IsNullOrEmpty(Sub) ? Visibility.Collapsed : Visibility.Visible;
}
