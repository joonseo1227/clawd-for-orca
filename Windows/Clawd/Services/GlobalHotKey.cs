using Clawd.Core;
using Clawd.Native;

namespace Clawd.Services;

/// <summary>
/// The system-wide "talk to Clawd" shortcut through RegisterHotKey, which needs no special
/// permission. Registering again replaces the previous key.
/// </summary>
internal sealed class GlobalHotKey : IDisposable
{
    private const int Id = 1;
    private readonly HostWindow _host;
    private bool _registered;
    private double _last;

    public GlobalHotKey(HostWindow host, Action pressed)
    {
        _host = host;
        host.HotKey += id =>
        {
            if (id != Id) return;
            // Key repeat is off (MOD_NOREPEAT), but guard against a double delivery all the same:
            // a toggle would then open and close in a blink.
            var now = Clock.Uptime;
            if (now - _last < 0.3) return;
            _last = now;
            pressed();
        };
    }

    /// <summary>False when the key combination is taken by another app or the system.</summary>
    public bool Register(Shortcut shortcut)
    {
        Unregister();
        _registered = Win32.RegisterHotKey(_host.Handle, Id, (uint)shortcut.Modifiers | Win32.MOD_NOREPEAT, (uint)shortcut.Key);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) Win32.UnregisterHotKey(_host.Handle, Id);
        _registered = false;
    }

    /// <summary>Paused while a new shortcut is being recorded, so the current one doesn't fire.</summary>
    public bool Paused
    {
        set { if (value) Unregister(); }
    }

    public void Dispose() => Unregister();
}
