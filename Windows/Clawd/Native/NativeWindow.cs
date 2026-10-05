using System.Runtime.InteropServices;

namespace Clawd.Native;

/// <summary>
/// A raw Win32 window on the UI thread. WinUI's message loop pumps it like any other window on
/// the thread, so its messages arrive between XAML events, never concurrently with them.
/// </summary>
internal abstract class NativeWindow : IDisposable
{
    private static readonly Win32.WndProc Proc = Dispatch;   // kept alive for the process
    private static readonly Dictionary<IntPtr, NativeWindow> Windows = [];
    private static readonly HashSet<string> Registered = [];
    [ThreadStatic] private static NativeWindow? _creating;

    public IntPtr Handle { get; private set; }

    protected void Create(string className, uint exStyle, uint style, uint classStyle = 0)
    {
        var instance = Win32.GetModuleHandle(null);
        if (Registered.Add(className))
        {
            var wc = new Win32.WndClassEx
            {
                Size = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
                Style = classStyle,
                WndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                Instance = instance,
                Cursor = LoadCursor(IntPtr.Zero, 32512),   // IDC_ARROW
                ClassName = className,
            };
            if (Win32.RegisterClassEx(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }
        // Messages arrive during CreateWindowEx, before it returns the handle.
        _creating = this;
        try
        {
            Handle = Win32.CreateWindowEx(exStyle, className, "Clawd", style, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        }
        finally
        {
            _creating = null;
        }
        if (Handle == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        Windows[Handle] = this;
    }

    private static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!Windows.TryGetValue(hwnd, out var window))
        {
            window = _creating;
            if (window is not null) { window.Handle = hwnd; Windows[hwnd] = window; }
        }
        try
        {
            if (window is not null && window.Message(msg, wParam, lParam) is { } handled) return handled;
        }
        catch (Exception e)
        {
            // An exception must never unwind through a native window procedure.
            Clawd.Core.Log.Error($"window message {msg:X}: {e}");
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Handles a message; null passes it to DefWindowProc.</summary>
    protected virtual IntPtr? Message(uint msg, IntPtr wParam, IntPtr lParam) => null;

    public virtual void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        Windows.Remove(Handle);
        Win32.DestroyWindow(Handle);
        Handle = IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursor(IntPtr instance, int name);
}

/// <summary>Monitors, work areas and DPI, in physical pixels.</summary>
internal static class Monitors
{
    public readonly record struct Monitor(IntPtr Handle, Win32.Rect Bounds, Win32.Rect Work, double Scale);

    public static Monitor At(Win32.Point p)
    {
        var handle = Win32.MonitorFromPoint(p, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MonitorInfo { Size = (uint)Marshal.SizeOf<Win32.MonitorInfo>() };
        Win32.GetMonitorInfo(handle, ref info);
        var scale = Win32.GetDpiForMonitor(handle, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return new Monitor(handle, info.Monitor, info.Work, scale);
    }

    public static Win32.Point Cursor => Win32.GetCursorPos(out var p) ? p : default;
}

/// <summary>
/// Lets Win32 popup menus follow the system's dark mode, as Explorer's do. uxtheme exports this
/// only by ordinal (SetPreferredAppMode, 135, since Windows 10 1903); on a build without it the
/// menus simply stay light.
/// </summary>
internal static class DarkMenus
{
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    public static void FollowSystem()
    {
        try
        {
            SetPreferredAppMode(1);   // AllowDark: dark when the system is dark
            FlushMenuThemes();
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            Clawd.Core.Log.Debug(() => "dark menus unavailable");
        }
    }
}
