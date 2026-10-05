using Clawd.Native;

namespace Clawd.Services;

/// <summary>The chimes, from the user's Windows sound scheme so they match the rest of the system
/// (and follow it when changed in Settings › Sound).</summary>
internal static class Sounds
{
    /// <summary>An agent needs you: the Mac app's "Glass".</summary>
    public static void Nudge() => Play("Notification.Default");

    /// <summary>A task finished: the Mac app's softer "Pop".</summary>
    public static void Done() => Play("SystemAsterisk");

    private static void Play(string alias) =>
        Win32.PlaySound(alias, IntPtr.Zero, Win32.SND_ALIAS | Win32.SND_ASYNC | Win32.SND_NODEFAULT);
}
