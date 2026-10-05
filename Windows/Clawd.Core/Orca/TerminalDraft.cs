using System.Text.RegularExpressions;

namespace Clawd.Core.Orca;

/// <summary>Where the input cursor is, marked inside a screen line (the screen text has no cursor).</summary>
public static class TerminalCursor
{
    public const string Mark = "";
}

/// <summary>
/// Orca lifts whatever is being typed at the agent's "❯" prompt out of the screen text into
/// `draft`. Putting it back lets the terminal view show what you type, with a cursor mark: the
/// screen text carries no cursor or inverse-video information.
/// </summary>
public static partial class TerminalDraft
{
    [GeneratedRegex(@"^❯\s*\d+\.")]
    private static partial Regex HighlightedOption();

    public static List<string> Merge(string draft, IReadOnlyList<string> screen)
    {
        var lines = screen.ToList();
        // The input line, not a highlighted "❯ 1. Yes" option in a permission dialog.
        static bool IsInput(string line)
        {
            var t = line.Trim();
            return t.StartsWith('❯') && !HighlightedOption().IsMatch(t);
        }
        if (PermissionPrompt.Parse(lines) is not null) return lines;
        var i = lines.FindLastIndex(IsInput);
        if (i < 0) return lines;
        var mark = lines[i].IndexOf('❯');

        if (draft.Length == 0)
        {
            // Empty prompt: the cursor sits on the first character of the placeholder.
            var rest = lines[i][(mark + 1)..];
            var gapLength = rest.TakeWhile(c => c is ' ' or ' ').Count();
            var gap = gapLength == 0 ? " " : rest[..gapLength];
            lines[i] = lines[i][..(mark + 1)] + gap + TerminalCursor.Mark + rest[gapLength..];
            return lines;
        }
        var parts = draft.Split('\n');
        var rows = new List<string> { lines[i][..(mark + 1)] + " " + parts[0] };
        rows.AddRange(parts.Skip(1).Select(p => "  " + p));
        rows[^1] += TerminalCursor.Mark;
        lines[i] = rows[0];
        lines.InsertRange(i + 1, rows.Skip(1));
        // Keep the screen's height: the draft's extra rows replace blank ones below.
        for (var k = 1; k < rows.Count; k++)
        {
            var blank = lines.FindLastIndex(l => l.Trim().Length == 0);
            if (blank > i + rows.Count - 1) lines.RemoveAt(blank);
        }
        return lines;
    }
}
