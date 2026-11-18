using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace ZenFiles.Services;

/// <summary>
/// Lar et vilkårlig element fungere som tittellinje: dra flytter vinduet, dobbeltklikk maksimerer/gjenoppretter.
/// Flyttingen gjøres av appen selv mens musa er fanget av elementet (AppWindow.Move). Windows' egen flytteløkke
/// (SendMessage WM_NCLBUTTONDOWN) ble droppet fordi den ikke så at knappen ble sluppet i et XAML-vindu, og vinduet
/// fulgte da musa uten at man holdt. Ulempen er at kantsnapping ikke skjer ved dra i navnet.
/// </summary>
public static class WindowMove
{
    static long _lastPress;
    static bool _down, _dragging;
    static POINT _cursorStart;
    static PointInt32 _winStart;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();

    public static void OnPressed(Window window, PointerRoutedEventArgs e, UIElement source)
    {
        var pt = e.GetCurrentPoint(source);
        if (!pt.Properties.IsLeftButtonPressed) return;
        e.Handled = true;

        long now = Environment.TickCount64;
        bool dbl = now - _lastPress <= GetDoubleClickTime();
        _lastPress = dbl ? 0 : now;

        if (dbl)
        {
            End(source);
            if (window.AppWindow.Presenter is OverlappedPresenter p)
            {
                if (p.State == OverlappedPresenterState.Maximized) p.Restore(); else p.Maximize();
            }
            return;
        }

        _down = true;
        _dragging = false;
        GetCursorPos(out _cursorStart);
        _winStart = window.AppWindow.Position;
        source.CapturePointer(e.Pointer);
    }

    public static void OnMoved(Window window, PointerRoutedEventArgs e, UIElement source)
    {
        if (!_down) return;
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) { End(source); return; }

        GetCursorPos(out var now);
        int dx = now.X - _cursorStart.X, dy = now.Y - _cursorStart.Y;
        if (!_dragging)
        {
            if (dx * dx + dy * dy < 16) return;   // liten terskel så et vanlig klikk ikke flytter
            _dragging = true;
            if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } p)
            {
                // Dra et maksimert vindu: gjenopprett og legg det under musa
                p.Restore();
                var size = window.AppWindow.Size;
                _winStart = new PointInt32(now.X - size.Width / 2, now.Y - 20);
                _cursorStart = now;
                window.AppWindow.Move(_winStart);
                return;
            }
        }
        window.AppWindow.Move(new PointInt32(_winStart.X + dx, _winStart.Y + dy));
    }

    public static void OnReleased(PointerRoutedEventArgs e, UIElement source) => End(source);

    public static void OnCaptureLost() { _down = false; _dragging = false; }

    static void End(UIElement source)
    {
        _down = false; _dragging = false;
        try { source.ReleasePointerCaptures(); } catch { }
    }
}
