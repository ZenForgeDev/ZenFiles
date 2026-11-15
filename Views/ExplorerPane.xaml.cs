using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;
using Windows.UI.Core;
using Microsoft.UI.Input;
using System.Collections.Concurrent;
using ZenFiles.Models;
using ZenFiles.Services;

namespace ZenFiles.Views;

public sealed partial class ExplorerPane : UserControl
{
    const string ThisPc = "Denne PC-en";

    public IFileSystem Fs { get; }
    public string CurrentPath { get; private set; } = "";

    ObservableCollection<FileItem> _items = new();
    List<FileItem> _all = new();
    readonly Stack<string> _back = new(), _fwd = new();
    string _sort = "name";
    bool _asc = true, _showHidden, _grid, _loaded;
    CancellationTokenSource? _thumbCts;

    // Dypt søk (Enter i søkefeltet)
    bool _searchMode, _searchRunning;
    List<FileItem> _folderAll = new();
    CancellationTokenSource? _searchCts;
    readonly ConcurrentQueue<SearchHit> _hitQueue = new();
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _hitTimer;
    string _searchInfo = "";

    /// <summary>Elementer som dras mellom ruter i samme app.</summary>
    static (ExplorerPane Pane, List<FileItem> Items)? _drag;

    public event Action<ExplorerPane>? Focused;
    public event Action<IFileSystem, string>? TerminalRequested;
    public event Action<ExplorerPane, bool>? TransferRequested;   // bool = flytt
    public event Action<ExplorerPane>? LocationChanged;
    public event Action? DualPaneRequested;
    public event Action<string>? FavoriteRequested;

    /// <summary>Lokale mapper i pågående intern dra-og-slipp (brukes av sidepanelet).</summary>
    public static List<string> DraggedLocalFolders() =>
        _drag is not { } d || d.Pane.Fs.IsRemote
            ? new List<string>()
            : d.Items.Where(i => i.IsDirectory && !i.IsDrive && i.FullPath.Length > 0).Select(i => i.FullPath).ToList();

    public static void ClearDrag() => _drag = null;

    /// <summary>Det som dras nå (kilde-filsystem, elementer og kilde-rute), eller null.</summary>
    public static (IFileSystem Fs, List<FileItem> Items, ExplorerPane Pane)? CurrentDrag() =>
        _drag is { } d ? (d.Pane.Fs, d.Items, d.Pane) : null;

    public string Title { get; private set; } = "";

