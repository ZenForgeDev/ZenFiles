using System.Runtime.InteropServices;
using EasyWindowsTerminalControl;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ZenFiles.Views;

/// <summary>
/// Terminalfane (lokal PowerShell eller SSH via Windows' ssh.exe) med Windows Terminal-motoren.
/// All avhengighet av EasyWindowsTerminalControl.WinUI (alfa/uoffisiell) er samlet i denne ene filen.
///
/// Kontrollen er et eget Win32-vindu som pakken selv plasserer oppå XAML. Pakken regner ut størrelsen
/// riktig (skalert), men bruker posisjonen i uskalerte enheter. På skjermer med skalering over 100 %
/// havner vinduet derfor for langt opp til venstre. Loggen viste at det ikke hjelper å flytte vinduet
/// selv, for pakken setter det tilbake hele tiden. Derfor flytter vi i stedet kontrollen i XAML med
/// (skala - 1) ganger dens egentlige posisjon, slik at pakkens uskalerte regning gir riktig sted.
/// </summary>
public sealed class TerminalTab : Grid, IDisposable
{
    readonly EasyTerminalControl _term;
    readonly Canvas _canvas = new();
    readonly IntPtr _mainHwnd;
    readonly DispatcherQueueTimer _timer;
    DependencyObject? _container;
    int _logCount;
    string _lastState = "";

    public TerminalTab(string commandLine, IntPtr mainHwnd)
    {
        _mainHwnd = mainHwnd;
        _term = new EasyTerminalControl { StartupCommandLine = commandLine };
        _canvas.Children.Add(_term);
        Children.Add(_canvas);

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(300);
        _timer.Tick += (_, _) => Verify();
        LayoutUpdated += (_, _) => Compensate();
        SizeChanged += (_, _) => Compensate();
        Loaded += (_, _) => { _timer.Start(); Compensate(); Log("Loaded"); };
        Unloaded += (_, _) => { _timer.Stop(); Log("Unloaded"); };
    }

    public void Dispose()
    {
        _timer.Stop();
        try { _term.DisconnectConPTYTerm(); } catch { }
    }

    // ───────── Plassering ─────────

    /// <summary>Forskyver kontrollen slik at pakkens uskalerte posisjon blir riktig fysisk posisjon.</summary>
    void Compensate()
    {
        try
        {
            if (XamlRoot?.Content is not UIElement root || ActualWidth < 1 || ActualHeight < 1) return;
            double scale = XamlRoot.RasterizationScale;
            var p = TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
            double left = p.X * (scale - 1), top = p.Y * (scale - 1);

            if (Math.Abs(Canvas.GetLeft(_term) - left) > 0.5 || double.IsNaN(Canvas.GetLeft(_term)))
                Canvas.SetLeft(_term, left);
            if (Math.Abs(Canvas.GetTop(_term) - top) > 0.5 || double.IsNaN(Canvas.GetTop(_term)))
                Canvas.SetTop(_term, top);
            if (Math.Abs(_term.Width - ActualWidth) > 0.5 || double.IsNaN(_term.Width)) _term.Width = ActualWidth;
            if (Math.Abs(_term.Height - ActualHeight) > 0.5 || double.IsNaN(_term.Height)) _term.Height = ActualHeight;
        }
        catch (Exception ex) { State("Compensate feilet: " + ex.GetType().Name + " " + ex.Message); }
    }

    /// <summary>Flytter ingenting, men logger når det virkelige vinduet ikke ligger der det skal (for feilsøking).</summary>
    void Verify()
    {
        try
        {
            if (XamlRoot?.Content is not UIElement root || ActualWidth < 1 || ActualHeight < 1) { State("ikke klar"); return; }
            _container ??= Find(_term, "TerminalContainer");
            if (_container == null) { State("fant ikke TerminalContainer"); return; }
            var h = ToPtr(_container.GetType().GetProperty("Handle")?.GetValue(_container));
            if (h == IntPtr.Zero) { State("Handle er 0 / ukjent type"); return; }
            if (!IsWindowVisible(h)) { State("terminalvindu skjult"); return; }
            var parent = GetParent(h);
            if (parent == IntPtr.Zero) { State("ingen parent"); return; }

            double scale = XamlRoot.RasterizationScale;
            var pt = TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
            var origin = new POINT();
            ClientToScreen(_mainHwnd, ref origin);
            var want = new POINT { X = origin.X + (int)Math.Round(pt.X * scale), Y = origin.Y + (int)Math.Round(pt.Y * scale) };
            ScreenToClient(parent, ref want);
            GetWindowRect(h, out var r);
            var cur = new POINT { X = r.Left, Y = r.Top };
            ScreenToClient(parent, ref cur);

            bool ok = Math.Abs(cur.X - want.X) <= 2 && Math.Abs(cur.Y - want.Y) <= 2;
            State(ok ? $"riktig plassert ({cur.X},{cur.Y}, skala {scale})"
                     : $"FEIL plassert: er {cur.X},{cur.Y} skal {want.X},{want.Y} (skala {scale}, størrelse {r.Right - r.Left}x{r.Bottom - r.Top})");
        }
        catch (Exception ex) { State("Verify feilet: " + ex.GetType().Name + " " + ex.Message); }
    }

    void Log(string text)
    {
        if (_logCount >= 60) return;   // begrenset, kun til feilsøking
        _logCount++;
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenFiles");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "terminal-layout.log"), $"{DateTime.Now:HH:mm:ss.fff} {text}\r\n");
        }
        catch { }
    }

    /// <summary>Logger bare når tilstanden endrer seg, så loggen ikke fylles av gjentakelser.</summary>
    void State(string s)
    {
        if (s == _lastState) return;
        _lastState = s;
        Log("tilstand: " + s);
    }

    static DependencyObject? Find(DependencyObject root, string typeName)
    {
        if (root.GetType().Name == typeName) return root;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var r = Find(VisualTreeHelper.GetChild(root, i), typeName);
            if (r != null) return r;
        }
        return null;
    }

    static IntPtr ToPtr(object? o)
    {
        switch (o)
        {
            case null: return IntPtr.Zero;
            case IntPtr p: return p;
            case long l: return (IntPtr)l;
            default:
                var t = o.GetType();
                if (t.GetField("Value")?.GetValue(o) is IntPtr a) return a;
                if (t.GetProperty("Value")?.GetValue(o) is IntPtr b) return b;
                return IntPtr.Zero;
        }
    }

    // ───────── Win32 ─────────

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
