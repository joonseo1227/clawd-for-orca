using Clawd.Core;
using Clawd.Native;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Clawd.UI;

/// <summary>Window plumbing shared by Clawd's WinUI windows.</summary>
internal static class WindowChrome
{
    public static IntPtr Handle(Window w) => WinRT.Interop.WindowNative.GetWindowHandle(w);

    /// <summary>A flyout-like window: no title bar but the system's thin border, rounded corners and
    /// shadow; always on top; not in Alt-Tab or the taskbar.</summary>
    public static void MakeFlyout(Window w, bool resizable = false)
    {
        var app = w.AppWindow;
        if (app.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(true, false);
            p.IsResizable = resizable;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        app.IsShownInSwitchers = false;
        var hwnd = Handle(w);
        var corner = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>Clicks on it never take focus from the app being typed into.</summary>
    public static void NoActivate(Window w)
    {
        var hwnd = Handle(w);
        var ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (IntPtr)(ex | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
    }

    /// <summary>Gives the window's content exactly <paramref name="r"/> (physical pixels).
    /// <list type="bullet">
    /// <item>AppWindow sizes include the invisible resize frame (8 px at 100% left, right and
    /// bottom), so content laid out for the full size would be cut off; the frame is added outside.</item>
    /// <item>Landing on a monitor with another scale makes Windows rescale the window for the new
    /// DPI after the move (a 920 DIP chat created at 150% shrinks to 613 px on a 100% screen), so it
    /// is moved there first and sized again if it still came out wrong.</item>
    /// </list></summary>
    public static void Place(AppWindow app, RectInt32 r)
    {
        app.Move(new PointInt32(r.X, r.Y));
        var frameX = app.Size.Width - app.ClientSize.Width;
        var frameY = app.Size.Height - app.ClientSize.Height;
        var outer = new RectInt32(r.X - frameX / 2, r.Y, r.Width + frameX, r.Height + frameY);
        app.MoveAndResize(outer);
        if (app.Size.Width != outer.Width || app.Size.Height != outer.Height) app.MoveAndResize(outer);
    }

    /// <summary>A window of <paramref name="width"/> x <paramref name="height"/> DIPs centred in the work
    /// area of the monitor under the pointer, where the user just asked for it (or CLAWD_START_AT's).</summary>
    public static void Center(AppWindow app, double width, double height)
    {
        var m = Monitors.At(TestHooks.StartAt is var (x, y) ? new Win32.Point { X = x, Y = y } : Monitors.Cursor);
        var w = Math.Min((int)Math.Round(width * m.Scale), m.Work.Right - m.Work.Left);
        var h = Math.Min((int)Math.Round(height * m.Scale), m.Work.Bottom - m.Work.Top);
        Place(app, new RectInt32((m.Work.Left + m.Work.Right - w) / 2, (m.Work.Top + m.Work.Bottom - h) / 2, w, h));
    }

    /// <summary>A rectangle of <paramref name="width"/> x <paramref name="height"/> DIPs placed above the
    /// anchor (or below it when there's no room), centred on it and kept inside the work area.</summary>
    public static RectInt32 Above(Win32.Rect anchor, double width, double height, double gap = 8)
    {
        var m = Monitors.At(new Win32.Point { X = (anchor.Left + anchor.Right) / 2, Y = anchor.Top });
        var w = (int)Math.Round(width * m.Scale);
        var h = (int)Math.Round(height * m.Scale);
        var g = (int)Math.Round(gap * m.Scale);
        var x = (anchor.Left + anchor.Right) / 2 - w / 2;
        var y = anchor.Top - g - h;
        if (y < m.Work.Top) y = anchor.Bottom + g;
        x = Math.Clamp(x, m.Work.Left + g, Math.Max(m.Work.Left + g, m.Work.Right - w - g));
        y = Math.Clamp(y, m.Work.Top + g, Math.Max(m.Work.Top + g, m.Work.Bottom - h - g));
        return new RectInt32(x, y, w, h);
    }
}
