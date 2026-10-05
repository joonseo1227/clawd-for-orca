using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Clawd.Core.Orca;

namespace Clawd.Core.Terminal;

/// <summary>Claude Code's dark-theme roles; the app maps them to colours.</summary>
public enum TerminalRole { Plain, Claude, Success, Error, Permission, Plan, Dim, Strong }

public sealed record TerminalSpan(string Text, TerminalRole Role, bool Cursor = false);

public sealed record TerminalLine(int Id, string Text, bool IsRule, IReadOnlyList<TerminalSpan> Spans);

/// <summary>
/// Orca's CLI returns the rendered screen as plain text, without colours. This recolours it with
/// Claude Code's own palette by recognising the parts of its interface, for the terminal view
/// shown before pairing.
/// </summary>
public static partial class TerminalStyler
{
    private const string Spinner = "✻✢✳✶✽·*";
    private const string Logo = "▐▛▜▌▝▘█▀";

    [GeneratedRegex(@"\s+$")] private static partial Regex TrailingSpace();
    [GeneratedRegex(@"^⏺ [A-Za-z_][\w:.-]*\(")] private static partial Regex ToolCall();
    [GeneratedRegex(@"^❯\s*\d+\.")] private static partial Regex HighlightedOption();
    [GeneratedRegex(@"^\s*\d+\s+\+")] private static partial Regex DiffAdded();
    [GeneratedRegex(@"^\s*\d+\s+-")] private static partial Regex DiffRemoved();

