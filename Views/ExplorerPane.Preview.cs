using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Storage.Streams;
using ZenFiles.Models;
using ZenFiles.Services;

namespace ZenFiles.Views;

/// <summary>Forhåndsvisning med mellomromstasten (som Hurtigtitt/QuickLook): et kort over fillisten med bilde, tekst, PDF, lyd eller video.</summary>
public sealed partial class ExplorerPane
{
    static readonly HashSet<string> PvImg = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".heic", ".avif" };
    static readonly HashSet<string> PvVid = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v" };
    static readonly HashSet<string> PvAud = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma", ".opus" };
    static readonly HashSet<string> PvText = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".yml", ".yaml", ".toml", ".cs", ".xaml",
        ".js", ".ts", ".py", ".ps1", ".bat", ".cmd", ".sh", ".html", ".htm", ".css", ".cpp", ".c", ".h", ".java", ".rs", ".go",
        ".sql", ".reg", ".iss", ".csproj", ".sln", ".gitignore", ".srt", ".manifest"
    };

    const long PvMaxBinary = 60L * 1024 * 1024;     // bilder og PDF
    const long PvMaxMediaRemote = 150L * 1024 * 1024;   // video/lyd fra mobil eller server lastes ned først
    const int PvMaxText = 128 * 1024;

    Grid? _pv;
    Grid? _pvBody;
    TextBlock? _pvTitle, _pvMeta;
    MediaPlayerElement? _pvPlayer;
    string? _pvTempDir;
    int _pvToken;

    bool IsPreviewOpen => _pv?.Visibility == Visibility.Visible;

    void TogglePreview()
    {
        if (IsPreviewOpen) { ClosePreview(); return; }
        var sel = SelectedItems;
        if (sel.Count != 1) return;
        EnsurePreview();
        _pv!.Visibility = Visibility.Visible;
        _ = ShowPreviewAsync(sel[0]);
    }

    void RefreshPreview()
    {
        var sel = SelectedItems;
        if (sel.Count == 1) _ = ShowPreviewAsync(sel[0]);
        else ClosePreview();
    }

    void ClosePreview()
    {
        _pvToken++;
        ResetPreviewBody();
        if (_pv != null) _pv.Visibility = Visibility.Collapsed;
        ActiveList.Focus(FocusState.Programmatic);
    }

    void DisposePreview()
    {
        _pvToken++;
        ResetPreviewBody();
    }

    void EnsurePreview()
    {
        if (_pv != null) return;
        const string xaml = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
      xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
      Visibility='Collapsed' Background='#A6000000'>
  <Border x:Name='Card' Margin='28' MaxWidth='1100' MaxHeight='820' CornerRadius='12' BorderThickness='1'
          Background='{ThemeResource SolidBackgroundFillColorBaseBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}'>
    <Grid>
      <Grid.RowDefinitions>
        <RowDefinition Height='Auto'/>
        <RowDefinition Height='*'/>
      </Grid.RowDefinitions>
      <Grid Padding='16,10,8,10' ColumnSpacing='12'>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='*'/>
          <ColumnDefinition Width='Auto'/>
        </Grid.ColumnDefinitions>
        <StackPanel>
          <TextBlock x:Name='Title' FontSize='15' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/>
          <TextBlock x:Name='Meta' FontSize='12' Opacity='0.6' TextTrimming='CharacterEllipsis'/>
        </StackPanel>
        <Button x:Name='CloseBtn' Grid.Column='1' Background='Transparent' BorderThickness='0' Padding='10,8' VerticalAlignment='Center'
                ToolTipService.ToolTip='Lukk (Esc eller mellomrom)'>
          <FontIcon Glyph='&#xE711;' FontSize='14'/>
        </Button>
      </Grid>
      <Grid x:Name='Body' Grid.Row='1' Margin='0,0,0,1'/>
    </Grid>
  </Border>
</Grid>";
        var root = (Grid)XamlReader.Load(xaml);
        _pvBody = (Grid)root.FindName("Body");
        _pvTitle = (TextBlock)root.FindName("Title");
        _pvMeta = (TextBlock)root.FindName("Meta");
        ((Button)root.FindName("CloseBtn")).Click += (_, _) => ClosePreview();
        ((Border)root.FindName("Card")).Tapped += (_, e) => e.Handled = true;   // klikk på selve kortet lukker ikke
        root.Tapped += (_, _) => ClosePreview();                                  // klikk på det mørke området utenfor lukker
        root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { ClosePreview(); e.Handled = true; }
        };
        Grid.SetRowSpan(root, 2);
        Grid.SetColumnSpan(root, 2);
        ListArea.Children.Add(root);
        _pv = root;
    }

    void ResetPreviewBody()
    {
        if (_pvPlayer != null)
        {
            try { _pvPlayer.MediaPlayer?.Pause(); } catch { }
            try { _pvPlayer.Source = null; } catch { }
            try { _pvPlayer.SetMediaPlayer(null); } catch { }
            _pvPlayer = null;
        }
        _pvBody?.Children.Clear();
        if (_pvTempDir != null)
        {
            try { Directory.Delete(_pvTempDir, true); } catch { }
            _pvTempDir = null;
        }
    }

    async Task ShowPreviewAsync(FileItem it)
    {
        int tok = ++_pvToken;
        ResetPreviewBody();
        _pvTitle!.Text = it.Name;
        _pvMeta!.Text = it.IsDrive ? "Stasjon" : it.IsDirectory ? "Mappe" : $"{it.TypeText} · {it.SizeText} · {it.ModifiedText}";

        if (it.IsDirectory || it.IsDrive) { SetPreview(InfoView(it, null)); return; }

        var ext = Path.GetExtension(it.Name);
        try
        {
            if (PvImg.Contains(ext))
            {
                if (it.Size > PvMaxBinary) { SetPreview(InfoView(it, "Bildet er for stort til forhåndsvisning.")); return; }
                var ras = await ReadAllAsync(it);
                if (tok != _pvToken) return;
                var bmp = new BitmapImage { DecodePixelWidth = 1800 };
                await bmp.SetSourceAsync(ras);
                if (tok != _pvToken) return;
                SetPreview(new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(12) });
            }
            else if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                if (it.Size > PvMaxBinary) { SetPreview(InfoView(it, "PDF-en er for stor til forhåndsvisning.")); return; }
                var ras = await ReadAllAsync(it);
                if (tok != _pvToken) return;
                var doc = await PdfDocument.LoadFromStreamAsync(ras);
                using var page = doc.GetPage(0);
                var outStream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(outStream, new PdfPageRenderOptions { DestinationWidth = 1100 });
                if (tok != _pvToken) return;
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(outStream);
                if (tok != _pvToken) return;
                _pvMeta.Text += $" · side 1 av {doc.PageCount}";
                SetPreview(new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(12) }
                });
            }
            else if (PvVid.Contains(ext) || PvAud.Contains(ext))
            {
                string path = it.FullPath;
                if (Fs.IsRemote)
                {
                    if (it.Size > PvMaxMediaRemote) { SetPreview(InfoView(it, "Filen er for stor til forhåndsvisning fra en ekstern enhet.")); return; }
                    SetBusy($"Laster ned {it.Name}…");
                    var dir = Path.Combine(Path.GetTempPath(), "ZenFiles", "preview-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    _pvTempDir = dir;
                    try { await Task.Run(() => Transfer.CopyAsync(Fs, it, LocalFileSystem.Instance, dir, null, CancellationToken.None)); }
                    finally { SetBusy(null); }
                    if (tok != _pvToken) { try { Directory.Delete(dir, true); } catch { } return; }
                    path = Path.Combine(dir, it.Name);
                }
                var player = new MediaPlayerElement
                {
                    AreTransportControlsEnabled = true, AutoPlay = true, Stretch = Stretch.Uniform,
                    Source = MediaSource.CreateFromUri(new Uri(path))
                };
                _pvPlayer = player;
                var host = new Grid { Margin = new Thickness(8) };
                if (PvAud.Contains(ext))
                    host.Children.Add(new FontIcon { Glyph = "", FontSize = 72, Opacity = 0.25, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                host.Children.Add(player);
                SetPreview(host);
            }
            else if (PvText.Contains(ext) || ext.Length == 0)
            {
                var (text, truncated, binary) = await ReadTextAsync(it);
                if (tok != _pvToken) return;
                if (binary) { SetPreview(InfoView(it, "Ser ut som en binærfil, så det vises ingen forhåndsvisning.")); return; }
                if (truncated) text += $"\n\n… (viser de første {PvMaxText / 1024} KB)";
                SetPreview(new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = text, FontFamily = new FontFamily("Consolas"), FontSize = 13,
                        IsTextSelectionEnabled = true, Margin = new Thickness(16, 8, 16, 12)
                    }
                });
            }
            else SetPreview(InfoView(it, null));
        }
        catch (Exception ex)
        {
            if (tok == _pvToken) SetPreview(InfoView(it, "Kunne ikke vise forhåndsvisning: " + ex.Message));
        }
    }

    void SetPreview(UIElement content)
    {
        _pvBody!.Children.Clear();
        _pvBody.Children.Add(content);
    }

    /// <summary>Leser hele filen (opptil grensen) til minnet, slik at også ikke-spolbare strømmer (SFTP) kan vises.</summary>
    async Task<IRandomAccessStream> ReadAllAsync(FileItem it)
    {
        await using var s = await Fs.OpenReadAsync(it.FullPath);
        var ms = new InMemoryRandomAccessStream();
        var w = ms.AsStreamForWrite();
        await s.CopyToAsync(w);
        await w.FlushAsync();
        ms.Seek(0);
        return ms;
    }

    async Task<(string text, bool truncated, bool binary)> ReadTextAsync(FileItem it)
    {
        await using var s = await Fs.OpenReadAsync(it.FullPath);
        var buf = new byte[PvMaxText + 1];
        int n = 0;
        while (n < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(n, buf.Length - n));
            if (r == 0) break;
            n += r;
        }
        bool truncated = n > PvMaxText;
        if (truncated) n = PvMaxText;
        for (int i = 0; i < Math.Min(n, 4096); i++)
            if (buf[i] == 0) return ("", false, true);
        var enc = new UTF8Encoding(false);
        int skip = n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0;
        return (enc.GetString(buf, skip, n - skip), truncated, false);
    }

    UIElement InfoView(FileItem it, string? note)
    {
        var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        panel.Children.Add(new FontIcon
        {
            Glyph = it.Glyph.Length > 0 ? it.Glyph : "", FontSize = 64, Foreground = it.IconBrush,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock { Text = it.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        if (!it.IsDrive)
            panel.Children.Add(new TextBlock { Text = it.IsDirectory ? it.ModifiedText : $"{it.SizeText} · {it.ModifiedText}", Opacity = 0.65, TextAlignment = TextAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = note ?? "Ingen forhåndsvisning for denne filtypen. Trykk Enter for å åpne den.",
            Opacity = 0.65, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 8, 0, 0)
        });
        return panel;
    }
}
