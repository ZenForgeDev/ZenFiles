using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using ZenFiles.Models;
using ZenFiles.Services;
using ZenFiles.Views;

namespace ZenFiles;

public sealed partial class MainWindow : Window
{
    readonly List<ConnectionProfile> _profiles = ConnectionStore.Load();
    readonly ObservableCollection<SidebarEntry> _quick = new(), _drives = new(), _ssh = new();

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    bool _initSwitch, _syncing, _loadingFav;
    readonly ToggleSwitch _defaultSwitch = new()
    {
        Header = "Standard filbehandler",
        OnContent = "På (erstatter Utforsker)",
        OffContent = "Av"
    };

    public MainWindow(string? startPath = null)
    {
        InitializeComponent();
        Title = "ZenFiles";
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ZenFiles.ico")); } catch { }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        SystemBackdrop = new MicaBackdrop();
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;   // ingen «Ctrl+T»-tooltips
        AppWindow.Resize(new SizeInt32(1440, 880));

        QuickList.ItemsSource = _quick;
        DriveList.ItemsSource = _drives;
        SshList.ItemsSource = _ssh;
        _quick.CollectionChanged += (_, _) => { if (!_loadingFav) SaveFavorites(); };
        BuildSidebar();

        AddAccel(VirtualKey.T, VirtualKeyModifiers.Control, () => NewExplorerTab(Home));
        AddAccel(VirtualKey.W, VirtualKeyModifiers.Control, CloseCurrentTab);
        AddAccel(VirtualKey.D, VirtualKeyModifiers.Control, ToggleDual);
        AddAccel(VirtualKey.L, VirtualKeyModifiers.Control, () => CurrentExplorer?.Active.FocusPathBox());
        AddAccel(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => OpenTerminalTab(ShellCommands.LocalShell(), "Terminal"));

        try
        {
            _initSwitch = true;
            _defaultSwitch.IsOn = DefaultFileManager.IsEnabled();
        }
        finally { _initSwitch = false; }
        _defaultSwitch.Toggled += DefaultSwitch_Toggled;

        var panel = new StackPanel { Spacing = 8, Width = 270 };
        panel.Children.Add(new TextBlock { Text = "Innstillinger", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(_defaultSwitch);
        panel.Children.Add(new TextBlock
        {
            Text = "Mapper og Win+E åpnes i ZenFiles i stedet for Utforsker. Kan angres når som helst.",
            FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap
        });
        SettingsBtn.Flyout = new Flyout { Content = panel };

        NewExplorerTab(string.IsNullOrEmpty(startPath) ? Home : startPath);
    }

    async void DefaultSwitch_Toggled(object s, RoutedEventArgs e)
    {
        if (_initSwitch) return;
        bool wantOn = _defaultSwitch.IsOn;

        void Revert()
        {
            _initSwitch = true;
            _defaultSwitch.IsOn = !wantOn;
            _initSwitch = false;
        }

        try
        {
            if (wantOn)
            {
                var exe = Environment.ProcessPath ?? "";
                var msg =
                    "Mapper (og Win+E) vil åpnes i ZenFiles i stedet for Windows Utforsker. Dette endrer registeret for din bruker " +
                    "(ingen administrator), og de opprinnelige verdiene sikkerhetskopieres.\n\n" +
                    "Programmet må bli liggende her: " + exe + "\n\n" +
                    "Angre: slå bryteren av, eller kjør «ZenFiles.exe --restore-default». " +
                    "Apper som starter explorer.exe direkte (f.eks. «Vis i mappe» med utvalgt fil) kan fortsatt åpne Utforsker.";
                if (!await Dialogs.ConfirmAsync(Root.XamlRoot, "Bruke ZenFiles som standard filbehandler?", msg, "Aktiver"))
                {
                    Revert();
                    return;
                }
                DefaultFileManager.Enable();
            }
            else
            {
                DefaultFileManager.Disable();
            }
        }
        catch (Exception ex)
        {
            Revert();
            await Dialogs.ErrorAsync(Root.XamlRoot, "Kunne ikke endre standard filbehandler", ex.Message);
        }
    }

    // ───────── Sidepanel ─────────

    void BuildSidebar()
    {
        _loadingFav = true;
        _quick.Clear();
        void Q(string label, string glyph, Environment.SpecialFolder f)
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) _quick.Add(new SidebarEntry { Label = label, Glyph = glyph, Path = p });
        }
        _quick.Add(new SidebarEntry { Label = "Hjem", Glyph = "", Path = Home });
        Q("Skrivebord", "", Environment.SpecialFolder.DesktopDirectory);
        Q("Dokumenter", "", Environment.SpecialFolder.MyDocuments);
        _quick.Add(new SidebarEntry { Label = "Nedlastinger", Glyph = "", Path = System.IO.Path.Combine(Home, "Downloads") });
        Q("Bilder", "", Environment.SpecialFolder.MyPictures);
        Q("Musikk", "", Environment.SpecialFolder.MyMusic);
        Q("Videoer", "", Environment.SpecialFolder.MyVideos);

        var saved = FavoritesStore.Load();
        if (saved != null)
        {
            _quick.Clear();
            foreach (var f in saved)
                if (!string.IsNullOrEmpty(f.Path))
                    _quick.Add(new SidebarEntry { Label = f.Label, Glyph = f.Glyph, Path = f.Path });
        }
        _loadingFav = false;

        _drives.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            var label = string.IsNullOrEmpty(d.VolumeLabel) ? "Lokal disk" : d.VolumeLabel;
            _drives.Add(new SidebarEntry
            {
                Label = $"{label} ({d.Name.TrimEnd('\\')})",
                Glyph = "",
                Path = d.Name,
                Sub = $"{FileItem.Format(d.TotalFreeSpace)} ledig av {FileItem.Format(d.TotalSize)}"
            });
        }