    public static List<TerminalLine> Lines(IReadOnlyList<string> screen, int? rows)
    {
        var raw = screen.Select(l => TrailingSpace().Replace(l, "")).ToList();
        if (rows is { } r)
        {
            // Fitted: keep the full grid, top to bottom, so it fills the view exactly.
            if (raw.Count > r) raw = raw.Skip(raw.Count - r).ToList();
            while (raw.Count < r) raw.Add("");
        }
        else
        {
            while (raw.Count > 0 && raw[^1].Length == 0) raw.RemoveAt(raw.Count - 1);
        }
        var dimBlock = false;   // inside a tool's output, whose wrapped lines stay dim
        var result = new List<TerminalLine>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var row = raw[i];
            var rule = row.Length > 0 && row.All(c => "─╌━ ".Contains(c));
            var t = row.Trim();
            // Tool output starts with "⎿" and wraps onto lines indented by five spaces.
            if (t.StartsWith('⎿')) dimBlock = true;
            else if (t.Length == 0 || !row.StartsWith("     ", StringComparison.Ordinal)) dimBlock = false;
            result.Add(new TerminalLine(i, row, rule, rule ? [] : CachedStyle(row, dimBlock)));
        }
        return result;
    }

    // Most rows don't change between refreshes; style each distinct row once.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<TerminalSpan>> Cache = new();

    private static IReadOnlyList<TerminalSpan> CachedStyle(string row, bool dim)
    {
        var key = (dim ? "1" : "0") + row;
        if (Cache.TryGetValue(key, out var hit)) return hit;
        if (Cache.Count > 2000) Cache.Clear();
        return Cache[key] = Style(row, dim);
    }

    /// <summary>Per-character roles for one screen row, merged into runs; the cursor mark becomes a
    /// one-character span with <see cref="TerminalSpan.Cursor"/> set.</summary>
    public static IReadOnlyList<TerminalSpan> Style(string row, bool dim)
    {
        var marker = row.IndexOf(TerminalCursor.Mark, StringComparison.Ordinal);
        var clean = row.Replace(TerminalCursor.Mark, "");
        var chars = (clean.Length == 0 ? " " : clean).ToCharArray();
        var roles = new TerminalRole[chars.Length];
        var t = clean.Trim();
        var lead = clean.Length - clean.TrimStart().Length;
        void All(TerminalRole r) => Array.Fill(roles, r);
        void Prefix(int n, TerminalRole r) { for (var k = lead; k < Math.Min(roles.Length, lead + n); k++) roles[k] = r; }
        var first = t.Length > 0 ? t[0] : '\0';

        if (t.Any(c => Logo.Contains(c)))
        {
            // The Clawd logo in the welcome header.
            for (var k = 0; k < chars.Length; k++) if (Logo.Contains(chars[k])) roles[k] = TerminalRole.Claude;
        }
        else if (dim || t.StartsWith('⎿') || t.StartsWith('…') || t.StartsWith("(ctrl", StringComparison.Ordinal) || t.StartsWith("Tip:", StringComparison.Ordinal)
                 || t.Contains("esc to interrupt", StringComparison.Ordinal) || t.Contains("? for shortcuts", StringComparison.Ordinal))
            All(TerminalRole.Dim);
        else if (t.StartsWith('⏺'))
            // Tool calls look like "⏺ Bash(...)"; plain "⏺ text" is Claude talking.
            Prefix(1, t.Contains("error", StringComparison.OrdinalIgnoreCase) ? TerminalRole.Error : ToolCall().IsMatch(t) ? TerminalRole.Success : TerminalRole.Plain);
        else if (Spinner.Contains(first) && first != '\0' && t.Contains('…'))
            All(TerminalRole.Claude);                       // live spinner: "✻ Thinking…"
        else if (Spinner.Contains(first) && first != '\0' && t.Contains(" for ", StringComparison.Ordinal))
            All(TerminalRole.Dim);                          // finished: "✻ Baked for 3s"
        else if (t.Contains("bypass permissions on", StringComparison.Ordinal)) All(TerminalRole.Error);
        else if (t.Contains("accept edits on", StringComparison.Ordinal)) All(TerminalRole.Permission);
        else if (t.Contains("plan mode on", StringComparison.Ordinal)) All(TerminalRole.Plan);
        else if (t.Contains("manual mode on", StringComparison.Ordinal) || t.StartsWith('⏵') || t.StartsWith('⏸')) All(TerminalRole.Dim);
        else if (HighlightedOption().IsMatch(t)) All(TerminalRole.Permission);   // highlighted option in a prompt
        else if (t.StartsWith("Do you want", StringComparison.Ordinal)) All(TerminalRole.Strong);
        else if (DiffAdded().IsMatch(clean)) All(TerminalRole.Success);
        else if (DiffRemoved().IsMatch(clean)) All(TerminalRole.Error);
        else if (t.StartsWith("Error", StringComparison.Ordinal) || t.StartsWith("error:", StringComparison.Ordinal)) All(TerminalRole.Error);

        var cursorAt = -1;
        if (marker >= 0)
        {
            // An empty prompt shows its placeholder dimmed, as Claude Code does.
            if (t.StartsWith('❯') && clean.Length > marker)
                for (var k = marker; k < roles.Length; k++) roles[k] = TerminalRole.Dim;
            // Block cursor on the character under it (or a trailing space).
            if (marker >= chars.Length)
            {
                Array.Resize(ref chars, marker + 1);
                Array.Resize(ref roles, marker + 1);
                for (var k = clean.Length; k <= marker; k++) chars[k] = ' ';
            }
            cursorAt = marker;
        }

        var spans = new List<TerminalSpan>();
        var start = 0;
        for (var k = 1; k <= chars.Length; k++)
        {
            var boundary = k == chars.Length || roles[k] != roles[start] || k == cursorAt || k - 1 == cursorAt;
            if (!boundary) continue;
            spans.Add(new TerminalSpan(new string(chars, start, k - start), roles[start], start == cursorAt));
            start = k;
        }
        return spans;
    }

    /// <summary>Display width in terminal cells: Hangul and other wide characters take two.</summary>
    public static int Cells(string text)
    {
        var w = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            var wide = v is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3
                or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60;
            w += wide ? 2 : 1;
        }
        return w;
    }
}
