using System.Diagnostics;
using Microsoft.Win32;

namespace Clawd.Core.Orca;

/// <summary>
/// Where Orca is installed and the parts of it Clawd uses. On Windows, found through its
/// uninstall entry, a running Orca process, or the default per-user install folder; the CLI is
/// the one Orca ships in resources\bin, with a PATH lookup as fallback.
/// </summary>
public sealed record OrcaInstallation(string? AppDirectory, string? Executable, string? Cli)
{
    public static readonly Uri Homepage = new("https://www.onorca.dev");
    /// <summary>The process name of Orca's main executable (Orca.exe), used to tell when it is in front.</summary>
    public const string ProcessName = "Orca";

    /// <summary>Orca's Electron executable (Orca.exe), which runs Node scripts with ELECTRON_RUN_AS_NODE=1.</summary>
    public string? ExecutablePath => Executable;

    /// <summary>The same as <see cref="SharedModules"/>, under the name the live-terminal bridge uses.</summary>
    public string? SharedModulesDir => SharedModules;

    /// <summary>Orca's own client modules, shared by its CLI and mobile app; the bridge loads them.</summary>
    public string? SharedModules
    {
        get
        {
            if (AppDirectory is null) return null;
            var dir = OperatingSystem.IsMacOS()
                ? Path.Combine(AppDirectory, "Contents", "Resources", "app.asar.unpacked", "out", "shared")
                : Path.Combine(AppDirectory, "resources", "app.asar.unpacked", "out", "shared");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    public static OrcaInstallation? Locate()
    {
        foreach (var dir in CandidateDirectories().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (FromDirectory(dir) is { } found) return found;
        }
        // A CLI on the PATH still works for agent status, even without the app found.
        return FindOnPath() is { } cli ? new OrcaInstallation(null, null, cli) : null;
    }

    /// <summary>An install folder: Orca.exe with resources\bin\orca.exe beside it (Orca.app on macOS,
    /// which lets the debug tools run against a Mac's Orca).</summary>
    public static OrcaInstallation? FromDirectory(string dir)
    {
        string exe, cli;
        if (OperatingSystem.IsMacOS())
        {
            exe = Path.Combine(dir, "Contents", "MacOS", "Orca");
            cli = Path.Combine(dir, "Contents", "Resources", "bin", "orca");
        }
        else
        {
            exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "Orca.exe" : "orca");
            cli = Path.Combine(dir, "resources", "bin", OperatingSystem.IsWindows() ? "orca.exe" : "orca-ide");
        }
        if (!File.Exists(exe)) return null;
        return new OrcaInstallation(dir, exe, File.Exists(cli) ? cli : FindOnPath());
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Orca.app";
            yield return Path.Combine(AppPaths.Home, "Applications", "Orca.app");
            yield break;
        }
        if (!OperatingSystem.IsWindows()) yield break;

        // A running Orca knows exactly where it lives.
        foreach (var dir in RunningOrcaDirectories()) yield return dir;
        // electron-builder's NSIS installer registers an uninstall entry with the folder.
        foreach (var dir in UninstallEntries()) yield return dir;
        // Its defaults: per-user, then per-machine.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Programs", "Orca");
        yield return Path.Combine(local, "Programs", "orca");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Orca");
    }

    private static IEnumerable<string> RunningOrcaDirectories()
    {
        var dirs = new List<string>();
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            using (p)
            {
                try
                {
                    if (p.MainModule?.FileName is { } file && Path.GetDirectoryName(file) is { } dir) dirs.Add(dir);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        return dirs;
    }

    private static IEnumerable<string> UninstallEntries()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var dirs = new List<string>();
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var root = hive.OpenSubKey(uninstall);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is not string display || !display.StartsWith("Orca", StringComparison.OrdinalIgnoreCase)) continue;
                    if (key.GetValue("InstallLocation") is string location && location.Length > 0) dirs.Add(location.Trim('"'));
                    else if (key.GetValue("DisplayIcon") is string icon && Path.GetDirectoryName(icon.Split(',')[0].Trim('"')) is { } dir) dirs.Add(dir);
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return dirs;
    }

    /// <summary>Only a real executable: a .cmd would have to go through cmd.exe, which would reinterpret
    /// the text Clawd types into terminals.</summary>
    private static string? FindOnPath()
    {
        var name = OperatingSystem.IsWindows() ? "orca.exe" : "orca";
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!OperatingSystem.IsWindows()) dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", Path.Combine(AppPaths.Home, ".local", "bin")]);
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
