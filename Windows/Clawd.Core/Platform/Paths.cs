namespace Clawd.Core.Platform;

/// <summary>Folder comparisons that follow the file system: case-insensitive with either slash on Windows.</summary>
public static class Paths
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string path)
    {
        var p = OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;
        return p.Length > 1 ? p.TrimEnd('/', '\\') : p;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or a folder inside it.</summary>
    public static bool IsSameOrInside(string path, string root)
    {
        if (root.Length == 0) return false;
        var p = Normalize(path);
        var r = Normalize(root);
        if (string.Equals(p, r, Comparison)) return true;
        var sep = OperatingSystem.IsWindows() ? "\\" : "/";
        return p.StartsWith(r + sep, Comparison);
    }
}
