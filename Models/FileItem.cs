using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace ZenFiles.Models;

public sealed class FileItem : INotifyPropertyChanged
{
    static readonly HashSet<string> Img = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".avif" };
    static readonly HashSet<string> Vid = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv" };
    static readonly HashSet<string> Aud = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac" };
    static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".xaml", ".js", ".ts", ".py", ".json", ".xml", ".html", ".css", ".sh", ".ps1", ".cpp", ".h", ".md", ".yml", ".yaml" };

    static readonly HashSet<string> Arc = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso" };
    static readonly System.Globalization.CultureInfo Nb = System.Globalization.CultureInfo.GetCultureInfo("nb-NO");

    static SolidColorBrush? _amber, _gray;

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsDrive { get; set; }
    /// <summary>Element på en ekstern server (SFTP). Slike mapper får ikke egen mappefarge.</summary>
    public bool Remote { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    /// <summary>Mappen elementet ligger i. Settes bare for søkeresultater, ellers tom.</summary>
    public string Where { get; set; } = "";
    public Visibility WhereVisibility => Where.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string TipText => Where.Length > 0 ? FullPath : Name;

    string Ext => IsDirectory ? "" : Path.GetExtension(Name);
    public bool IsMedia => !IsDirectory && (Img.Contains(Ext) || Vid.Contains(Ext));

    public string Category =>
        Img.Contains(Ext) ? "image" :
        Vid.Contains(Ext) ? "video" :
        Aud.Contains(Ext) ? "audio" :
        Code.Contains(Ext) ? "code" :
        Arc.Contains(Ext) ? "archive" : "other";

    public string Glyph =>
        IsDrive ? "" :
        IsDirectory ? "" :
        Img.Contains(Ext) ? "" :
        Vid.Contains(Ext) ? "" :
        Aud.Contains(Ext) ? "" :
        Code.Contains(Ext) ? "" : "";

    public Brush IconBrush => IsDirectory || IsDrive
        ? _amber ??= new SolidColorBrush(Color.FromArgb(255, 255, 200, 61))
        : _gray ??= new SolidColorBrush(Color.FromArgb(255, 154, 160, 166));

    public string TypeText =>
        IsDrive ? "Stasjon" :
        IsDirectory ? "Mappe" :
        string.IsNullOrEmpty(Ext) ? "Fil" : Ext.TrimStart('.').ToUpperInvariant() + "-fil";

    public string SizeText => IsDrive ? "" : IsDirectory ? "--" : Format(Size);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("d. MMM yyyy HH:mm", Nb);

    BitmapImage? _thumb;
    public BitmapImage? Thumbnail
    {
        get => _thumb;
        set
        {
            _thumb = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlyphVisibility)));
        }
    }
    public Visibility ThumbVisibility => _thumb != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => _thumb == null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string Format(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }
}