    public ExplorerPane(IFileSystem fs, string startPath)
    {
        Fs = fs;
        InitializeComponent();
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => Focused?.Invoke(this)), true);
        ListArea.AddHandler(PointerPressedEvent, new PointerEventHandler(Area_PointerPressed), true);
        ListArea.PointerMoved += Area_PointerMoved;
        ListArea.PointerWheelChanged += Area_PointerWheel;
        ListArea.PointerReleased += Area_PointerEnded;
        ListArea.PointerCaptureLost += Area_PointerEnded;
        _ = NavigateAsync(startPath, push: false);
    }

    // ───────── Markeringsboks (dra med musa på tom plass) ─────────

    bool _marquee;
    double _mStartX, _mStartContentY;
    Windows.Foundation.Point _mLast;
    List<object> _mBase = new();
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _mTimer;

    ListViewBase ActiveList => _grid ? GridList : DetailsList;

    static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (FindScroll(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } r) return r;
        return null;
    }

    void Area_PointerPressed(object s, PointerRoutedEventArgs e)
    {
        var cp = e.GetCurrentPoint(ListArea);
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse || !cp.Properties.IsLeftButtonPressed) return;

        // Bare på tom plass: ikke på rader, rullefelt, knapper eller kolonneoverskriften
        for (var el = e.OriginalSource as DependencyObject; el != null && !ReferenceEquals(el, ListArea);
             el = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el))
            if (el is Microsoft.UI.Xaml.Controls.Primitives.SelectorItem
                   or Microsoft.UI.Xaml.Controls.Primitives.ScrollBar
                   or Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                   or Microsoft.UI.Xaml.Controls.Primitives.Thumb) return;
        var lv = ActiveList;
        var top = lv.TransformToVisual(ListArea).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        if (!_grid && cp.Position.Y < top) return;   // overskriften

        bool ctrl = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        _mBase = ctrl ? lv.SelectedItems.Cast<object>().ToList() : new List<object>();
        if (!ctrl) lv.SelectedItems.Clear();

        // Startpunktet lagres i innholdskoordinater, slik at boksen følger innholdet når listen ruller
        _mStartX = cp.Position.X;
        _mStartContentY = cp.Position.Y + (FindScroll(lv)?.VerticalOffset ?? 0);
        _mLast = cp.Position;
        _marquee = true;
        ListArea.CapturePointer(e.Pointer);

        _mTimer ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _mTimer.Interval = TimeSpan.FromMilliseconds(16);
        _mTimer.Tick -= Marquee_Tick;
        _mTimer.Tick += Marquee_Tick;
        _mTimer.Start();
    }

    void Area_PointerMoved(object s, PointerRoutedEventArgs e)
    {
        if (!_marquee) return;
        _mLast = e.GetCurrentPoint(ListArea).Position;
        UpdateMarquee();
    }

    // Rull med musehjulet mens du markerer
    void Area_PointerWheel(object s, PointerRoutedEventArgs e)
    {
        if (!_marquee) return;
        var sv = FindScroll(ActiveList);
        if (sv == null) return;
        int delta = e.GetCurrentPoint(ListArea).Properties.MouseWheelDelta;
        sv.ChangeView(null, Math.Clamp(sv.VerticalOffset - delta * 0.8, 0, sv.ScrollableHeight), null, true);
        e.Handled = true;
        UpdateMarquee();
    }

    // Rull automatisk når musepekeren er nær øvre eller nedre kant
    void Marquee_Tick(object? s, object e)
    {
        if (!_marquee) { _mTimer?.Stop(); return; }
        var lv = ActiveList;
        var sv = FindScroll(lv);
        if (sv != null)
        {
            var lb = lv.TransformToVisual(ListArea).TransformBounds(new Windows.Foundation.Rect(0, 0, lv.ActualWidth, lv.ActualHeight));
            const double zone = 36, maxSpeed = 22;
            double dy = 0;
            if (_mLast.Y < lb.Top + zone) dy = -maxSpeed * Math.Min(1, (lb.Top + zone - _mLast.Y) / zone);
            else if (_mLast.Y > lb.Bottom - zone) dy = maxSpeed * Math.Min(1, (_mLast.Y - (lb.Bottom - zone)) / zone);
            if (dy != 0)
                sv.ChangeView(null, Math.Clamp(sv.VerticalOffset + dy, 0, sv.ScrollableHeight), null, true);
        }
        UpdateMarquee();   // også etter rulling, når nye rader er laget
    }

    void UpdateMarquee()
    {
        var lv = ActiveList;
        var sv = FindScroll(lv);
        double w = ListArea.ActualWidth, h = ListArea.ActualHeight;
        double startY = _mStartContentY - (sv?.VerticalOffset ?? 0);
        double x2 = Math.Clamp(_mLast.X, 0, w), y2 = Math.Clamp(_mLast.Y, 0, h);
        double sy = Math.Clamp(startY, 0, h);
        var r = new Windows.Foundation.Rect(
            Math.Min(_mStartX, x2), Math.Min(sy, y2), Math.Abs(x2 - _mStartX), Math.Abs(y2 - sy));

        MarqueeRect.Visibility = r.Width > 3 || r.Height > 3 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(MarqueeRect, r.X);
        Canvas.SetTop(MarqueeRect, r.Y);
        MarqueeRect.Width = r.Width;
        MarqueeRect.Height = r.Height;

        if (lv.ItemsPanelRoot == null) return;
        var lb = lv.TransformToVisual(ListArea).TransformBounds(new Windows.Foundation.Rect(0, 0, lv.ActualWidth, lv.ActualHeight));

        var hits = new HashSet<object>(_mBase);
        foreach (var c in lv.ItemsPanelRoot.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.SelectorItem>())
        {
            var b = c.TransformToVisual(ListArea).TransformBounds(new Windows.Foundation.Rect(0, 0, c.ActualWidth, c.ActualHeight));
            bool inView = b.Bottom > lb.Top && b.Top < lb.Bottom;
            bool hit = b.X < r.Right && b.Right > r.X && b.Y < r.Bottom && b.Bottom > r.Y;
            if (inView && hit && lv.ItemFromContainer(c) is { } it) hits.Add(it);
        }

        foreach (var it in lv.SelectedItems.Cast<object>().ToList())
            if (!hits.Contains(it)) lv.SelectedItems.Remove(it);
        foreach (var it in hits)
            if (!lv.SelectedItems.Contains(it)) lv.SelectedItems.Add(it);
    }

    void Area_PointerEnded(object s, PointerRoutedEventArgs e)
    {
        if (!_marquee) return;
        _marquee = false;
        _mTimer?.Stop();
        MarqueeRect.Visibility = Visibility.Collapsed;
        ListArea.ReleasePointerCaptures();
        ActiveList.Focus(FocusState.Programmatic);
    }

    // ───────── Navigasjon ─────────

    public async Task NavigateAsync(string path, bool push = true)
    {
        _thumbCts?.Cancel();
        SetBusy("Laster…");
        try
        {
            var list = await Fs.ListAsync(path, _showHidden);
            if (push && _loaded) { _back.Push(CurrentPath); _fwd.Clear(); }
            _loaded = true;
            if (_searchMode) { EndSearchState(); SearchBox.Text = ""; }
            CurrentPath = path;
            _all = list;
            Title = ComputeTitle();
            TitleText.Text = Title;
            BuildCrumbs();
            HideEditor();
            Apply();
            LocationChanged?.Invoke(this);
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
            if (XamlRoot != null) await Dialogs.ErrorAsync(XamlRoot, "Kan ikke åpne mappen", ex.Message);
        }
        finally
        {
            SetBusy(null);
            UpdateNavButtons();
        }
    }

    public Task RefreshAsync() => NavigateAsync(CurrentPath, push: false);

    void UpdateNavButtons()
    {
        BackBtn.IsEnabled = _back.Count > 0;
        FwdBtn.IsEnabled = _fwd.Count > 0;
        UpBtn.IsEnabled = Fs.Parent(CurrentPath) != null;
    }

    async void Back_Click(object s, RoutedEventArgs e)
    {
        if (_back.Count == 0) return;
        _fwd.Push(CurrentPath);
        await NavigateAsync(_back.Pop(), push: false);
    }

    async void Forward_Click(object s, RoutedEventArgs e)
    {
        if (_fwd.Count == 0) return;
        _back.Push(CurrentPath);
        await NavigateAsync(_fwd.Pop(), push: false);
    }

    async void Up_Click(object s, RoutedEventArgs e)
    {
        var p = Fs.Parent(CurrentPath);
        if (p != null) await NavigateAsync(p);
    }

    async void Refresh_Click(object s, RoutedEventArgs e) => await RefreshAsync();

    async void PathBox_KeyDown(object s, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { HideEditor(); e.Handled = true; return; }
        if (e.Key != VirtualKey.Enter) return;
        var t = PathBox.Text.Trim();
        e.Handled = true;
        HideEditor();
        await NavigateAsync(t == ThisPc ? "" : t);
    }

    void PathBox_LostFocus(object s, RoutedEventArgs e) => HideEditor();
    void PathBar_DoubleTapped(object s, DoubleTappedRoutedEventArgs e) => FocusPathBox();

    /// <summary>Klikk på tom plass i stilinjen åpner tekstfeltet (klikk på en mappe i stien navigerer som før).</summary>
    void PathBar_Tapped(object s, TappedRoutedEventArgs e)
    {
        for (var el = e.OriginalSource as DependencyObject; el != null && !ReferenceEquals(el, PathBar);
             el = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el))
            if (el is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or TextBox) return;
        FocusPathBox();
    }

    void EditPath_Click(object s, RoutedEventArgs e) => FocusPathBox();

    public void FocusPathBox()
    {
        PathBox.Text = CurrentPath.Length == 0 && !Fs.IsRemote ? ThisPc : CurrentPath;
        CrumbScroll.Visibility = Visibility.Collapsed;
        EditPathBtn.Visibility = Visibility.Collapsed;
        PathBox.Visibility = Visibility.Visible;
        PathBox.Focus(FocusState.Programmatic);
        PathBox.SelectAll();
    }

    void HideEditor()
    {
        PathBox.Visibility = Visibility.Collapsed;
        CrumbScroll.Visibility = Visibility.Visible;
        EditPathBtn.Visibility = Visibility.Visible;
    }

    string ComputeTitle()
    {
        if (!Fs.IsRemote)
        {
            if (CurrentPath.Length == 0) return ThisPc;
            var n = Path.GetFileName(CurrentPath.TrimEnd('\\'));
            return string.IsNullOrEmpty(n) ? CurrentPath : n;
        }
        var t = CurrentPath.TrimEnd('/');
        var name = t.Length == 0 ? "" : t[(t.LastIndexOf('/') + 1)..];
        return name.Length == 0 ? Fs.DisplayName : name;
    }

    static readonly SolidColorBrush Blue = new(Windows.UI.Color.FromArgb(255, 61, 155, 255));

    void BuildCrumbs()
    {
        var list = new List<(string Label, string Path)>();
        if (!Fs.IsRemote)
        {
            list.Add((ThisPc, ""));
            if (CurrentPath.Length > 0)
            {
                var parts = CurrentPath.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                string acc = "";
                for (int i = 0; i < parts.Length; i++)
                {
                    acc = i == 0 ? parts[0] + "\\" : Path.Combine(acc, parts[i]);
                    list.Add((parts[i], acc));
                }
            }
        }
        else
        {
            list.Add((Fs.DisplayName, "/"));
            string acc = "";
            foreach (var part in CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                acc += "/" + part;
                list.Add((part, acc));
            }
        }

        Crumbs.Children.Clear();
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0)
                Crumbs.Children.Add(new FontIcon { Glyph = "\uE76C", FontSize = 9, Opacity = 0.45,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });

            bool last = i == list.Count - 1;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            content.Children.Add(new FontIcon { Glyph = i == 0 && !Fs.IsRemote ? "\uEDA2" : "\uE8B7", FontSize = 12, Foreground = Blue });
            content.Children.Add(new TextBlock { Text = list[i].Label, FontSize = 12,
                FontWeight = last ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal });

            var b = new Button
            {
                Content = content,
                Tag = list[i].Path,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 3, 6, 3),
                MinHeight = 0,
                CornerRadius = new CornerRadius(5)
            };
            b.Click += async (snd, _) => await NavigateAsync((string)((Button)snd).Tag);
            Crumbs.Children.Add(b);
        }

        CrumbScroll.UpdateLayout();
        CrumbScroll.ChangeView(CrumbScroll.ScrollableWidth, null, null);
    }

    void Dual_Click(object s, RoutedEventArgs e) => DualPaneRequested?.Invoke();

    void Row_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.MinHeight = 32;
        args.ItemContainer.Padding = new Thickness(8, 0, 8, 0);
        args.ItemContainer.Margin = new Thickness(4, 1, 4, 1);
        WireDropTarget(args.ItemContainer, args.Item);
    }

    void Grid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        WireDropTarget(args.ItemContainer, args.Item);

    // ───────── Visning ─────────

    void Search_TextChanged(object s, TextChangedEventArgs e)
    {
        if (_searchMode)
        {
            // Tømt felt avslutter søket; ellers beholdes treffene til du trykker Enter på nytt
            if (string.IsNullOrWhiteSpace(SearchBox.Text)) CloseSearch();
            return;
        }
        Apply();
    }

    public void FocusSearch()
    {
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    void Search_KeyDown(object s, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (_searchMode || SearchBox.Text.Length > 0) { CloseSearch(); ActiveList.Focus(FocusState.Programmatic); }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            _ = DeepSearchAsync(ctrl);
            e.Handled = true;
        }
    }

    void CloseSearch_Click(object s, RoutedEventArgs e) { CloseSearch(); ActiveList.Focus(FocusState.Programmatic); }

    /// <summary>Avslutter søket, tømmer feltet og viser mappen igjen.</summary>
    void CloseSearch()
    {
        var was = _searchMode;
        EndSearchState();
        if (was) _all = _folderAll;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";   // utløser Apply via TextChanged
        else Apply();
    }

    void EndSearchState()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _hitTimer?.Stop();
        while (_hitQueue.TryDequeue(out _)) { }
        _searchMode = false;
        _searchRunning = false;
        CloseSearchBtn.Visibility = Visibility.Collapsed;
        SetBusy(null);
    }

    async Task DeepSearchAsync(bool allDrives)
    {
        var q = SearchBox.Text?.Trim() ?? "";
        if (q.Length == 0) return;
        if (Fs.IsRemote) { Status.Text = "Dypt søk gjelder bare lokale mapper"; return; }
        if (CurrentPath.Length == 0) allDrives = true;   // «Denne PC-en» -> alle stasjoner

        _searchCts?.Cancel();
        while (_hitQueue.TryDequeue(out _)) { }
        if (!_searchMode) { _folderAll = _all; _searchMode = true; }
        var cts = _searchCts = new CancellationTokenSource();
        _all = new List<FileItem>();
        _items = new ObservableCollection<FileItem>();
        DetailsList.ItemsSource = _items;
        GridList.ItemsSource = _items;
        CloseSearchBtn.Visibility = Visibility.Visible;
        _searchRunning = true;
        _searchInfo = "";
        SetBusy(allDrives ? "Søker på alle stasjoner…" : "Søker i undermapper…");
        UpdateStatus();

        _hitTimer ??= DispatcherQueue.CreateTimer();
        _hitTimer.Interval = TimeSpan.FromMilliseconds(150);
        _hitTimer.Tick -= HitTimer_Tick;
        _hitTimer.Tick += HitTimer_Tick;
        _hitTimer.Start();

        SearchOutcome? outcome = null;
        try
        {
            outcome = await SearchService.RunAsync(allDrives ? null : CurrentPath, q, h => _hitQueue.Enqueue(h), cts.Token);
        }
        catch (OperationCanceledException) { return; }   // nytt søk eller avbrutt
        catch (Exception ex) { if (cts == _searchCts) _searchInfo = "Feil: " + ex.Message; }
        finally
        {
            if (cts == _searchCts)
            {
                _hitTimer?.Stop();
                DrainHits();
                _searchRunning = false;
                SetBusy(null);
            }
        }
        if (cts != _searchCts) return;
        if (outcome != null)
            _searchInfo = outcome.Source + (outcome.Limited ? $" · viser de første {SearchService.MaxHits}, snevre inn søket" : "");
        Apply();   // endelig sortering
    }

    void HitTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object a)
    {
        DrainHits();
        UpdateStatus();
    }

    void DrainHits()
    {
        int n = 0;
        while (n < 500 && _hitQueue.TryDequeue(out var h))
        {
            n++;
            var it = new FileItem
            {
                Name = h.Name, FullPath = h.Path, IsDirectory = h.IsDir, Size = h.Size,
                Modified = h.Ticks > 0 ? new DateTime(h.Ticks) : default,
                Where = Path.GetDirectoryName(h.Path) ?? ""
            };
            _all.Add(it);
            _items.Add(it);
        }
    }

    void ListToggle_Click(object s, RoutedEventArgs e) => SetGridMode(false);
    void GridToggle_Click(object s, RoutedEventArgs e) => SetGridMode(true);

    /// <summary>Sann = ikonvisning, usann = liste. Brukes også når økten gjenopprettes.</summary>
    public bool GridMode
    {
        get => _grid;
        set { if (value != _grid) SetGridMode(value); }
    }

    public event Action<ExplorerPane>? ViewChanged;

    void SetGridMode(bool grid)
    {
        _grid = grid;
        ListToggle.IsChecked = !grid;
        GridToggle.IsChecked = grid;
        GridList.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        DetailsList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        HeaderRow.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        Apply();
        ViewChanged?.Invoke(this);
    }

    async void HiddenToggle_Click(object s, RoutedEventArgs e)
    {
        _showHidden = ((ToggleMenuFlyoutItem)s).IsChecked;
        await RefreshAsync();
    }

    void Sort_Click(object s, RoutedEventArgs e)
    {
        var key = (string)((Button)s).Tag;
        if (_sort == key) _asc = !_asc; else { _sort = key; _asc = true; }
        Apply();
    }

    void Apply()
    {
        IEnumerable<FileItem> q = _all;
        var f = _searchMode ? null : SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(f))
            q = q.Where(i => i.Name.Contains(f, StringComparison.OrdinalIgnoreCase));

        IOrderedEnumerable<FileItem> Ord(IEnumerable<FileItem> src) => _sort switch
        {
            "date" => _asc ? src.OrderBy(i => i.Modified) : src.OrderByDescending(i => i.Modified),
            "size" => _asc ? src.OrderBy(i => i.Size) : src.OrderByDescending(i => i.Size),
            "type" => _asc ? src.OrderBy(i => i.TypeText) : src.OrderByDescending(i => i.TypeText),
            _ => _asc ? src.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                      : src.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
        };

        var sorted = Ord(q.Where(i => i.IsDirectory)).Concat(Ord(q.Where(i => !i.IsDirectory))).ToList();
        _items = new ObservableCollection<FileItem>(sorted);
        DetailsList.ItemsSource = _items;
        GridList.ItemsSource = _items;
        UpdateHeaders();
        UpdateStatus();
        if (_grid) _ = LoadThumbnailsAsync(sorted);
    }

    void UpdateHeaders()
    {
        string Arrow(string k) => _sort == k ? (_asc ? "  ▴" : "  ▾") : "";
        NameHdr.Content = "Navn" + Arrow("name");
        DateHdr.Content = "Endret" + Arrow("date");
        SizeHdr.Content = "Størrelse" + Arrow("size");
        TypeHdr.Content = "Type" + Arrow("type");
    }

    void UpdateStatus()
    {
        var sel = SelectedItems;
        if (_searchMode)
        {
            Status.Text = sel.Count == 1
                ? sel[0].FullPath
                : $"{_items.Count} treff" + (_searchRunning ? " …" : _searchInfo.Length > 0 ? " · " + _searchInfo : "");
            return;
        }
        var text = sel.Count == 0 ? $"{_items.Count} elementer" : $"{sel.Count} av {_items.Count} valgt";
        if (sel.Count > 0 && sel.All(i => !i.IsDirectory)) text += $" ({FileItem.Format(sel.Sum(i => i.Size))})";

        if (!Fs.IsRemote && CurrentPath.Length > 0)
        {
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(CurrentPath)!);
                text += $", {FileItem.Format(d.AvailableFreeSpace)} ledig";
            }
            catch { }
        }
        Status.Text = text;
    }

    void SetBusy(string? text)
    {
        BusyPanel.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        BusyText.Text = text ?? "";
    }

    async Task LoadThumbnailsAsync(List<FileItem> items)
    {
        _thumbCts?.Cancel();
        var cts = _thumbCts = new CancellationTokenSource();
        if (Fs is IThumbnailSource ts)
        {
            foreach (var it in items.Where(i => i.IsMedia && i.Thumbnail == null))
            {
                if (cts.IsCancellationRequested) return;
                try
                {
                    var bytes = await ts.GetThumbnailAsync(it.FullPath);
                    if (bytes == null || cts.IsCancellationRequested) continue;
                    using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    await ras.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
                    ras.Seek(0);
                    var bmp = new BitmapImage { DecodePixelWidth = 96 };
                    await bmp.SetSourceAsync(ras);
                    it.Thumbnail = bmp;
                }
                catch { }
            }
            return;
        }
        if (Fs.IsRemote) return;
        foreach (var it in items.Where(i => i.IsMedia && i.Thumbnail == null))
        {
            if (cts.IsCancellationRequested) return;
            try
            {
                var sf = await StorageFile.GetFileFromPathAsync(it.FullPath);
                using var th = await sf.GetThumbnailAsync(ThumbnailMode.SingleItem, 96, ThumbnailOptions.UseCurrentScale);
                if (th == null || cts.IsCancellationRequested) continue;
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(th);
                it.Thumbnail = bmp;
            }
            catch { /* hopp over filer uten miniatyr */ }
        }
    }

    // ───────── Utvalg og åpning ─────────

    public List<FileItem> SelectedItems =>
        (_grid ? GridList.SelectedItems : DetailsList.SelectedItems).Cast<FileItem>().ToList();

    void List_SelectionChanged(object s, SelectionChangedEventArgs e) => UpdateStatus();

    async void List_DoubleTapped(object s, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem it) await OpenAsync(it);
    }

    async Task OpenAsync(FileItem it)
    {
        try
        {
            if (it.IsDirectory) { await NavigateAsync(it.FullPath); return; }

            if (!Fs.IsRemote)
            {
                Process.Start(new ProcessStartInfo(it.FullPath) { UseShellExecute = true });
                return;
            }

            // SFTP: last ned til temp og åpne
            var dir = Path.Combine(Path.GetTempPath(), "ZenFiles", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            SetBusy($"Laster ned {it.Name}…");
            await Task.Run(() => Transfer.CopyAsync(Fs, it, LocalFileSystem.Instance, dir, null, CancellationToken.None));
            Process.Start(new ProcessStartInfo(Path.Combine(dir, it.Name)) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(XamlRoot, "Kunne ikke åpne", ex.Message);
        }
        finally { SetBusy(null); }
    }

    async void List_KeyDown(object s, KeyRoutedEventArgs e)
    {
        var sel = SelectedItems;
        switch (e.Key)
        {
            case VirtualKey.Enter when sel.Count == 1: await OpenAsync(sel[0]); e.Handled = true; break;
            case VirtualKey.Delete: await DeleteSelectedAsync(); e.Handled = true; break;
            case VirtualKey.F2: await RenameSelectedAsync(); e.Handled = true; break;
            case VirtualKey.F5: TransferRequested?.Invoke(this, false); e.Handled = true; break;
            case VirtualKey.F6: TransferRequested?.Invoke(this, true); e.Handled = true; break;
            case VirtualKey.Back: Up_Click(this, e); e.Handled = true; break;
        }
    }

    // ───────── Kontekstmeny ─────────

    void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var lv = (ListViewBase)sender;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem it && !lv.SelectedItems.Contains(it))
        {
            lv.SelectedItems.Clear();
            lv.SelectedItems.Add(it);
        }

        var sel = SelectedItems;
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, Symbol icon, Action act, bool enabled = true)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon), IsEnabled = enabled };
            mi.Click += (_, _) => act();
            return mi;
        }

        if (sel.Count == 1) menu.Items.Add(Item("Åpne", Symbol.OpenFile, async () => await OpenAsync(sel[0])));
        if (_searchMode && sel.Count == 1 && sel[0].Where.Length > 0)
        {
            var parent = sel[0].Where;
            menu.Items.Add(Item("Åpne overordnet mappe", Symbol.OpenLocal, async () => await NavigateAsync(parent)));
        }
        menu.Items.Add(Item("Kopier til andre rute (F5)", Symbol.Copy, () => TransferRequested?.Invoke(this, false), sel.Count > 0));
        menu.Items.Add(Item("Flytt til andre rute (F6)", Symbol.MoveToFolder, () => TransferRequested?.Invoke(this, true), sel.Count > 0));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Gi nytt navn (F2)", Symbol.Rename, async () => await RenameSelectedAsync(), sel.Count == 1));
        menu.Items.Add(Item("Slett (Del)", Symbol.Delete, async () => await DeleteSelectedAsync(), sel.Count > 0));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Ny mappe", Symbol.NewFolder, async () => await NewFolderAsync()));
        var colorDirs = sel.Where(i => i.IsDirectory && !i.IsDrive && !i.Remote).Select(i => i.FullPath).ToList();
        if (!Fs.IsRemote && colorDirs.Count > 0) menu.Items.Add(BuildFolderColorMenu(colorDirs));
        menu.Items.Add(Item("Kopier sti", Symbol.Link, () => CopyPath(sel)));
        if (!Fs.IsRemote)
        {
            var favPath = sel.Count == 1 && sel[0].IsDirectory ? sel[0].FullPath : CurrentPath;
            menu.Items.Add(Item("Legg til i favoritter", Symbol.Favorite, () => FavoriteRequested?.Invoke(favPath), favPath.Length > 0));
        }
        menu.Items.Add(Item("Åpne terminal her", Symbol.Admin, () => TerminalRequested?.Invoke(Fs, TerminalFolder(sel))));
        menu.ShowAt(lv, e.GetPosition(lv));
    }

    MenuFlyoutSubItem BuildFolderColorMenu(List<string> dirs)
    {
        var sub = new MenuFlyoutSubItem { Text = "Mappefarge", Icon = new FontIcon { Glyph = "\uE790" } };   // palett
        foreach (var sw in FolderColors.Swatches)
        {
            var hex = sw.Hex;
            var mi = new MenuFlyoutItem
            {
                Text = sw.Name,
                Icon = new FontIcon { Glyph = "\uEA3B", Foreground = new SolidColorBrush(FolderColors.Parse(hex)) }   // fylt sirkel
            };
            mi.Click += (_, _) => FolderColors.Set(dirs, hex);
            sub.Items.Add(mi);
        }
        var custom = new MenuFlyoutItem { Text = "Egendefinert…", Icon = new FontIcon { Glyph = "\uE771" } };
        custom.Click += async (_, _) =>
        {
            var hex = await Dialogs.PickColorAsync(XamlRoot, "Velg mappefarge", FolderColors.ToHex(FolderColors.Resolve(dirs[0])));
            if (hex != null) FolderColors.Set(dirs, hex);
        };
        sub.Items.Add(custom);
        sub.Items.Add(new MenuFlyoutSeparator());
        var reset = new MenuFlyoutItem { Text = "Tilbakestill (følg standard)", Icon = new FontIcon { Glyph = "\uE777" } };
        reset.Click += (_, _) => FolderColors.Set(dirs, null);
        sub.Items.Add(reset);
        return sub;
    }

    string TerminalFolder(List<FileItem> sel) =>
        sel.Count == 1 && sel[0].IsDirectory ? sel[0].FullPath : CurrentPath;

    void CopyPath(List<FileItem> sel)
    {
        var text = sel.Count == 0 ? CurrentPath : string.Join(Environment.NewLine, sel.Select(i => i.FullPath));
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    // ───────── Filhandlinger ─────────

    async void NewFolder_Click(object s, RoutedEventArgs e) => await NewFolderAsync();
    async void Rename_Click(object s, RoutedEventArgs e) => await RenameSelectedAsync();
    async void Delete_Click(object s, RoutedEventArgs e) => await DeleteSelectedAsync();
    void Copy_Click(object s, RoutedEventArgs e) => TransferRequested?.Invoke(this, false);
    void Move_Click(object s, RoutedEventArgs e) => TransferRequested?.Invoke(this, true);
    void Terminal_Click(object s, RoutedEventArgs e) => TerminalRequested?.Invoke(Fs, TerminalFolder(SelectedItems));

    async Task NewFolderAsync()
    {
        if (CurrentPath.Length == 0 && !Fs.IsRemote) return;
        var name = await Dialogs.PromptAsync(XamlRoot, "Ny mappe", "Ny mappe", "Opprett");
        if (name == null) return;
        try
        {
            await Fs.CreateDirectoryAsync(Fs.Combine(CurrentPath, name));
            await RefreshAsync();
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(XamlRoot, "Kunne ikke opprette mappen", ex.Message); }
    }

    async Task RenameSelectedAsync()
    {
        var sel = SelectedItems;
        if (sel.Count != 1 || sel[0].IsDrive) return;
        var name = await Dialogs.PromptAsync(XamlRoot, "Gi nytt navn", sel[0].Name, "Endre");
        if (name == null || name == sel[0].Name) return;
        try
        {
            await Fs.RenameAsync(sel[0].FullPath, Fs.Combine(CurrentPath, name));
            await RefreshAsync();
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(XamlRoot, "Kunne ikke endre navn", ex.Message); }
    }

    public async Task DeleteSelectedAsync()
    {
        var sel = SelectedItems.Where(i => !i.IsDrive).ToList();
        if (sel.Count == 0) return;

        var msg = sel.Count == 1 ? $"Slette «{sel[0].Name}»?" : $"Slette {sel.Count} elementer?";
        msg += Fs.IsRemote ? " Dette kan ikke angres." : " De flyttes til papirkurven.";
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Slett", msg, "Slett")) return;

        try
        {
            SetBusy("Sletter…");
            foreach (var it in sel) await Fs.DeleteAsync(it.FullPath, it.IsDirectory);
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(XamlRoot, "Kunne ikke slette", ex.Message); }
        finally { SetBusy(null); await RefreshAsync(); }
    }

    // ───────── Overføring (kopier/flytt/dra-og-slipp) ─────────

    public async Task ReceiveAsync(IFileSystem srcFs, IEnumerable<FileItem> items, bool move, ExplorerPane? srcPane, string? dstDir = null)
    {
        dstDir ??= CurrentPath;
        if (dstDir.Length == 0 && !Fs.IsRemote)
        {
            await Dialogs.ErrorAsync(XamlRoot, "Velg en mappe", "Åpne en stasjon eller mappe i målruten først.");
            return;
        }

        var list = FilterTransferable(srcFs, items, dstDir);
        if (list.Count == 0) return;

        var progress = new Progress<string>(n => SetBusy($"Overfører {n}…"));
        try
        {
            SetBusy("Overfører…");
            await Task.Run(async () =>
            {
                foreach (var it in list)
                {
                    // Flytting innen samme filsystem: gi nytt navn er raskt og trygt. Feiler det (f.eks. mellom stasjoner), kopieres og slettes.
                    if (move && ReferenceEquals(srcFs, Fs))
                    {
                        var t = Fs.Combine(dstDir, it.Name);
                        try
                        {
                            if (!await Fs.ExistsAsync(t)) { await Fs.RenameAsync(it.FullPath, t); continue; }
                        }
                        catch { }
                    }
                    await Transfer.CopyAsync(srcFs, it, Fs, dstDir, progress, CancellationToken.None);
                    if (move) await srcFs.DeleteAsync(it.FullPath, it.IsDirectory);
                }
            });
        }
        catch (Exception ex) { await Dialogs.ErrorAsync(XamlRoot, "Overføringen feilet", ex.Message); }
        finally
        {
            SetBusy(null);
            await RefreshAsync();
            if (move && srcPane != null && srcPane != this) await srcPane.RefreshAsync();
        }
    }

    void List_DragStarting(object s, DragItemsStartingEventArgs e)
    {
        var dragged = e.Items.Cast<FileItem>().ToList();
        _drag = (this, dragged);
        // Copy|Move: Utforsker velger selv (flytter på samme stasjon, kopierer til en annen; Ctrl/Shift overstyrer).
        // Internt i ZenFiles godtar slippmålene bare Copy og spør alltid via menyen.
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        e.Data.SetData("ZenFiles.Items", "1");

        // Lokale filer kan også slippes utenfor appen (skrivebordet, Utforsker …).
        // Filene hentes først når noen faktisk slipper dem et sted som ber om dem.
        if (Fs.IsRemote) return;
        var paths = dragged.Where(i => !i.IsDrive && i.FullPath.Length > 0).Select(i => (i.FullPath, i.IsDirectory)).ToList();
        if (paths.Count == 0) return;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var def = request.GetDeferral();
            try
            {
                var list = new List<IStorageItem>();
                foreach (var (path, isDir) in paths)
                {
                    try
                    {
                        list.Add(isDir ? await StorageFolder.GetFolderFromPathAsync(path)
                                       : await StorageFile.GetFileFromPathAsync(path));
                    }
                    catch { /* skjulte/låste filer hoppes over */ }
                }
                request.SetData(list);
            }
            finally { def.Complete(); }
        });
    }

    /// <summary>Etter slipp utenfor appen: ble filene flyttet, oppdater mappen så de forsvinner fra listen.</summary>
    async void List_DragCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        if (e.DropResult == DataPackageOperation.Move) await RefreshAsync();
    }

    /// <summary>Fjerner det som ikke gir mening å overføre (stasjoner, ting som allerede ligger i målmappen, mappen selv).</summary>
    List<FileItem> FilterTransferable(IFileSystem srcFs, IEnumerable<FileItem> items, string dstDir)
    {
        var dst = dstDir.TrimEnd('\\', '/');
        var list = items.Where(i => !i.IsDrive && i.FullPath.TrimEnd('\\', '/') != dst).ToList();
        if (ReferenceEquals(srcFs, Fs))
            list = list.Where(i => srcFs.Parent(i.FullPath)?.TrimEnd('\\', '/') != dst).ToList();
        return list;
    }

    static bool IsOurDrag(DragEventArgs e) => e.DataView.Contains("ZenFiles.Items") && _drag != null;

    void Pane_DragOver(object s, DragEventArgs e)
    {
        e.AcceptedOperation = IsOurDrag(e) || e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy : DataPackageOperation.None;
    }

    async void Pane_Drop(object s, DragEventArgs e) => await HandleDropAsync(e, CurrentPath);

    // Mapper i listen er egne slippmål
    void WireDropTarget(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem c, object? item)
    {
        c.AllowDrop = item is FileItem { IsDirectory: true, IsDrive: false };
        if (c.Tag is not "dt") { c.Tag = "dt"; c.DragOver += Row_DragOver; c.Drop += Row_Drop; }
    }

    static FileItem? RowItem(object s) => (s as ContentControl)?.Content as FileItem;

    void Row_DragOver(object s, DragEventArgs e)
    {
        if (RowItem(s) is not { IsDirectory: true } it) return;
        e.Handled = true;
        bool ok = IsOurDrag(e) || e.DataView.Contains(StandardDataFormats.StorageItems);
        if (ok && _drag is { } d && d.Items.Any(i => i.FullPath == it.FullPath)) ok = false;   // ikke på seg selv
        e.AcceptedOperation = ok ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (ok) e.DragUIOverride.Caption = $"Slipp i «{it.Name}»";
    }

    async void Row_Drop(object s, DragEventArgs e)
    {
        if (RowItem(s) is not { IsDirectory: true } it) return;
        e.Handled = true;
        await HandleDropAsync(e, it.FullPath);
    }

    /// <summary>Spør alltid «Flytt hit / Kopier hit» før noe skjer, så ingenting havner feil ved uhell.</summary>
    async Task HandleDropAsync(DragEventArgs e, string dstDir)
    {
        var def = e.GetDeferral();
        var pos = e.GetPosition(Layout);
        IFileSystem? srcFs = null;
        List<FileItem>? items = null;
        ExplorerPane? srcPane = null;
        try
        {
            if (IsOurDrag(e))
            {
                var d = _drag!.Value;
                srcFs = d.Pane.Fs; items = d.Items; srcPane = d.Pane;
            }
            else if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var si = await e.DataView.GetStorageItemsAsync();
                srcFs = LocalFileSystem.Instance;
                items = si.Select(x => new FileItem
                {
                    Name = x.Name, FullPath = x.Path, IsDirectory = x.IsOfType(StorageItemTypes.Folder)
                }).ToList();
            }
        }
        finally { def.Complete(); }

        _drag = null;
        if (items == null || srcFs == null) return;

        if (dstDir.Length == 0 && !Fs.IsRemote)
        {
            await Dialogs.ErrorAsync(XamlRoot, "Velg en mappe", "Åpne en stasjon eller mappe i målruten først.");
            return;
        }

        var list = FilterTransferable(srcFs, items, dstDir);
        if (list.Count == 0) return;

        var target = dstDir.TrimEnd('\\', '/');
        var name = target.Split('\\', '/').LastOrDefault() ?? target;
        if (string.IsNullOrEmpty(name)) name = target;

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = list.Count == 1 ? $"«{list[0].Name}» til «{name}»" : $"{list.Count} elementer til «{name}»",
            IsEnabled = false
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        var src = srcFs; var pane = srcPane;
        var move = new MenuFlyoutItem { Text = "Flytt hit", Icon = new SymbolIcon(Symbol.MoveToFolder) };
        move.Click += async (_, _) => await ReceiveAsync(src, list, true, pane, dstDir);
        var copy = new MenuFlyoutItem { Text = "Kopier hit", Icon = new SymbolIcon(Symbol.Copy) };
        copy.Click += async (_, _) => await ReceiveAsync(src, list, false, pane, dstDir);
        menu.Items.Add(move);
        menu.Items.Add(copy);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Avbryt", Icon = new SymbolIcon(Symbol.Cancel) });
        menu.ShowAt(Layout, pos);
    }
}
