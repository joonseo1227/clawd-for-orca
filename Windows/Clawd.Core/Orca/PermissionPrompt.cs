using System.Globalization;
using Clawd.Core.Terminal;

namespace Clawd.Core.Orca;

/// <summary>A Claude Code "Do you want to proceed?" dialog read off the terminal screen.</summary>
public sealed class PermissionPrompt : IEquatable<PermissionPrompt>
{
    public sealed record Option(int Number, string Label)
    {
        /// <summary>Short label for the standard answers; anything else keeps its own text.</summary>
        public string Title =>
            Label == "Yes" ? Strings.Get("Permission_Allow")
            // "Yes, and switch to auto mode · auto mode handles these prompts for you" allows this once.
            : Label.StartsWith("Yes, and switch to auto mode", StringComparison.Ordinal) ? Strings.Get("Permission_AllowAuto")
            : Label.StartsWith("Yes, and", StringComparison.Ordinal) ? Strings.Get("Permission_AllowAlways")
            : Label.StartsWith("No", StringComparison.Ordinal) ? Strings.Get("Permission_Deny")
            : Label;

        /// <summary>Ctrl+1 to Ctrl+9; options past nine have no single-key shortcut.</summary>
        public char? Shortcut => Number is >= 1 and <= 9 ? (char)('0' + Number) : null;
    }

    public string Question { get; }
    /// <summary>The dialog's title ("Bash command"), then its body lines.</summary>
    public IReadOnlyList<string> Detail { get; }
    public IReadOnlyList<Option> Options { get; }

    /// <summary>Lines of <see cref="Detail"/> drawn inside a dashed box: the command, in Bash and PowerShell dialogs.</summary>
    public Range? Boxed { get; }

    public PermissionPrompt(string question, IReadOnlyList<string> detail, IReadOnlyList<Option> options, Range? boxed = null)
    {
        Question = question;
        Detail = detail;
        Options = options;
        Boxed = boxed;
    }

    /// <summary>"Bash command", "Edit file", …</summary>
    public string? Title => Detail.Count > 0 ? Detail[0] : null;
    /// <summary>The command or file the dialog asks about: what the dashed box holds, otherwise the first
    /// line under the title.</summary>
    public string? Command => Boxed is { } box ? string.Join("\n", Detail.Take(box)) : Detail.Count > 1 ? Detail[1] : null;
    /// <summary>What Claude says the command is for.</summary>
    public IEnumerable<string> Explanation
    {
        get
        {
            if (Boxed is not { } box) return Detail.Skip(2);
            var (start, length) = box.GetOffsetAndLength(Detail.Count);
            return Detail.Where((_, i) => i > 0 && (i < start || i >= start + length));
        }
    }

    /// <summary>Longest command kept from a dashed box; its first lines say the most.</summary>
    public const int BoxedLines = 6;

    /// <summary>The dialog sits at the bottom of the screen; looking further up would find questions
    /// Claude asked in its replies ("Do you want me to: 1. … 2. …").</summary>
    public const int SearchDepth = 30;

    /// <summary>Finds the dialog at the bottom of the screen, or null when none is showing. A dialog has
    /// a question, then options numbered 1, 2, 3… in order with exactly one selected ("❯").</summary>
    public static PermissionPrompt? Parse(IReadOnlyList<string> screen)
    {
        var lines = screen.Skip(Math.Max(0, screen.Count - SearchDepth)).Select(Unframed).ToList();
        var q = lines.FindLastIndex(l => l.StartsWith("Do you want", StringComparison.Ordinal));
        if (q < 0) return null;

        var options = new List<Option>();
        var selected = 0;
        foreach (var line in lines.Skip(q + 1))
        {
            var isSelected = line.StartsWith('❯');
            var body = isSelected ? line[1..].Trim() : line;
            // Blank lines, wrapped labels and the footer ("Esc to cancel") sit between options.
            var dot = body.IndexOf('.');
            if (dot < 0 || !int.TryParse(body.AsSpan(0, dot), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) continue;
            if (n != options.Count + 1) return null;
            if (isSelected) selected++;
            options.Add(new Option(n, body[(dot + 1)..].Trim()));
        }
        if (options.Count < 2 || selected != 1) return null;

        // Everything between the dialog's top rule and the question: tool name, description, command.
        // Bash and PowerShell dialogs put the description first and the command between two dashed
        // rules ("╌"); other dialogs list the file or command first, without rules.
        var top = lines.FindLastIndex(Math.Max(0, q - 1), q, l => l.Contains("───", StringComparison.Ordinal)) + 1;
        var detail = new List<string>();
        var rules = new List<int>();
        // The top rule spans the pane; text inside the dialog wraps one cell short of each side.
        var width = top > 0 ? TerminalStyler.Cells(lines[top - 1]) - 2 : 0;
        string? tip = null;   // the last line of a tip being skipped
        foreach (var line in lines.Skip(top).Take(q - top))
        {
            // Claude Code's own hints ("Tip: auto mode handles these prompts for you…") aren't the
            // request, nor are the lines a narrow pane wraps them onto: a line continues the tip when
            // its first word wouldn't have fitted on the tip's line.
            if (line.StartsWith("Tip:", StringComparison.Ordinal) || tip is not null && line.Length > 0 && Continues(tip, line, width))
            {
                tip = line;
                continue;
            }
            tip = null;
            if (line.Length == 0) continue;
            if (line.StartsWith('╌')) { rules.Add(detail.Count); continue; }
            detail.Add(line);
        }
        if (rules.Count < 2 || rules[0] == 0 || rules[1] == rules[0])
            return new PermissionPrompt(lines[q], detail.Skip(Math.Max(0, detail.Count - 6)).ToList(), options);
        var end = Math.Min(rules[1], rules[0] + BoxedLines);
        detail.RemoveRange(end, rules[1] - end);
        return new PermissionPrompt(lines[q], detail, options, rules[0]..end);
    }

    private static bool Continues(string previous, string line, int width) =>
        width > 0 && TerminalStyler.Cells(previous) + 1 + TerminalStyler.Cells(line.Split(' ')[0]) > width;

    /// <summary>A screen line without the dialog's side borders and surrounding spaces.</summary>
    private static string Unframed(string line) => line.Trim('│', '┃', ' ', ' ', '\t');

    public bool Equals(PermissionPrompt? other) =>
        other is not null && Question == other.Question && Detail.SequenceEqual(other.Detail) && Options.SequenceEqual(other.Options)
        && Boxed.Equals(other.Boxed);

    public override bool Equals(object? obj) => Equals(obj as PermissionPrompt);
    public override int GetHashCode() => HashCode.Combine(Question, Detail.Count, Options.Count);
}
