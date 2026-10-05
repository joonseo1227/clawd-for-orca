using System.Globalization;
using System.Reflection;

namespace Clawd.Core.Updates;

/// <summary>
/// A release version as Windows/VERSION and the <c>windows-v</c> tags write it: major.minor.patch,
/// optionally with a pre-release suffix ("1.0.0-beta.1"), compared by SemVer precedence. Build
/// metadata ("+commit", which the informational version carries) is ignored.
/// </summary>
public sealed record AppVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<AppVersion>
{
    /// <summary>The running app's version: Directory.Build.props gives every project, this one
    /// included, Windows/VERSION as its informational version.</summary>
    public static AppVersion? Current { get; } = Parse(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim();
        var plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus];
        string? pre = null;
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            pre = core[(dash + 1)..];
            core = core[..dash];
            if (pre.Length == 0 || pre.Split('.').Any(id => id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        }
        var parts = core.Split('.');
        if (parts.Length != 3) return null;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return new AppVersion(numbers[0], numbers[1], numbers[2], pre);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A release ranks above its own pre-releases.
        if (PreRelease is null || other.PreRelease is null) return (PreRelease is null).CompareTo(other.PreRelease is null);
        var a = PreRelease.Split('.');
        var b = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = a[i].All(char.IsAsciiDigit);
            var bn = b[i].All(char.IsAsciiDigit);
            // Numeric identifiers compare as numbers and rank below alphanumeric ones.
            c = an && bn ? CompareNumeric(a[i], b[i]) : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>Digit strings of any length, without overflowing.</summary>
    private static int CompareNumeric(string a, string b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (PreRelease is null ? "" : "-" + PreRelease);
}
