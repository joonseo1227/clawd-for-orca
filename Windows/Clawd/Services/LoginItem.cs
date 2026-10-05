using Microsoft.Win32;

namespace Clawd.Services;

/// <summary>
/// Launch at sign-in through the per-user Run key, so Clawd shows up (and can be switched off)
/// under Settings › Apps › Startup like any other app. Windows records that switch separately
/// under StartupApproved; it counts here too.
/// </summary>
internal static class LoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "Clawd";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is not string value || !value.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase)) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // First byte 2 (or no entry) means enabled; 3 means switched off in Settings or Task Manager.
            return approved?.GetValue(Name) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
    }

    /// <summary>Returns an error message, or null on success.</summary>
    public static string? Set(bool on)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            if (on)
            {
                run.SetValue(Name, Command);
                approved.DeleteValue(Name, throwOnMissingValue: false);   // undo an earlier "off" in Settings
            }
            else
            {
                run.DeleteValue(Name, throwOnMissingValue: false);
            }
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return e.Message;
        }
    }
}
