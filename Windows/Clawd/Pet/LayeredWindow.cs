using System.Runtime.InteropServices;
using Clawd.Core.Pet;
using Clawd.Native;

namespace Clawd.Pet;

/// <summary>
/// A borderless, always-on-top, per-pixel-alpha window that never takes focus, drawn with
/// UpdateLayeredWindow from a <see cref="Rasterizer"/>. Fully transparent pixels let clicks
/// through to whatever is underneath, so only Clawd's own pixels are clickable.
///
/// This is the one place the pet touches Win32 drawing; swapping it for another technique
/// (a WinUI window with a transparent backdrop, DirectComposition) only means replacing it.
/// </summary>
internal abstract class LayeredWindow : NativeWindow
{
    private IntPtr _dc;
    private IntPtr _bitmap;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private int _width, _height;

    protected LayeredWindow(string className, bool acceptFiles = false)
    {
        var ex = Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOACTIVATE;
        if (acceptFiles) ex |= Win32.WS_EX_ACCEPTFILES;
        // CS_DBLCLKS: double clicks arrive as WM_LBUTTONDBLCLK.
        Create(className, ex, Win32.WS_POPUP, Win32.CS_DBLCLKS);
        if (acceptFiles) Win32.DragAcceptFiles(Handle, true);
    }

    public bool Visible { get; private set; }

    /// <summary>Puts <paramref name="image"/> on screen with its top-left corner at (x, y), physical pixels.</summary>
    public void Present(Rasterizer image, int x, int y)
    {
        EnsureSurface(image.Width, image.Height);
        Marshal.Copy(image.Pixels, 0, _bits, image.Pixels.Length);
        var dst = new Win32.Point { X = x, Y = y };
        var size = new Win32.Size { Cx = _width, Cy = _height };
        var src = new Win32.Point();
        var blend = new Win32.BlendFunction { BlendOp = Win32.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Win32.AC_SRC_ALPHA };
        Win32.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, _dc, ref src, 0, ref blend, Win32.ULW_ALPHA);
        if (!Visible) Show();
    }

    public void Show()
    {
        Visible = true;
        Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    public void Hide()
    {
        Visible = false;
        Win32.ShowWindow(Handle, Win32.SW_HIDE);
    }

    /// <summary>Other always-on-top windows (the taskbar, a video player) can rise above Clawd; this
    /// puts it back on top without activating it.</summary>
    public void KeepOnTop()
    {
        if (Visible) Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void EnsureSurface(int width, int height)
    {
        if (_dc != IntPtr.Zero && width == _width && height == _height) return;
        ReleaseSurface();
        _width = width;
        _height = height;
        _dc = Win32.CreateCompatibleDC(IntPtr.Zero);
        var header = new Win32.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<Win32.BitmapInfoHeader>(),
            Width = width,
            Height = -height,   // top-down rows, matching the rasterizer
            Planes = 1,
            BitCount = 32,
        };
        _bitmap = Win32.CreateDIBSection(_dc, ref header, 0, out _bits, IntPtr.Zero, 0);
        _oldBitmap = Win32.SelectObject(_dc, _bitmap);
    }

    private void ReleaseSurface()
    {
        if (_dc == IntPtr.Zero) return;
        Win32.SelectObject(_dc, _oldBitmap);
        Win32.DeleteObject(_bitmap);
        Win32.DeleteDC(_dc);
        _dc = _bitmap = _bits = IntPtr.Zero;
    }

    public Win32.Rect Bounds => GetWindowRect(Handle, out var r) ? r : default;

    protected override IntPtr? Message(uint msg, IntPtr wParam, IntPtr lParam) =>
        // Clicking Clawd must not take focus from the app being typed into.
        msg == Win32.WM_MOUSEACTIVATE ? Win32.MA_NOACTIVATE : null;

    public override void Dispose()
    {
        ReleaseSurface();
        base.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32.Rect rect);
}

/// <summary>Converts between the pet's world (DIPs, y up) and the screen (physical pixels, y down)
/// on one monitor. World x is screen x / scale; world y is -(screen y) / scale.</summary>
internal readonly record struct ScreenSpace(Monitors.Monitor Monitor)
{
    public double Scale => Monitor.Scale;

    /// <summary>The work area: above the taskbar and any docked app bars.</summary>
    public Box Bounds => new(Monitor.Work.Left / Scale, -Monitor.Work.Bottom / Scale, Monitor.Work.Width / Scale, Monitor.Work.Height / Scale);

    public Vec ToWorld(Win32.Point p) => new(p.X / Scale, -p.Y / Scale);

    /// <summary>The screen position of a window's top-left corner from its world origin (bottom-left).</summary>
    public (int X, int Y) TopLeft(Vec origin, int heightPx) =>
        ((int)Math.Round(origin.X * Scale), (int)Math.Round(-origin.Y * Scale) - heightPx);

    /// <summary>The world origin (bottom-left) of a window whose top-left corner is at (x, y).</summary>
    public Vec Origin(int x, int y, int heightPx) => new(x / Scale, -(y + heightPx) / Scale);

    /// <summary>Whole device pixels per sprite unit (5 DIPs at 100%), so the pixel art stays crisp.</summary>
    public int UnitPixels => Math.Max(1, (int)Math.Round(5 * Scale));
}
