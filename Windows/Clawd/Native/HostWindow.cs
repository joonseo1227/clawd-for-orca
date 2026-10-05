namespace Clawd.Native;

/// <summary>
/// Clawd's hidden top-level window: it receives the notification-area icon's clicks, the global
/// hotkey and display changes, and owns the popup menus. Top-level rather than message-only,
/// because only top-level windows hear the "TaskbarCreated" broadcast that says Explorer
/// restarted and the icon must be added again.
/// </summary>
internal sealed class HostWindow : NativeWindow
{
    public const uint TrayMessage = Win32.WM_APP + 1;
    private static readonly uint TaskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");

    public event Action<uint, int, int>? TrayEvent;     // event, anchor x, anchor y
    public event Action<int>? HotKey;
    public event Action? TaskbarRestarted;
    public event Action? DisplayChanged;

    public HostWindow()
    {
        Create("ClawdHost", Win32.WS_EX_TOOLWINDOW, Win32.WS_POPUP);
    }

    protected override IntPtr? Message(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case TrayMessage:
                // NOTIFYICON_VERSION_4: the event in the low word of lParam, the anchor in wParam.
                TrayEvent?.Invoke((uint)Win32.LowWord(lParam), Win32.LowWord(wParam), Win32.HighWord(wParam));
                return IntPtr.Zero;
            case Win32.WM_HOTKEY:
                HotKey?.Invoke((int)wParam);
                return IntPtr.Zero;
            // Only a real display change: WM_SETTINGCHANGE also comes for a theme switch or a new
            // environment, and a moved or hidden taskbar is picked up by the next frame anyway.
            case Win32.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                return null;
            default:
                if (msg == TaskbarCreated && TaskbarCreated != 0) { TaskbarRestarted?.Invoke(); return IntPtr.Zero; }
                return null;
        }
    }
}
