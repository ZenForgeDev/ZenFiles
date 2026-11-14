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
    readonly Splitter _divider = new() { Visibility = Visibility.Collapsed };
    public ExplorerPane Active { get; private set; }

    public event Action<IFileSystem, string>? TerminalRequested;
    public event Action<ExplorerPane>? LocationChanged;
    public event Action<string>? FavoriteRequested;

    public ExplorerTab(IFileSystem fs, string startPath)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        // Høyre rute tar ikke plass før dobbel rute er slått på
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

        _left = new ExplorerPane(fs, startPath) { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Active = _left;
        Wire(_left);
        Children.Add(_left);

        _divider.Dragged += OnSplitDragged;
        _divider.ResetRequested += () => SetSplit(0.5);
        SetColumn(_divider, 1);
        Children.Add(_divider);
    }

    void Wire(ExplorerPane p)
    {
        p.Focused += x => Active = x;
        p.TerminalRequested += (fs, path) => TerminalRequested?.Invoke(fs, path);
        p.TransferRequested += OnTransfer;
        p.LocationChanged += x => LocationChanged?.Invoke(x);
        p.ViewChanged += x => LocationChanged?.Invoke(x);   // liste/ikoner endret: lagre økten på nytt
        p.DualPaneRequested += ToggleDual;
        p.FavoriteRequested += path => FavoriteRequested?.Invoke(path);
        p.HorizontalAlignment = HorizontalAlignment.Stretch;
        p.VerticalAlignment = VerticalAlignment.Stretch;
    }

    public bool IsDual => _right != null;

    public ExplorerPane Left => _left;
    public ExplorerPane? Right => _right;

    /// <summary>Slår på dobbel rute og åpner høyre rute i gitt mappe (brukes når økten gjenopprettes).</summary>
    public void OpenDual(string rightPath, bool rightGrid)
    {
        if (_right == null) ToggleDualCore(rightPath);
        if (_right == null) return;
        _right.GridMode = rightGrid;
        Active = _left;
    }

    public void ToggleDual() => ToggleDualCore(null);

    void ToggleDualCore(string? rightStart)
    {
        if (_right == null)
        {
            // Fjernserver til venstre -> lokal Nedlastinger til høyre; ellers samme mappe.
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            _right = _left.Fs.IsRemote
                ? new ExplorerPane(LocalFileSystem.Instance, downloads)
                : new ExplorerPane(LocalFileSystem.Instance, rightStart ?? _left.CurrentPath);
            Wire(_right);
            SetColumn(_right, 2);
            Children.Add(_right);
            SetSplit(0.5);
            _divider.Visibility = Visibility.Visible;
            Active = _right;
        }
        else
        {
            Children.Remove(_right);
            _right = null;
            ColumnDefinitions[2].Width = new GridLength(0);
            ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            _divider.Visibility = Visibility.Collapsed;
            Active = _left;
        }
    }

    /// <summary>x = skillelinjens posisjon i fanens koordinater.</summary>
    void OnSplitDragged(double x)
    {
        var total = ActualWidth - ColumnDefinitions[1].Width.Value;
        if (total <= 0) return;
        SetSplit((x - ColumnDefinitions[1].Width.Value / 2) / total);
    }

    void SetSplit(double leftFraction)
    {
        var f = Math.Clamp(leftFraction, 0.15, 0.85);
        ColumnDefinitions[0].Width = new GridLength(f, GridUnitType.Star);
        ColumnDefinitions[2].Width = new GridLength(1 - f, GridUnitType.Star);
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


/// <summary>Skillelinje mellom to ruter: dra for å endre størrelse, dobbeltklikk for 50/50.</summary>
public sealed class Splitter : Grid
{
    public event Action<double>? Dragged;
    public event Action? ResetRequested;
    bool _drag;

    public Splitter()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);   // gjør hele bredden klikkbar
        var line = new Border
        {
            Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.3,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128))
        };
        Children.Add(line);
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        PointerEntered += (_, _) => line.Opacity = 0.9;
        PointerExited += (_, _) => { if (!_drag) line.Opacity = 0.3; };
        PointerPressed += (_, e) => { _drag = CapturePointer(e.Pointer); };
        PointerMoved += (_, e) =>
        {
            if (!_drag) return;
            if (Parent is FrameworkElement host) Dragged?.Invoke(e.GetCurrentPoint(host).Position.X);
        };
        PointerReleased += (_, e) => { _drag = false; ReleasePointerCapture(e.Pointer); line.Opacity = 0.3; };
        PointerCaptureLost += (_, _) => { _drag = false; line.Opacity = 0.3; };
        DoubleTapped += (_, _) => ResetRequested?.Invoke();
    }
}
