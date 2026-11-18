using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
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
    readonly ObservableCollection<SidebarEntry> _quick = new(), _drives = new(), _ssh = new(), _phones = new();

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    bool _initSwitch, _syncing, _loadingFav, _syncPending, _sidebarReady;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _syncTimer;
    readonly ToggleSwitch _defaultSwitch = new()
    {
        Header = "Standard filbehandler",
        OnContent = "På (erstatter Utforsker)",
        OffContent = "Av"
    };

    RectInt32 _geo;          // siste kjente plassering og størrelse (når vinduet ikke er maksimert)
    bool _maximized;
    MainWindow? _tearOutWindow;

    /// <summary>Tar i bruk valgt tema i dette vinduet: lys/mørk, bakgrunn (Mica eller solid) og vindusknapper.</summary>
    public void ApplyTheme()
    {
        var t = Themes.Current;

        // Bytt via motsatt tema først, så alle {ThemeResource}-farger (også accent) regnes ut på nytt.
        Root.RequestedTheme = t.Base == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        Root.RequestedTheme = t.Base;

        if (t.Background == null)
        {
            SystemBackdrop = new MicaBackdrop();
            Root.Background = null;
        }
        else
        {
            SystemBackdrop = null;
            Root.Background = new SolidColorBrush(FolderColors.Parse(t.Background));
        }

        try
        {
            var tb = AppWindow.TitleBar;
            if (t.Background == null)
            {
                tb.ButtonBackgroundColor = null; tb.ButtonInactiveBackgroundColor = null;
                tb.ButtonForegroundColor = null; tb.ButtonInactiveForegroundColor = null;
                tb.ButtonHoverBackgroundColor = null; tb.ButtonHoverForegroundColor = null;
                tb.ButtonPressedBackgroundColor = null; tb.ButtonPressedForegroundColor = null;
            }
            else
            {
                var fg = Themes.CaptionForeground(t);
                tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                tb.ButtonForegroundColor = fg;
                tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(160, fg.R, fg.G, fg.B);
                tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x22, fg.R, fg.G, fg.B);
                tb.ButtonHoverForegroundColor = fg;
                tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x33, fg.R, fg.G, fg.B);
                tb.ButtonPressedForegroundColor = fg;
            }
        }
        catch { }
    }

    /// <summary>Velger for tema i innstillinger.</summary>
    StackPanel BuildUpdateSetting()
    {
        var box = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        var sw = new ToggleSwitch { Header = "Se etter oppdateringer", OnContent = "På", OffContent = "Av", IsOn = AppSettings.UpdateCheck };
        var status = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var btn = new Button { Content = "Sjekk nå", Padding = new Thickness(12, 4, 12, 4) };

        sw.Toggled += async (_, _) =>
        {
            AppSettings.UpdateCheck = sw.IsOn;
            if (sw.IsOn) status.Text = await UpdateChecker.CheckAsync();
            else { UpdateChecker.Clear(); status.Text = ""; }
        };
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            status.Text = "Sjekker…";
            status.Text = await UpdateChecker.CheckAsync();
            btn.IsEnabled = true;
        };

        box.Children.Add(sw);
        box.Children.Add(btn);
        box.Children.Add(status);
        box.Children.Add(new TextBlock
        {
            Text = "Ved oppstart og hver 12. time spør ZenFiles GitHub om det finnes en nyere versjon. Ingen filer eller " +
                   "personopplysninger sendes, men GitHub ser IP-adressen din som ved all annen nettrafikk. Ingenting installeres automatisk.",
            FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap
        });
        return box;
    }

    StackPanel BuildMobileSetting()
    {
        var box = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        var sw = new ToggleSwitch
        {
            Header = "Mobile Devices",
            OnContent = "På", OffContent = "Av",
            IsOn = AppSettings.MobileDevices
        };
        sw.Toggled += (_, _) =>
        {
            AppSettings.MobileDevices = sw.IsOn;
            MobileDevices.Apply(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            foreach (var w in App.Windows) w.RefreshPhones();
        };
        box.Children.Add(sw);
        box.Children.Add(new TextBlock
        {
            Text = "Viser mobiler og nettbrett koblet til med USB under «Enheter». Android: lås opp og velg «Filoverføring». " +
                   "iPhone: kun kamerarulle (DCIM), og krever Apple-driver (Apple Devices fra Microsoft Store).",
            FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap
        });
        return box;
    }

    StackPanel BuildThemeSetting()
    {
        var box = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        box.Children.Add(new TextBlock { Text = "Tema", FontSize = 14 });
        var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var t in Themes.All) cb.Items.Add(t.Name);
        cb.SelectedIndex = Math.Max(0, Array.FindIndex(Themes.All, x => x.Id == Themes.Current.Id));
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedIndex >= 0 && Themes.All[cb.SelectedIndex].Id != Themes.Current.Id)
                Themes.SetCurrent(Themes.All[cb.SelectedIndex].Id);
        };
        box.Children.Add(cb);
        box.Children.Add(new TextBlock
        {
            Text = "Endrer farger i hele appen. AMOLED svart bruker ren svart bakgrunn.",
            FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap
        });
        return box;
    }

    /// <summary>Rad i innstillinger der man velger standard mappefarge (gjelder alle mapper uten egen farge).</summary>
    StackPanel BuildFolderColorSetting()
    {
        var box = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        box.Children.Add(new TextBlock { Text = "Mappefarge", FontSize = 14 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var buttons = new List<(Button b, string hex)>();

        void Mark()
        {
            var cur = AppSettings.FolderColor.Length > 0 ? AppSettings.FolderColor : FolderColors.DefaultHex;
            foreach (var (b, hex) in buttons)
                b.BorderBrush = new SolidColorBrush(hex.Equals(cur, StringComparison.OrdinalIgnoreCase)
                    ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Transparent);
        }

        foreach (var sw in FolderColors.Swatches)
        {
            var hex = sw.Hex;
            var b = new Button
            {
                Width = 22, Height = 22, Padding = new Thickness(0), CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(FolderColors.Parse(hex)), BorderThickness = new Thickness(2)
            };
            ToolTipService.SetToolTip(b, sw.Name);
            b.Click += (_, _) => { FolderColors.SetGlobal(hex); Mark(); };
            buttons.Add((b, hex));
            row.Children.Add(b);
        }
        Mark();
        box.Children.Add(row);
        box.Children.Add(new TextBlock
        {
            Text = "Gjelder alle mapper. Du kan gi enkeltmapper egen farge med høyreklikk → Mappefarge.",
            FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap
        });
        return box;
    }

    /// <param name="startPath">Mappen første fane åpner i.</param>
    /// <param name="session">Gjenopprett faner og plassering fra en lagret økt.</param>
    /// <param name="empty">Tomt vindu som skal motta en fane som flyttes hit.</param>
    public MainWindow(string? startPath = null, SessionWindow? session = null, bool empty = false)
    {
        InitializeComponent();
        Title = "ZenFiles";
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ZenFiles.ico")); } catch { }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        ApplyTheme();
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;   // ingen «Ctrl+T»-tooltips
        AppWindow.Resize(new SizeInt32(1440, 880));

        QuickList.ItemsSource = _quick;
        DriveList.ItemsSource = _drives;
        SshList.ItemsSource = _ssh;
        PhoneList.ItemsSource = _phones;
        MobileDevices.Changed += OnPhonesChanged;
        RefreshPhones();
        UpdateChecker.Changed += RefreshUpdateCard;
        RefreshUpdateCard();
        _quick.CollectionChanged += (_, _) => { if (!_loadingFav) SaveFavorites(); };
        BuildSidebar();

        // Ctrl+C/X/V/A: fanges for hele vinduet (også når fokus står i sidepanelet), men aldri i tekstfelt
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), true);
        AddAccel(VirtualKey.T, VirtualKeyModifiers.Control, () => NewExplorerTab(Home));
        AddAccel(VirtualKey.W, VirtualKeyModifiers.Control, CloseCurrentTab);
        AddAccel(VirtualKey.D, VirtualKeyModifiers.Control, ToggleDual);
        AddAccel(VirtualKey.L, VirtualKeyModifiers.Control, () => CurrentExplorer?.Active.FocusPathBox());
        AddAccel(VirtualKey.F, VirtualKeyModifiers.Control, () => CurrentExplorer?.Active.FocusSearch());
        AddAccel(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => OpenTerminalTab(ShellCommands.LocalShell(), "Terminal"));
        AddAccel(VirtualKey.N, VirtualKeyModifiers.Control, () =>
        {
            var a = CurrentExplorer?.Active;
            App.OpenNewWindow(a != null && !a.Fs.IsRemote && a.CurrentPath.Length > 0 ? a.CurrentPath : Home);
        });

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

        var sessionSwitch = new ToggleSwitch
        {
            Header = "Husk faner og vinduer",
            OnContent = "På",
            OffContent = "Av",
            IsOn = AppSettings.RestoreSession,
            Margin = new Thickness(0, 8, 0, 0)
        };
        sessionSwitch.Toggled += (_, _) =>
        {
            AppSettings.RestoreSession = sessionSwitch.IsOn;
            if (sessionSwitch.IsOn) App.SaveSessionNow(); else SessionStore.Clear();
        };
        panel.Children.Add(sessionSwitch);
        panel.Children.Add(new TextBlock
        {
            Text = "Lagrer åpne vinduer, faner (lokale mapper), liste/ikonvisning og vindusplassering, og åpner dem igjen " +
                   "neste gang du starter ZenFiles. SFTP- og terminalfaner gjenopprettes ikke.",
            FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(BuildMobileSetting());
        panel.Children.Add(BuildUpdateSetting());
        panel.Children.Add(BuildThemeSetting());
        panel.Children.Add(BuildFolderColorSetting());
        var ver = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        ver = ver.Split('+')[0];   // fjern git-hash e.l. etter «+»
        panel.Children.Add(new TextBlock
        {
            Text = $"ZenFiles {ver}", FontSize = 12, Opacity = 0.5, Margin = new Thickness(0, 12, 0, 0)
        });
        SettingsBtn.Flyout = new Flyout { Content = panel };

        App.Windows.Add(this);
        Closed += OnWindowClosed;
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += (_, _) => CaptureGeometry();

        if (empty) return;   // venter på en fane som flyttes hit
        Tabs.TabDragCompleted += (_, _) => DispatcherQueue.TryEnqueue(NormalizePinnedOrder);
        if (session != null) RestoreSession(session);
        else
        {
            // Festede mapper åpnes bare i det første vinduet (ikke i vinduer som åpnes senere).
            var pins = App.Windows.Count == 1 ? AppSettings.PinnedTabs.Where(Directory.Exists).ToList() : new List<string>();
            Diag.Log($"oppstart: festede mapper = {string.Join(" | ", pins)}; startPath='{startPath}'");
            foreach (var p in pins) NewExplorerTab(p, pinned: true, save: false);
            Diag.Log($"oppstart: festede faner opprettet ({Tabs.TabItems.Count})");
            if (pins.Count == 0 || !string.IsNullOrEmpty(startPath))
                NewExplorerTab(string.IsNullOrEmpty(startPath) ? Home : startPath);
            else if (Tabs.TabItems.Count > 0)
                Tabs.SelectedItem = Tabs.TabItems[0];
            Diag.Log("oppstart: vindu ferdig satt opp");
        }
        App.RequestSessionSave();
    }

    // ───────── Vindu: plassering, lukking, økt ─────────

    void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs a)
    {
        if (!a.DidPositionChange && !a.DidSizeChange) return;
        CaptureGeometry();
        App.RequestSessionSave();
    }

    bool _geoLocked;
    SessionWindow? _pendingGeo;

    /// <summary>
    /// Flytter først og endrer størrelse etterpå. Hvis vinduet flyttes til en skjerm med annen skalering (DPI) samtidig som
    /// størrelsen settes, regner Windows størrelsen om, og vinduet blir feil. Da må vinduet først ligge på riktig skjerm.
    /// </summary>
    void ApplySavedGeometry(SessionWindow sw)
    {
        try
        {
            var r = new RectInt32(sw.X, sw.Y, sw.W, sw.H);
            if (DisplayArea.GetFromRect(r, DisplayAreaFallback.None) != null)
                AppWindow.Move(new PointInt32(r.X, r.Y));   // skjermen finnes ikke lenger hvis ikke: da lar vi Windows plassere vinduet
            AppWindow.Resize(new SizeInt32(r.Width, r.Height));
        }
        catch { }
    }

    void OnFirstActivated(object sender, WindowActivatedEventArgs a)
    {
        if (_pendingGeo is not { } g) return;
        _pendingGeo = null;
        Activated -= OnFirstActivated;

        // Når vinduet er vist har Windows landet på skjermens DPI: sett posisjon og størrelse én gang til, og lås opp etterpå.
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplySavedGeometry(g);
            if (g.Maximized && AppWindow.Presenter is OverlappedPresenter p) { try { p.Maximize(); } catch { } }

            var t = DispatcherQueue.CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(700);
            t.IsRepeating = false;
            t.Tick += (_, _) =>
            {
                if (!g.Maximized) ApplySavedGeometry(g);
                _geoLocked = false;
                CaptureGeometry();
            };
            t.Start();
        });
    }

    /// <summary>Leser plassering og størrelse rett fra vinduet. Gjøres også ved lagring og lukking, så vi ikke er avhengige av at endringsmeldinger har kommet.</summary>
    void CaptureGeometry()
    {
        if (_geoLocked) return;
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                _maximized = p.State == OverlappedPresenterState.Maximized;
                if (p.State == OverlappedPresenterState.Restored)
                {
                    var pos = AppWindow.Position; var size = AppWindow.Size;
                    if (size.Width > 100 && size.Height > 100)
                        _geo = new RectInt32(pos.X, pos.Y, size.Width, size.Height);
                }
            }
        }
        catch { }   // vinduet kan allerede være borte ved lukking; da beholder vi forrige kjente verdi
    }

    void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Lukker du ikke det siste vinduet, lagres de andre uten dette. Det siste vinduet lagres med faner og alt.
        bool last = App.Windows.Count <= 1;
        if (!last) App.Windows.Remove(this);
        App.SaveSessionNow();
        App.Windows.Remove(this);

        _driveTimer?.Stop();
        MobileDevices.Changed -= OnPhonesChanged;
        UpdateChecker.Changed -= RefreshUpdateCard;
        foreach (var t in Tabs.TabItems.OfType<TabViewItem>().ToList())
        {
            (t.Tag as Action)?.Invoke();
            t.Tag = null;
            _pinned.Remove(t);
            (t.Content as IDisposable)?.Dispose();
        }

        // Et tomt vindu som aldri fikk noen fane (avbrutt løsriving) skal ikke holde programmet i live.
        foreach (var w in App.Windows.Where(w => w.IsEmptyHost).ToList()) w.Close();
    }

    bool IsEmptyHost => Tabs.TabItems.Count == 0;

    /// <summary>Øyeblikksbilde av dette vinduet til økt-filen. Bare lokale utforsker-faner tas med.</summary>
    public SessionWindow Snapshot()
    {
        CaptureGeometry();
        var sw = new SessionWindow { Maximized = _maximized };
        if (_geo.Width > 0 && _geo.Height > 0) { sw.X = _geo.X; sw.Y = _geo.Y; sw.W = _geo.Width; sw.H = _geo.Height; }
        foreach (var t in Tabs.TabItems.OfType<TabViewItem>())
        {
            if (t.Content is not ExplorerTab et || et.Left.Fs.IsRemote) continue;
            if (ReferenceEquals(t, Tabs.SelectedItem)) sw.Selected = sw.Tabs.Count;
            var tab = new SessionTab { Path = et.Left.CurrentPath, LeftGrid = et.Left.GridMode, Pinned = IsPinned(t) };
            if (et.Right is { } r && !r.Fs.IsRemote)
            {
                tab.Dual = true;
                tab.RightPath = r.CurrentPath;
                tab.RightGrid = r.GridMode;
            }
            sw.Tabs.Add(tab);
        }
        return sw;
    }

    void RestoreSession(SessionWindow sw)
    {
        try
        {
            if (sw.W > 0 && sw.H > 0)
            {
                _geo = new RectInt32(sw.X, sw.Y, sw.W, sw.H);   // husk den lagrede størrelsen selv om ingen endringsmelding kommer før neste lagring
                _geoLocked = true;   // ikke la mellomstørrelser underveis overskrive den lagrede
                _pendingGeo = sw;
                ApplySavedGeometry(sw);
                Activated += OnFirstActivated;   // gjør det på nytt når vinduet vises (se under)
            }
            _maximized = sw.Maximized;
        }
        catch { }

        var tabs = new List<TabViewItem>();
        foreach (var t in sw.Tabs)
        {
            if (t.Path.Length > 0 && !Directory.Exists(t.Path)) continue;   // mappen finnes ikke lenger
            var et = NewExplorerTab(t.Path, t.Pinned, save: false);
            et.Left.GridMode = t.LeftGrid;
            if (t.Dual && (t.RightPath.Length == 0 || Directory.Exists(t.RightPath)))
                et.OpenDual(t.RightPath, t.RightGrid);
            tabs.Add((TabViewItem)Tabs.SelectedItem);
        }
        if (tabs.Count == 0) { NewExplorerTab(Home); return; }
        Tabs.SelectedItem = tabs[Math.Clamp(sw.Selected, 0, tabs.Count - 1)];
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
        foreach (var e in ReadDrives()) _drives.Add(e);
        StartDriveWatch();

        RefreshSsh();
    }

    // ───────── Stasjoner som kommer og går ─────────

    static List<SidebarEntry> ReadDrives()
    {
        var list = new List<SidebarEntry>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    var label = string.IsNullOrEmpty(d.VolumeLabel) ? "Lokal disk" : d.VolumeLabel;
                    list.Add(new SidebarEntry
                    {
                        Label = $"{label} ({d.Name.TrimEnd('\\')})",
                        Glyph = "\uEDA2",
                        Path = d.Name,
                        Sub = $"{FileItem.Format(d.TotalFreeSpace)} ledig av {FileItem.Format(d.TotalSize)}"
                    });
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer? _driveTimer;
    string _driveSig = "";
    bool _driveBusy;

    /// <summary>Sjekker stasjonene hvert tredje sekund og oppdaterer «Steder» når en USB-penn eller et minnekort kommer eller går.</summary>
    void StartDriveWatch()
    {
        _driveSig = string.Join("|", _drives.Select(x => x.Label + x.Path));
        if (_driveTimer != null) return;
        _driveTimer = DispatcherQueue.CreateTimer();
        _driveTimer.Interval = TimeSpan.FromSeconds(3);
        _driveTimer.IsRepeating = true;
        _driveTimer.Tick += async (_, _) =>
        {
            if (_driveBusy) return;
            _driveBusy = true;
            try
            {
                var fresh = await Task.Run(ReadDrives);   // av og til tregt (frakoblet nettverksstasjon), derfor ikke på UI-tråden
                var sig = string.Join("|", fresh.Select(x => x.Label + x.Path));
                if (sig == _driveSig) return;
                _driveSig = sig;
                Diag.Log("stasjoner endret: " + sig);
                _drives.Clear();
                foreach (var e in fresh) _drives.Add(e);
                if (_sidebarReady) SyncSidebar();
            }
            catch (Exception ex) { Diag.Log("stasjonsoppdatering feilet: " + ex.Message); }
            finally { _driveBusy = false; }
        };
        _driveTimer.Start();
    }

    void OnPhonesChanged() => RefreshPhones();

    void RefreshUpdateCard()
    {
        var u = UpdateChecker.Available;
        bool show = u != null && AppSettings.UpdateCheck && UpdateChecker.Dismissed != u.Version;
        UpdateCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) UpdateText.Text = $"ZenFiles {u!.Version} er tilgjengelig (du har {UpdateChecker.CurrentVersion}).";
    }

    void UpdateDownload_Click(object s, RoutedEventArgs e)
    {
        if (UpdateChecker.Available is not { } u) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.Url) { UseShellExecute = true }); }
        catch { }
    }

    void UpdateDismiss_Click(object s, RoutedEventArgs e) => UpdateChecker.Dismiss();

    public void RefreshPhones()
    {
        _phones.Clear();
        foreach (var d in MobileDevices.Devices)
            _phones.Add(new SidebarEntry
            {
                Label = d.Name,
                Glyph = d.Kind == "Apple" ? "\uE8EA" : "\uE8EA",
                Sub = d.Kind == "Apple" ? "iPhone/iPad (USB)" : "Android (USB)",
                DeviceId = d.Id
            });
        PhoneSection.Visibility = MobileDevices.Enabled && _phones.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Phone_ItemClick(object s, ItemClickEventArgs e)
    {
        if (((SidebarEntry)e.ClickedItem).DeviceId is { } id) await OpenPhoneAsync(id);
    }

    async Task OpenPhoneAsync(string deviceId)
    {
        try
        {
            var fs = await MobileDevices.OpenAsync(deviceId);
            var et = new ExplorerTab(fs, "/");
            var item = AddTab(et, fs.DeviceName, "\uE8EA");
            HookExplorer(item, et);
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync(Root.XamlRoot, "Kunne ikke åpne enheten",
                ex.Message + "\n\nLås opp telefonen og velg «Filoverføring» (Android) eller trykk «Stol på denne datamaskinen» (iPhone).");
        }
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

    void SideTitle_PointerPressed(object s, PointerRoutedEventArgs e) => WindowMove.OnPressed(this, e, (UIElement)s);
    void SideTitle_PointerMoved(object s, PointerRoutedEventArgs e) => WindowMove.OnMoved(this, e, (UIElement)s);
    void SideTitle_PointerReleased(object s, PointerRoutedEventArgs e) => WindowMove.OnReleased(e, (UIElement)s);
    void SideTitle_CaptureLost(object s, PointerRoutedEventArgs e) => WindowMove.OnCaptureLost();

    async void Ssh_ItemClick(object s, ItemClickEventArgs e)
    {
        var profile = ((SidebarEntry)e.ClickedItem).Profile;
        Diag.Log($"ssh-klikk '{profile?.Name}' aktiv fane={(Tabs.SelectedItem as TabViewItem)?.Content?.GetType().Name} {Diag.Env()}");
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
        PrepareTab(tab);
        ApplyTabLook(tab);
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        App.RequestSessionSave();
        return tab;
    }

    // ───────── Festede faner ─────────

    /// <summary>Festede faner er lagret her mens appen kjører (statisk så de beholder festet når de flyttes mellom vinduer).</summary>
    static readonly HashSet<TabViewItem> _pinned = new();

    static bool IsPinned(TabViewItem t) => _pinned.Contains(t);

    static string TitleOf(TabViewItem t) =>
        t.Content is ExplorerTab et && !string.IsNullOrEmpty(et.Active.Title) ? et.Active.Title
        : t.Header as string is { Length: > 0 } h && h != "\u200B" ? h : "Fane";

    /// <summary>Festede faner vises smale med bare ikon (navnet står i verktøytipset); vanlige faner får navn og lukkeknapp.</summary>
    static void ApplyTabLook(TabViewItem t)
    {
        if (IsPinned(t))
        {
            // En TOM tittel (Header = "") på en festet fane gir «Feil parameter» fra XAML og krasj ved oppstart når fanen er valgt.
            // Derfor brukes et usynlig tegn (nullbreddes mellomrom): tittelen er ikke tom, men vises ikke, så fanen viser bare ikonet.
            var title = TitleOf(t);
            t.Header = "\u200B";
            ToolTipService.SetToolTip(t, title);
            t.IsClosable = false;
            t.MinWidth = 0;
            t.MaxWidth = 44;
        }
        else
        {
            if (t.Header is not string hs || hs.Length == 0 || hs == "\u200B") t.Header = TitleOf(t);
            ToolTipService.SetToolTip(t, null);
            t.IsClosable = true;
            t.MinWidth = 120;
            t.MaxWidth = 240;
        }
    }

    void SetPinned(TabViewItem tab, bool pin, bool save = true)
    {
        if (pin) _pinned.Add(tab); else _pinned.Remove(tab);
        ApplyTabLook(tab);
        PrepareTab(tab);
        MovePinBoundary(tab);
        if (save) { SavePins(); App.RequestSessionSave(); }
    }

    /// <summary>Festet fane går sist i den festede gruppen til venstre; løsnet fane går rett etter gruppen.</summary>
    void MovePinBoundary(TabViewItem tab)
    {
        int cur = Tabs.TabItems.IndexOf(tab);
        if (cur < 0) return;
        int target = Tabs.TabItems.OfType<TabViewItem>().Count(t => t != tab && IsPinned(t));
        if (cur == target) return;
        var sel = Tabs.SelectedItem;
        Tabs.TabItems.RemoveAt(cur);
        Tabs.TabItems.Insert(Math.Min(target, Tabs.TabItems.Count), tab);
        Tabs.SelectedItem = sel ?? tab;
    }

    /// <summary>Etter at brukeren har dratt faner: festede skal alltid ligge først.</summary>
    void NormalizePinnedOrder()
    {
        var all = Tabs.TabItems.OfType<TabViewItem>().ToList();
        var want = all.Where(IsPinned).Concat(all.Where(t => !IsPinned(t))).ToList();
        if (all.SequenceEqual(want)) return;
        var sel = Tabs.SelectedItem;
        for (int i = 0; i < want.Count; i++)
        {
            if (!ReferenceEquals(Tabs.TabItems[i], want[i]))
            {
                Tabs.TabItems.Remove(want[i]);
                Tabs.TabItems.Insert(i, want[i]);
            }
        }
        if (sel != null) Tabs.SelectedItem = sel;
    }

    /// <summary>Lagrer hvilke mapper som er festet (i alle vinduer) i innstillingene.</summary>
    static void SavePins()
    {
        var list = App.Windows
            .SelectMany(w => w.Tabs.TabItems.OfType<TabViewItem>())
            .Where(IsPinned)
            .Select(t => t.Content as ExplorerTab)
            .Where(et => et != null && !et.Left.Fs.IsRemote && et.Left.CurrentPath.Length > 0)
            .Select(et => et!.Left.CurrentPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        AppSettings.PinnedTabs = list;
    }

    /// <summary>Høyreklikkmeny på fanen. Bygges på nytt når fanen flyttes til et annet vindu.</summary>
    void PrepareTab(TabViewItem tab)
    {
        var menu = new MenuFlyout();
        var move = new MenuFlyoutItem
        {
            Text = "Flytt til nytt vindu",
            Icon = new FontIcon { Glyph = "\uE8A7" },
            IsEnabled = tab.Content is not TerminalTab   // terminalen er knyttet til vinduet den ble åpnet i
        };
        move.Click += (_, _) => MoveTabToNewWindow(tab);
        var close = new MenuFlyoutItem { Text = "Lukk fane", Icon = new FontIcon { Glyph = "\uE711" }, IsEnabled = !IsPinned(tab) };
        close.Click += (_, _) => CloseTab(tab);
        if (tab.Content is ExplorerTab pet && !pet.Left.Fs.IsRemote)
        {
            bool pinned = IsPinned(tab);
            var pin = new MenuFlyoutItem { Text = pinned ? "Løsne fane" : "Fest fane", Icon = new FontIcon { Glyph = pinned ? "\uE77A" : "\uE718" } };
            pin.Click += (_, _) => SetPinned(tab, !pinned);
            menu.Items.Add(pin);
        }
        menu.Items.Add(move);
        menu.Items.Add(close);
        tab.ContextFlyout = menu;
    }

    /// <summary>Kobler utforsker-fanens hendelser til dette vinduet. Tag på fanen holder en funksjon som kobler dem fra igjen.</summary>
    void HookExplorer(TabViewItem item, ExplorerTab et)
    {
        Action<IFileSystem, string> term = OnTerminalRequested;
        Action<string> fav = AddFavorite;
        Action<ExplorerPane> loc = p => OnLocationChanged(et, item, p);
        et.TerminalRequested += term;
        et.LocationChanged += loc;
        bool local = !et.Left.Fs.IsRemote;
        if (local) et.FavoriteRequested += fav;
        item.Tag = (Action)(() =>
        {
            et.TerminalRequested -= term;
            et.LocationChanged -= loc;
            if (local) et.FavoriteRequested -= fav;
        });
    }

    ExplorerTab NewExplorerTab(string path, bool pinned = false, bool save = true)
    {
        var et = new ExplorerTab(LocalFileSystem.Instance, path);
        var name = string.IsNullOrEmpty(path) ? "Denne PC-en" : System.IO.Path.GetFileName(path.TrimEnd('\\'));
        var item = AddTab(et, string.IsNullOrEmpty(name) ? path : name, "\uE8B7");
        HookExplorer(item, et);
        if (pinned) SetPinned(item, true, save);
        return et;
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fs = await SftpFileSystem.ConnectAsync(p, secret);
            Diag.Log($"sftp tilkoblet '{p.Name}' {sw.ElapsedMilliseconds}ms");
            ConnectionStore.Save(_profiles);   // lagrer vertsnøkkelen ved første tilkobling
            var et = new ExplorerTab(fs, fs.HomePath);
            var item = AddTab(et, p.Name, "\uE774");
            HookExplorer(item, et);
        }
        catch (Exception ex)
        {
            Diag.Log($"sftp FEIL '{p.Name}' {ex.GetType().Name}: {ex.Message}");
            var msg = ex.Message.Contains("Channel was closed", StringComparison.OrdinalIgnoreCase)
                ? "Innloggingen gikk bra, men serveren åpner ikke SFTP. Som regel mangler SFTP-tjenesten på serveren " +
                  "(for eksempel Dropbear uten sftp-server; på Debian/Raspberry Pi OS: sudo apt install openssh-sftp-server). " +
                  "Terminal (høyreklikk → Åpne terminal) fungerer uansett.\n\n" + ex.Message
                : ex.Message;
            await Dialogs.ErrorAsync(Root.XamlRoot, "Kunne ikke koble til", msg);
        }
    }

    void OnTerminalRequested(IFileSystem fs, string path)
    {
        if (fs is PhoneFileSystem) return;   // ingen terminal på mobil
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
        // Hvert trinn er skjermet: en feil her (f.eks. i uthevingen i sidepanelet) skal aldri få appen til å lukke seg.
        try
        {
            if (pane == et.Active && !string.IsNullOrEmpty(pane.Title))
            {
                if (IsPinned(item)) ToolTipService.SetToolTip(item, pane.Title);
                else item.Header = pane.Title;
            }
        }
        catch (Exception ex) { Diag.Log("OnLocationChanged/fane-tittel feilet: " + ex); }
        try { if (IsPinned(item)) SavePins(); }
        catch (Exception ex) { Diag.Log("OnLocationChanged/SavePins feilet: " + ex); }
        try { SyncSidebar(); }
        catch (Exception ex) { Diag.Log($"OnLocationChanged/SyncSidebar feilet (sti '{pane.CurrentPath}'): " + ex); }
        try { App.RequestSessionSave(); }
        catch (Exception ex) { Diag.Log("OnLocationChanged/RequestSessionSave feilet: " + ex); }
    }

    void Tabs_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        SyncSidebar();
        App.RequestSessionSave();
    }

    /// <summary>Utheving i sidepanelet følger den aktive rutens mappe.</summary>
    void SyncSidebar()
    {
        // Under oppstart tåler ikke favorittlisten at et element velges (XAML svarer «Feil parameter» selv om elementet finnes i listen
        // og listen er lastet), og feilen kan ta med seg hele appen. Markeringen er bare utseende, så den utsettes til listene har
        // fått tegnet seg ferdig.
        if (!_sidebarReady)
        {
            if (!_syncPending)
            {
                _syncPending = true;
                _syncTimer = DispatcherQueue.CreateTimer();
                _syncTimer.Interval = TimeSpan.FromMilliseconds(800);
                _syncTimer.IsRepeating = false;
                _syncTimer.Tick += (_, _) => { _sidebarReady = true; _syncPending = false; SyncSidebar(); };
                _syncTimer.Start();
            }
            return;
        }

        var pane = CurrentExplorer?.Active;
        string? path = pane != null && !pane.Fs.IsRemote ? pane.CurrentPath.TrimEnd('\\') : null;

        SidebarEntry? Find(IEnumerable<SidebarEntry> l) => path == null ? null :
            l.FirstOrDefault(x => string.Equals(x.Path.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase));

        _syncing = true;
        try
        {
            var q = Find(_quick);
            if (q != null && !QuickList.Items.Contains(q)) q = null;   // listen har ikke tatt imot elementene ennå
            var d = q == null ? Find(_drives) : null;
            if (d != null && !DriveList.Items.Contains(d)) d = null;
            if (!ReferenceEquals(QuickList.SelectedItem, q)) QuickList.SelectedItem = q;
            if (!ReferenceEquals(DriveList.SelectedItem, d)) DriveList.SelectedItem = d;
        }
        catch (Exception ex) { Diag.Log($"SyncSidebar feilet (sti '{path}'): " + ex); }
        finally { _syncing = false; }
    }

    void Side_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.MinHeight = 30;
        args.ItemContainer.Padding = new Thickness(8, 0, 8, 0);
        args.ItemContainer.Margin = new Thickness(0, 1, 0, 1);

        // Lokale mapper i sidepanelet er slippmål: dra filer hit for å flytte eller kopiere dem dit
        var c = args.ItemContainer;
        c.AllowDrop = args.Item is SidebarEntry { Profile: null, DeviceId: null };
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
        if (Tabs.SelectedItem is TabViewItem t && !IsPinned(t)) CloseTab(t);   // festede faner må løsnes først
    }

    void CloseTab(TabViewItem tab)
    {
        // Lukkes siste fane, tas økten med før fanen forsvinner, så den siste mappen huskes til neste gang.
        if (Tabs.TabItems.Count == 1) App.SaveSessionNow();
        (tab.Tag as Action)?.Invoke();
        tab.Tag = null;
        _pinned.Remove(tab);
        Tabs.TabItems.Remove(tab);
        (tab.Content as IDisposable)?.Dispose();
        if (Tabs.TabItems.Count == 0) Close();
        else App.RequestSessionSave();
    }

    // ───────── Flytte faner mellom vinduer ─────────

    /// <summary>Tar fanen ut av dette vinduet uten å lukke den (innholdet beholdes).</summary>
    void DetachTab(TabViewItem tab)
    {
        (tab.Tag as Action)?.Invoke();
        tab.Tag = null;
        Tabs.TabItems.Remove(tab);
    }

    /// <summary>Tar imot en fane fra et annet vindu.</summary>
    void AdoptTab(TabViewItem tab, int index = -1)
    {
        if (tab.Content is ExplorerTab et) HookExplorer(tab, et);
        PrepareTab(tab);
        if (index < 0 || index > Tabs.TabItems.Count) index = Tabs.TabItems.Count;
        Tabs.TabItems.Insert(index, tab);
        NormalizePinnedOrder();
        Tabs.SelectedItem = tab;
        SyncSidebar();
        App.RequestSessionSave();
    }

    void MoveTabToNewWindow(TabViewItem tab)
    {
        if (tab.Content is TerminalTab) return;
        var w = new MainWindow(empty: true);
        try { w.AppWindow.Resize(AppWindow.Size); } catch { }
        DetachTab(tab);
        w.AdoptTab(tab);
        w.Activate();
        if (Tabs.TabItems.Count == 0) Close();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetCursorPos(out Windows.Graphics.PointInt32 p);

    /// <summary>Fane sluppet utenfor fanelinjen -> eget vindu der musepekeren er.
    /// (WinUI sin innebygde uttrekking får hele skjermen til å blinke ved hvert klikk, så den er slått av.)</summary>
    void Tabs_DroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs e)
    {
        if (e.Tab is not TabViewItem tab || tab.Content is TerminalTab) return;
        if (Tabs.TabItems.Count == 1) return;   // eneste fane: allerede eget vindu
        GetCursorPos(out var cur);
        // Utsett til dra-og-slipp er helt ferdig; å flytte fanen midt i draget er det som kan krasje
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (!Tabs.TabItems.Contains(tab)) return;
                var w = new MainWindow(empty: true);
                try
                {
                    w.AppWindow.Resize(AppWindow.Size);
                    w.AppWindow.Move(new Windows.Graphics.PointInt32(cur.X - 200, cur.Y - 20));
                }
                catch { }
                DetachTab(tab);
                w.AdoptTab(tab);
                w.Activate();
            }
            catch (Exception ex) { _ = ex; }
        });
    }

    void Tabs_TearOutWindowRequested(TabView sender, TabViewTabTearOutWindowRequestedEventArgs e)
    {
        // Et vindu fra en avbrutt løsriving som aldri fikk noen fane, ryddes bort nå.
        if (_tearOutWindow is { IsEmptyHost: true } old) old.Close();
        _tearOutWindow = null;

        if (Tabs.SelectedItem is TabViewItem { Content: TerminalTab }) return;   // avbryter: terminalen kan ikke flyttes

        var w = new MainWindow(empty: true);
        try { w.AppWindow.Resize(AppWindow.Size); } catch { }
        _tearOutWindow = w;
        e.NewWindowId = w.AppWindow.Id;
    }

    void Tabs_TearOutRequested(TabView sender, TabViewTabTearOutRequestedEventArgs e)
    {
        var w = _tearOutWindow;
        _tearOutWindow = null;
        if (w == null) return;

        foreach (var tab in e.Tabs.OfType<TabViewItem>().ToList())
        {
            if (tab.Content is TerminalTab) continue;
            DetachTab(tab);
            w.AdoptTab(tab);
        }
        w.Activate();
        if (Tabs.TabItems.Count == 0) Close();
    }

    void Tabs_ExternalDropping(TabView sender, TabViewExternalTornOutTabsDroppingEventArgs e) => e.AllowDrop = true;

    /// <summary>En fane som dras fra et annet vindu og slippes her.</summary>
    void Tabs_ExternalDropped(TabView sender, TabViewExternalTornOutTabsDroppedEventArgs e)
    {
        foreach (var tab in e.Tabs.OfType<TabViewItem>().ToList())
        {
            if (tab.Content is TerminalTab) continue;
            var src = App.Windows.FirstOrDefault(x => x.Tabs.TabItems.Contains(tab));
            src?.DetachTab(tab);
            AdoptTab(tab, e.DropIndex);
            if (src != null && src != this && src.Tabs.TabItems.Count == 0) src.Close();
        }
    }

    async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.C or VirtualKey.X or VirtualKey.V or VirtualKey.A)) return;
        if (e.OriginalSource is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ComboBox) return;
        bool Down(VirtualKey k) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!Down(VirtualKey.Control) || Down(VirtualKey.Shift) || Down(VirtualKey.Menu)) return;
        if (CurrentExplorer is not { } ex) return;   // terminalfaner og andre faner håndterer tastene selv
        e.Handled = true;
        await ex.Active.HandleClipboardKeyAsync(e.Key);
    }

    void AddAccel(VirtualKey key, VirtualKeyModifiers mod, Action act)
    {
        var ka = new KeyboardAccelerator { Key = key, Modifiers = mod };
        ka.Invoked += (_, a) => { act(); a.Handled = true; };
        Root.KeyboardAccelerators.Add(ka);
    }
}
