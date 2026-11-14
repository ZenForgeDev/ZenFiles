using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ZenFiles.Services;

namespace ZenFiles.Views;

/// <summary>En fane med ett eller to utforsker-paneler (dual pane).</summary>
public sealed class ExplorerTab : Grid, IDisposable
{
    readonly ExplorerPane _left;
    ExplorerPane? _right;
    readonly Border _divider = new() { Width = 1, Opacity = 0.25, Visibility = Visibility.Collapsed };
    public ExplorerPane Active { get; private set; }

    public event Action<IFileSystem, string>? TerminalRequested;
    public event Action<ExplorerPane>? LocationChanged;
    public event Action<string>? FavoriteRequested;

    public ExplorerTab(IFileSystem fs, string startPath)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Høyre rute tar ikke plass før dobbel rute er slått på
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

        _left = new ExplorerPane(fs, startPath) { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Active = _left;
        Wire(_left);
        Children.Add(_left);

        _divider.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
        SetColumn(_divider, 1);
        Children.Add(_divider);
    }

    void Wire(ExplorerPane p)
    {
        p.Focused += x => Active = x;
        p.TerminalRequested += (fs, path) => TerminalRequested?.Invoke(fs, path);
        p.TransferRequested += OnTransfer;
        p.LocationChanged += x => LocationChanged?.Invoke(x);
        p.DualPaneRequested += ToggleDual;
        p.FavoriteRequested += path => FavoriteRequested?.Invoke(path);
        p.HorizontalAlignment = HorizontalAlignment.Stretch;
        p.VerticalAlignment = VerticalAlignment.Stretch;
    }

    public bool IsDual => _right != null;

    public void ToggleDual()
    {
        if (_right == null)
        {
            // Fjernserver til venstre -> lokal Nedlastinger til høyre; ellers samme mappe.
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            _right = _left.Fs.IsRemote
                ? new ExplorerPane(LocalFileSystem.Instance, downloads)
                : new ExplorerPane(LocalFileSystem.Instance, _left.CurrentPath);
            Wire(_right);
            SetColumn(_right, 2);
            Children.Add(_right);
            ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            _divider.Visibility = Visibility.Visible;
            Active = _right;
        }
        else
        {
            Children.Remove(_right);
            _right = null;
            ColumnDefinitions[2].Width = new GridLength(0);
            _divider.Visibility = Visibility.Collapsed;
            Active = _left;
        }
    }

    async void OnTransfer(ExplorerPane src, bool move)
    {
        if (_right == null) ToggleDual();
        var dst = src == _left ? _right! : _left;
        var items = src.SelectedItems;
        if (items.Count == 0) return;
        await dst.ReceiveAsync(src.Fs, items, move, src);
    }

    public void Dispose()
    {
        (_left.Fs as IDisposable)?.Dispose();
    }
}
