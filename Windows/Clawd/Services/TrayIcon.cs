using System.Runtime.InteropServices;
using Clawd.Core.Pet;
using Clawd.Native;

namespace Clawd.Services;

/// <summary>
/// Clawd in the notification area: the menu bar item of the Mac app. Left click opens the chat,
/// right click the menu. The icon is the standing sprite, with an orange dot while an agent
/// needs you, and the tooltip carries the count the Mac shows beside its icon.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly HostWindow _host;
    private IntPtr _icon;
    private string _tip = "Clawd";
    private bool _badge;
    private bool _added;
    private const uint Id = 1;

    public TrayIcon(HostWindow host)
    {
        _host = host;
        host.TaskbarRestarted += () => { _added = false; Show(); };
        Show();
    }

    /// <summary>"Clawd · 확인 2" and the badge, refreshed only when they change.</summary>
    public void Update(string status, bool badge)
    {
        var tip = status.Length == 0 ? "Clawd" : $"Clawd · {status}";
        if (tip == _tip && badge == _badge) return;
        var redraw = badge != _badge;
        _tip = tip;
        _badge = badge;
        if (redraw) ReplaceIcon();
        Notify(Win32.NIM_MODIFY);
    }

    /// <summary>Where the icon is on screen, for anchoring the chat above it.</summary>
    public Win32.Rect? Location()
    {
        var id = new Win32.NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<Win32.NotifyIconIdentifier>(), Window = _host.Handle, Id = Id };
        return Win32.Shell_NotifyIconGetRect(ref id, out var rect) == 0 ? rect : null;
    }

    private void Show()
    {
        ReplaceIcon();
        _added = Notify(Win32.NIM_ADD);
        if (!_added) return;
        var data = Data();
        data.VersionOrTimeout = Win32.NOTIFYICON_VERSION_4;
        Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref data);
    }

    private bool Notify(uint message)
    {
        if (message == Win32.NIM_MODIFY && !_added) return false;
        var data = Data();
        return Win32.Shell_NotifyIcon(message, ref data);
    }

    private Win32.NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<Win32.NotifyIconData>(),
        Window = _host.Handle,
        Id = Id,
        Flags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP,
        CallbackMessage = HostWindow.TrayMessage,
        Icon = _icon,
        Tip = _tip,
        Info = "",
        InfoTitle = "",
    };

    private void ReplaceIcon()
    {
        var old = _icon;
        // The notification area draws icons at the small-icon size for the primary display's DPI.
        var size = (int)Math.Round(16 * Monitors.At(default).Scale);
        _icon = Icons.FromRasterizer(IconArt.Sprite(size, badge: _badge));
        if (old != IntPtr.Zero) Win32.DestroyIcon(old);
    }

    public void Dispose()
    {
        if (_added) Notify(Win32.NIM_DELETE);
        _added = false;
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }
}

internal static class Icons
{
    /// <summary>An HICON from premultiplied BGRA pixels.</summary>
    public static IntPtr FromRasterizer(Rasterizer image)
    {
        var header = new Win32.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<Win32.BitmapInfoHeader>(),
            Width = image.Width,
            Height = -image.Height,
            Planes = 1,
            BitCount = 32,
        };
        var color = Win32.CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        Marshal.Copy(image.Pixels, 0, bits, image.Pixels.Length);
        var mask = Win32.CreateBitmap(image.Width, image.Height, 1, 1, IntPtr.Zero);
        var info = new Win32.IconInfo { IsIcon = true, Color = color, Mask = mask };
        var icon = Win32.CreateIconIndirect(ref info);
        Win32.DeleteObject(color);
        Win32.DeleteObject(mask);
        return icon;
    }
}