        RefreshSsh();
    }

    void RefreshSsh()
    {
        _ssh.Clear();
        foreach (var p in _profiles)
            _ssh.Add(new SidebarEntry { Label = p.Name, Glyph = "", Sub = $"{p.User}@{p.Host}:{p.Port}", Profile = p });
        SshEmpty.Visibility = _ssh.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SaveFavorites() =>
        FavoritesStore.Save(_quick.Select(q => new FavoriteEntry { Label = q.Label, Glyph = q.Glyph, Path = q.Path }));

    void AddFavorite(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var norm = path.TrimEnd('\\');
        if (_quick.Any(q => string.Equals(q.Path.TrimEnd('\\'), norm, StringComparison.OrdinalIgnoreCase))) return;
        var label = System.IO.Path.GetFileName(norm);
        if (string.IsNullOrEmpty(label)) label = path;
        _quick.Add(new SidebarEntry { Label = label, Glyph = "\uE8B7", Path = path });
        SyncSidebar();
    }

    void Quick_DragOver(object s, DragEventArgs e)
    {
        bool fromPane = e.DataView.Contains("ZenFiles.Items") && ExplorerPane.DraggedLocalFolders().Count > 0;
        if (fromPane || e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = "Legg til i favoritter";
        }
    }

    async void Quick_Drop(object s, DragEventArgs e)
    {
        var def = e.GetDeferral();
        try
        {
            var paths = new List<string>();
            if (e.DataView.Contains("ZenFiles.Items"))
                paths.AddRange(ExplorerPane.DraggedLocalFolders());
            else if (e.DataView.Contains(StandardDataFormats.StorageItems))
                foreach (var si in await e.DataView.GetStorageItemsAsync())
                    if (si.IsOfType(StorageItemTypes.Folder)) paths.Add(si.Path);
            foreach (var p in paths) AddFavorite(p);
            ExplorerPane.ClearDrag();
        }
        finally { def.Complete(); }
    }

    void Quick_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SidebarEntry entry) return;
        e.Handled = true;

        MenuFlyoutItem Item(string text, string glyph, Action act, bool enabled = true)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            mi.Click += (_, _) => act();
            return mi;
        }

        int idx = _quick.IndexOf(entry);
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Åpne", "\uE8E5", () => OpenQuick(entry, false)));
        menu.Items.Add(Item("Åpne i ny fane", "\uE8A7", () => OpenQuick(entry, true)));
        menu.Items.Add(Item("Åpne terminal her", "\uE756", () => OpenTerminalTab(ShellCommands.LocalShellAt(entry.Path), "Terminal")));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Flytt opp", "\uE74A", () => _quick.Move(idx, idx - 1), idx > 0));
        menu.Items.Add(Item("Flytt ned", "\uE74B", () => _quick.Move(idx, idx + 1), idx >= 0 && idx < _quick.Count - 1));
        menu.Items.Add(Item("Fjern fra favoritter", "\uE74D", () => { _quick.Remove(entry); SyncSidebar(); }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Tilbakestill favoritter", "\uE777", () => { FavoritesStore.Reset(); BuildSidebar(); SyncSidebar(); }));
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
    }

    void OpenQuick(SidebarEntry entry, bool newTab)
    {
        var ex = CurrentExplorer;
        if (!newTab && ex != null && !ex.Active.Fs.IsRemote) _ = ex.Active.NavigateAsync(entry.Path);
        else NewExplorerTab(entry.Path);
    }

    void Quick_ItemClick(object s, ItemClickEventArgs e)
    {
        var path = ((SidebarEntry)e.ClickedItem).Path;
        var ex = CurrentExplorer;
        // Naviger i aktiv lokal rute, ellers åpne ny fane (f.eks. hvis aktiv rute er en SFTP-server)
        if (ex != null && !ex.Active.Fs.IsRemote) _ = ex.Active.NavigateAsync(path);
        else NewExplorerTab(path);
    }

    async void Ssh_ItemClick(object s, ItemClickEventArgs e)
    {
        var profile = ((SidebarEntry)e.ClickedItem).Profile;
        if (profile != null) await OpenSftpAsync(profile);
    }

    void Ssh_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SidebarEntry { Profile: { } p }) return;

        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, Symbol icon, Action act)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon) };
            mi.Click += (_, _) => act();
            return mi;
        }
        menu.Items.Add(Item("Åpne filer (SFTP)", Symbol.OpenFile, async () => await OpenSftpAsync(p)));
        menu.Items.Add(Item("Åpne terminal (SSH)", Symbol.Admin, () => OpenTerminalTab(ShellCommands.Ssh(p), p.Name)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Rediger", Symbol.Edit, async () =>
        {
            if (await ConnectionDialog.ShowAsync(Root.XamlRoot, p) != null) { ConnectionStore.Save(_profiles); RefreshSsh(); }
        }));
        menu.Items.Add(Item("Slett", Symbol.Delete, async () =>
        {
            if (!await Dialogs.ConfirmAsync(Root.XamlRoot, "Slett tilkobling", $"Slette «{p.Name}»?", "Slett")) return;
            _profiles.Remove(p);
            ConnectionStore.Save(_profiles);
            RefreshSsh();
        }));
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
    }

    async void AddSsh_Click(object s, RoutedEventArgs e)
    {
        var p = await ConnectionDialog.ShowAsync(Root.XamlRoot);
        if (p == null) return;
        _profiles.Add(p);
        ConnectionStore.Save(_profiles);
        RefreshSsh();
    }

    void NewTerminal_Click(object s, RoutedEventArgs e) => OpenTerminalTab(ShellCommands.LocalShell(), "Terminal");
    void ToggleDual_Click(object s, RoutedEventArgs e) => ToggleDual();

    // ───────── Faner ─────────

    ExplorerTab? CurrentExplorer => (Tabs.SelectedItem as TabViewItem)?.Content as ExplorerTab;

    void ToggleDual() => CurrentExplorer?.ToggleDual();

    TabViewItem AddTab(UIElement content, string header, string glyph)
    {
        var tab = new TabViewItem
        {
            Header = header,
            IconSource = new FontIconSource { Glyph = glyph },
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        return tab;
    }

    void NewExplorerTab(string path)
    {
        var et = new ExplorerTab(LocalFileSystem.Instance, path);
        et.TerminalRequested += OnTerminalRequested;
        et.FavoriteRequested += AddFavorite;
        var name = string.IsNullOrEmpty(path) ? "Denne PC-en" : System.IO.Path.GetFileName(path.TrimEnd('\\'));
        var item = AddTab(et, string.IsNullOrEmpty(name) ? path : name, "\uE8B7");
        et.LocationChanged += p => OnLocationChanged(et, item, p);
    }

    async Task OpenSftpAsync(ConnectionProfile p)
    {
        var secret = ConnectionStore.Unprotect(p.EncryptedSecret);
        if (secret == null && !p.UseKey)
        {
            secret = await Dialogs.PasswordAsync(Root.XamlRoot, $"Passord for {p.User}@{p.Host}");
            if (secret == null) return;
        }

        try
        {
            var fs = await SftpFileSystem.ConnectAsync(p, secret);
            ConnectionStore.Save(_profiles);   // lagrer vertsnøkkelen ved første tilkobling
            var et = new ExplorerTab(fs, fs.HomePath);
            et.TerminalRequested += OnTerminalRequested;
            var item = AddTab(et, p.Name, "\uE774");
            et.LocationChanged += pane => OnLocationChanged(et, item, pane);
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(Root.XamlRoot, "Kunne ikke koble til", ex.Message);
        }
    }

    void OnTerminalRequested(IFileSystem fs, string path)
    {
        if (fs is SftpFileSystem sftp)
            OpenTerminalTab(ShellCommands.Ssh(sftp.Profile, path), sftp.Profile.Name);
        else
            OpenTerminalTab(ShellCommands.LocalShellAt(path), "Terminal");
    }

    void OpenTerminalTab(string commandLine, string title)
    {
        try
        {
            AddTab(new TerminalTab(commandLine, WinRT.Interop.WindowNative.GetWindowHandle(this)), title, "\uE756");
        }
        catch (Exception ex)
        {
            // Full feilinfo til loggfil, slik at vi kan se den egentlige årsaken til XAML-feilen.
            string log = "";
            try
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenFiles");
                System.IO.Directory.CreateDirectory(dir);
                log = System.IO.Path.Combine(dir, "terminal-error.log");
                System.IO.File.WriteAllText(log, $"HResult: 0x{ex.HResult:X8}\r\n{ex}\r\n");
            }
            catch { }

            // Reserveløsning: åpne i eget terminalvindu (Windows Terminal hvis tilgjengelig, ellers konsoll).
            bool launched = LaunchExternalTerminal(commandLine);
            _ = Dialogs.ErrorAsync(Root.XamlRoot, "Innebygd terminal kunne ikke lastes",
                (launched ? "Åpnet i eget terminalvindu i stedet.\n\n" : "") +
                $"{ex.GetType().Name}: {ex.Message}\n" +
                (log.Length > 0 ? $"\nDetaljer: {log}" : ""));
        }
    }

    static bool LaunchExternalTerminal(string commandLine)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = commandLine,
                UseShellExecute = true
            });
            return true;
        }
        catch { }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c start \"\" " + commandLine,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch { return false; }
    }

    void OnLocationChanged(ExplorerTab et, TabViewItem item, ExplorerPane pane)
    {
        if (pane == et.Active && !string.IsNullOrEmpty(pane.Title)) item.Header = pane.Title;
        SyncSidebar();
    }

    void Tabs_SelectionChanged(object s, SelectionChangedEventArgs e) => SyncSidebar();

    /// <summary>Utheving i sidepanelet følger den aktive rutens mappe.</summary>
    void SyncSidebar()
    {
        var pane = CurrentExplorer?.Active;
        string? path = pane != null && !pane.Fs.IsRemote ? pane.CurrentPath.TrimEnd('\\') : null;

        SidebarEntry? Find(IEnumerable<SidebarEntry> l) => path == null ? null :
            l.FirstOrDefault(x => string.Equals(x.Path.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase));

        _syncing = true;
        try
        {
            var q = Find(_quick);
            QuickList.SelectedItem = q;
            DriveList.SelectedItem = q == null ? Find(_drives) : null;
        }
        finally { _syncing = false; }
    }

    void Side_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.MinHeight = 30;
        args.ItemContainer.Padding = new Thickness(8, 0, 8, 0);
        args.ItemContainer.Margin = new Thickness(0, 1, 0, 1);

        // Lokale mapper i sidepanelet er slippmål: dra filer hit for å flytte eller kopiere dem dit
        var c = args.ItemContainer;
        c.AllowDrop = args.Item is SidebarEntry { Profile: null };
        if (c.Tag is not "sd")
        {
            c.Tag = "sd"; c.DragOver += SideEntry_DragOver; c.Drop += SideEntry_Drop;
            c.DragLeave += (_, _) => ResetSideVisuals();
        }
    }

    /// <summary>
    /// Nullstiller dra-og-slipp-tilstandene i sidepanelet. Når slippet håndteres av oss, får ikke ListView
    /// beskjed om at draingen er over, og elementene blir stående forskjøvet/utheva ("bugga sidebar").
    /// </summary>
    void ResetSideVisuals()
    {
        foreach (var list in new ListViewBase[] { QuickList, DriveList, SshList })
        {
            for (int i = 0; i < list.Items.Count; i++)
            {
                if (list.ContainerFromIndex(i) is not Control c) continue;
                foreach (var st in new[] { "NotDragging", "NoReorderHint", "Normal" })
                    VisualStateManager.GoToState(c, st, false);
            }
        }
        // Utsatt: kjør også etter at layout har satt seg
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            foreach (var list in new ListViewBase[] { QuickList, DriveList, SshList })
                for (int i = 0; i < list.Items.Count; i++)
                    if (list.ContainerFromIndex(i) is Control c)
                    {
                        VisualStateManager.GoToState(c, "NotDragging", false);
                        VisualStateManager.GoToState(c, "NoReorderHint", false);
                    }
        });
    }

    void SideEntry_DragOver(object s, DragEventArgs e)
    {
        bool ours = e.DataView.Contains("ZenFiles.Items");
        if (!ours && !e.DataView.Contains(StandardDataFormats.StorageItems)) return;   // f.eks. omsortering av favoritter
        if ((s as ContentControl)?.Content is not SidebarEntry { Profile: null } entry) return;

        e.Handled = true;
        var d = ExplorerPane.CurrentDrag();
        bool ok = ours ? d is { } dd && !dd.Fs.IsRemote : true;   // fra SFTP støttes ikke hit ennå
        e.AcceptedOperation = ok ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (ok) e.DragUIOverride.Caption = $"Slipp i «{entry.Label}»";
    }

    ExplorerPane? LocalExecutor()
    {
        var a = CurrentExplorer?.Active;
        if (a != null && !a.Fs.IsRemote) return a;
        foreach (var t in Tabs.TabItems.OfType<TabViewItem>())
            if (t.Content is ExplorerTab et && !et.Active.Fs.IsRemote) return et.Active;
        return null;
    }

    async void SideEntry_Drop(object s, DragEventArgs e)
    {
        if ((s as ContentControl)?.Content is not SidebarEntry { Profile: null } entry) return;
        // Omsortering av favoritter (ingen filer i dataen) skal ListView håndtere selv
        if (!e.DataView.Contains("ZenFiles.Items") && !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var container = (FrameworkElement)s;
        e.Handled = true;
        var pos = e.GetPosition(container);

        var def = e.GetDeferral();
        IFileSystem? srcFs = null;
        List<FileItem>? items = null;
        ExplorerPane? srcPane = null;
        try
        {
            if (e.DataView.Contains("ZenFiles.Items") && ExplorerPane.CurrentDrag() is { } d)
            {
                if (d.Fs.IsRemote) return;
                srcFs = d.Fs; items = d.Items; srcPane = d.Pane;
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
        finally { def.Complete(); ExplorerPane.ClearDrag(); ResetSideVisuals(); }
        if (items == null || srcFs == null) return;

        var dst = entry.Path.TrimEnd('\\');
        var list = items.Where(i => !i.IsDrive
            && i.FullPath.TrimEnd('\\') != dst
            && !string.Equals(System.IO.Path.GetDirectoryName(i.FullPath.TrimEnd('\\'))?.TrimEnd('\\'), dst, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0) return;

        var exec = srcPane ?? LocalExecutor();
        if (exec == null) return;
        var from = srcFs; var pane = srcPane; var target = entry.Path;

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = list.Count == 1 ? $"«{list[0].Name}» til «{entry.Label}»" : $"{list.Count} elementer til «{entry.Label}»",
            IsEnabled = false
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        var move = new MenuFlyoutItem { Text = "Flytt hit", Icon = new SymbolIcon(Symbol.MoveToFolder) };
        move.Click += async (_, _) => await exec.ReceiveAsync(from, list, true, pane, target);
        var copy = new MenuFlyoutItem { Text = "Kopier hit", Icon = new SymbolIcon(Symbol.Copy) };
        copy.Click += async (_, _) => await exec.ReceiveAsync(from, list, false, pane, target);
        menu.Items.Add(move);
        menu.Items.Add(copy);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Avbryt", Icon = new SymbolIcon(Symbol.Cancel) });
        menu.Closed += (_, _) => ResetSideVisuals();
        menu.ShowAt(container, pos);
    }

    void Tabs_AddClick(TabView s, object e) => NewExplorerTab(Home);

    void Tabs_CloseRequested(TabView s, TabViewTabCloseRequestedEventArgs e) => CloseTab(e.Tab);

    void CloseCurrentTab()
    {
        if (Tabs.SelectedItem is TabViewItem t) CloseTab(t);
    }

    void CloseTab(TabViewItem tab)
    {
        Tabs.TabItems.Remove(tab);
        (tab.Content as IDisposable)?.Dispose();
        if (Tabs.TabItems.Count == 0) Close();
    }

    void AddAccel(VirtualKey key, VirtualKeyModifiers mod, Action act)
    {
        var ka = new KeyboardAccelerator { Key = key, Modifiers = mod };
        ka.Invoked += (_, a) => { act(); a.Handled = true; };
        Root.KeyboardAccelerators.Add(ka);
    }
}
