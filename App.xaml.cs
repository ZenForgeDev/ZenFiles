using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ZenFiles.Services;

namespace ZenFiles;

public partial class App : Application
{
    /// <summary>Alle åpne vinduer (alle i denne prosessen).</summary>
    public static readonly List<MainWindow> Windows = new();

    static DispatcherQueue? _dq;
    static DispatcherQueueTimer? _saveTimer;

    public App()
    {
        InitializeComponent();
        // Krasjlogging til diag.log, slik at en feil som får appen til å lukke seg etterlater et spor.
        UnhandledException += (_, e) =>
        {
            Diag.Log($"KRASJ (XAML): hresult=0x{e.Exception.HResult:X8} melding='{e.Message}'\n--- unntakets stakk:\n{e.Exception.StackTrace}\n--- stakk i behandleren:\n{Environment.StackTrace}");
            // «Feil parameter» (E_INVALIDARG) fra XAML er som regel en feil i utseende/layout, ikke noe som ødelegger data:
            // la appen fortsette i stedet for å lukkes, så loggen kan fortelle hvor det skjedde.
            if (e.Exception is ArgumentException || e.Exception.HResult == unchecked((int)0x80070057)) e.Handled = true;
        };
        // Alle .NET-unntak som kastes (også de som fanges), med stakk, de første 40. Viser hvor «Feil parameter» oppstår.
        int firstChance = 0;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is not (ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)) return;
            if (Interlocked.Increment(ref firstChance) > 40) return;
            Diag.Log($"unntak kastet: {e.Exception.GetType().Name} 0x{e.Exception.HResult:X8} {e.Exception.Message.Trim()}\n{new System.Diagnostics.StackTrace(1, true)}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Diag.Log($"KRASJ (.NET, avslutter={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Diag.Log($"Ubehandlet oppgavefeil: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();

        // Nødutgang uten vindu: ZenFiles.exe --restore-default  (setter Windows Utforsker tilbake som standard)
        if (argv.Contains("--restore-default", StringComparer.OrdinalIgnoreCase))
        {
            try { DefaultFileManager.Disable(); } catch { }
            Exit();
            return;
        }

        // Windows sender mappen som skal åpnes som første argument ("%1").
        string? start = null;
        var raw = argv.FirstOrDefault(a => !a.StartsWith("--"));
        if (!string.IsNullOrWhiteSpace(raw))
        {
            raw = raw.Trim().Trim('"');
            if (Directory.Exists(raw)) start = raw;
            else if (File.Exists(raw)) start = Path.GetDirectoryName(raw);
            // Ikke-filsystem-steder (f.eks. ::{GUID}) faller tilbake til Hjem. Vi starter aldri explorer.exe herfra,
            // fordi det ville utløst oss selv på nytt i en evig løkke.
        }
        bool fresh = argv.Contains("--new-window", StringComparer.OrdinalIgnoreCase);

        _dq = DispatcherQueue.GetForCurrentThread();
        Diag.Start();
        MobileDevices.Apply(_dq);
        UpdateChecker.StartBackground(_dq);
        Themes.ApplyResources();   // tema og accent må være satt før første vindu opprettes

        // Kjører ZenFiles allerede, åpnes et nytt vindu der, og denne prosessen avslutter.
        if (!SingleInstance.TryBecomePrimary())
        {
            if (SingleInstance.SendToPrimary("open\t" + (start ?? ""))) { Exit(); return; }
            // Fikk ikke kontakt med den første instansen: kjør som egen prosess i stedet.
        }
        else
        {
            SingleInstance.StartServer(msg => _dq.TryEnqueue(() => HandleMessage(msg)));
        }

        if (start == null && !fresh && AppSettings.RestoreSession && SessionStore.Load() is { Windows.Count: > 0 } session)
        {
            foreach (var sw in session.Windows)
                new MainWindow(null, sw).Activate();
        }
        else
        {
            new MainWindow(start).Activate();
        }

        JumpListHelper.Update();
    }

    static void HandleMessage(string msg)
    {
        string? path = null;
        var parts = msg.Split('\t', 2);
        if (parts[0] == "open" && parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) && Directory.Exists(parts[1]))
            path = parts[1];
        OpenNewWindow(path);
    }

    public static MainWindow OpenNewWindow(string? path = null)
    {
        var w = new MainWindow(path);
        w.Activate();
        SingleInstance.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(w));
        return w;
    }

    // ───────── Økt (vinduer og faner som huskes til neste gang, hvis det er slått på) ─────────

    /// <summary>Lagrer økten kort tid etter siste endring.</summary>
    public static void RequestSessionSave()
    {
        if (!AppSettings.RestoreSession || _dq == null) return;
        if (_saveTimer == null)
        {
            _saveTimer = _dq.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(800);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => SaveSessionNow();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void SaveSessionNow()
    {
        if (!AppSettings.RestoreSession) return;
        var data = new SessionData();
        foreach (var w in Windows.ToList())
        {
            var s = w.Snapshot();
            if (s.Tabs.Count > 0) data.Windows.Add(s);
        }
        if (data.Windows.Count == 0) return;   // behold forrige lagrede økt hvis alt er lukket
        SessionStore.Save(data);
    }
}
