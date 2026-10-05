using System.Diagnostics;
using Clawd.Core;
using Clawd.Core.Orca;
using Clawd.Native;

namespace Clawd.Services;

/// <summary>Orca as a Windows app: whether it is in front, and bringing it there.</summary>
internal static class OrcaApp
{
    /// <summary>Orca is the foreground app (any of its windows).</summary>
    public static bool IsFrontmost()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, OrcaInstallation.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>Brings a running Orca to the front, or starts it. Windows only lets the foreground
    /// app hand over the foreground, which Clawd is right after a click, hotkey or menu choice.</summary>
    public static void Launch(OrcaInstallation orca)
    {
        foreach (var p in Process.GetProcessesByName(OrcaInstallation.ProcessName))
        {
            using (p)
            {
                var window = p.MainWindowHandle;
                if (window == IntPtr.Zero) continue;
                Win32.AllowSetForegroundWindow(p.Id);
                if (Win32.IsIconic(window)) Win32.ShowWindow(window, Win32.SW_RESTORE);
                Win32.SetForegroundWindow(window);
                return;
            }
        }
        if (orca.Executable is not { } exe) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error($"launch Orca: {e.Message}"); }
    }

    public static void OpenHomepage() => OpenUrl(OrcaInstallation.Homepage.ToString());

    /// <summary>Only web and mail links: anything else could launch things the user never meant to.</summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error($"open link: {e.Message}"); }
    }
}
