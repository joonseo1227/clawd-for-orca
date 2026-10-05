using System.Globalization;
using System.Text;

namespace Clawd.Core;

/// <summary>Short strings for times and trimmed text, shared by cards, menus and the chat.</summary>
public static class Text
{
    /// <summary>Collapses whitespace and trims to <paramref name="limit"/> characters (user-perceived ones).</summary>
    public static string? Snippet(string? s, int limit)
    {
        if (s is null) return null;
        var flat = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length == 0) return null;
        var info = new StringInfo(flat);
        return info.LengthInTextElements > limit ? info.SubstringByTextElements(0, limit - 1) + "…" : flat;
    }

    /// <summary>"3 min", "1 hr 5 min" ("3분", "1시간 5분")</summary>
    public static string Duration(double seconds)
    {
        var m = Math.Max(1, (int)(seconds / 60));
        return m < 60 ? Strings.Format("Duration_Minutes", m) : Strings.Format("Duration_HoursMinutes", m / 60, m % 60);
    }

    /// <summary>"just now", "3 min ago" ("방금", "3분 전")</summary>
    public static string Ago(DateTimeOffset date, DateTimeOffset? now = null)
    {
        var s = ((now ?? DateTimeOffset.Now) - date).TotalSeconds;
        return s < 60 ? Strings.Get("Ago_JustNow") : Strings.Format("Ago_Duration", Duration(s));
    }

    /// <summary>Drops a leading spinner glyph such as "✳ " or "◑ " from a terminal title.</summary>
    public static string WithoutSpinner(string title)
    {
        if (title.Length == 0) return title;
        var first = Rune.GetRuneAt(title, 0);
        var rest = title[first.Utf16SequenceLength..];
        return !Rune.IsLetterOrDigit(first) && rest.StartsWith(' ') ? rest[1..] : title;
    }
}

/// <summary>Monotonic seconds, unaffected by clock changes; the Mac app's systemUptime.</summary>
public static class Clock
{
    public static double Uptime => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
