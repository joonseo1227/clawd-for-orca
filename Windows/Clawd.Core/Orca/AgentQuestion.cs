using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Clawd.Core.Orca;

/// <summary>
/// Claude's AskUserQuestion: up to four questions, each with a few choices, single or multiple
/// pick, and room for an answer of one's own. A terminal session draws it as a dialog that only
/// takes keystrokes, so the questions come from the transcript (the dialog's text is clipped and
/// wrapped) and the answers go back as the keys a person would press. A session in Orca's chat
/// carries them in its history and takes the answers through Orca's API.
/// </summary>
public sealed class AgentQuestion : IEquatable<AgentQuestion>
{
    public sealed record Option(string Id, string Label, string? Description);

    public sealed class Item(string id, string question, string? header, bool multiSelect, IReadOnlyList<Option> options) : IEquatable<Item>
    {
        public string Id { get; } = id;
        public string Question { get; } = question;
        public string? Header { get; } = header;
        public bool MultiSelect { get; } = multiSelect;
        public IReadOnlyList<Option> Options { get; } = options;

        public bool Equals(Item? o) => o is not null && Id == o.Id && Question == o.Question && Header == o.Header
            && MultiSelect == o.MultiSelect && Options.SequenceEqual(o.Options);
        public override bool Equals(object? obj) => Equals(obj as Item);
        public override int GetHashCode() => HashCode.Combine(Id, Question, Options.Count);
    }

    /// <summary>One question's answer: the picked options by index, and text of one's own.</summary>
    public sealed class Answer
    {
        public HashSet<int> Picked { get; init; } = [];
        public string Other { get; set; } = "";

        public string OtherText => string.Join(" ", Other.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        public bool IsEmpty => Picked.Count == 0 && OtherText.Length == 0;
    }

    /// <summary>Where the question lives: a terminal's transcript, or a chat session's history
    /// item at the revision it was read at (Orca refuses an answer to a stale one).</summary>
    public abstract record QuestionSource
    {
        public sealed record Terminal(string ToolUseId) : QuestionSource;
        public sealed record Session(string ItemId, int Revision) : QuestionSource;
    }

    public QuestionSource Source { get; }
    public IReadOnlyList<Item> Items { get; }

    public AgentQuestion(QuestionSource source, IReadOnlyList<Item> items)
    {
        Source = source;
        Items = items;
    }

    /// <summary>One question with one choice: a click on a choice answers it.</summary>
    public bool AnswersOnClick => Items.Count == 1 && !Items[0].MultiSelect;

    /// <summary>Every question has an answer, and a single-choice one has at most one.</summary>
    public bool Complete(IReadOnlyList<Answer> answers) =>
        answers.Count == Items.Count && Items.Zip(answers).All(p =>
            !p.Second.IsEmpty && (p.First.MultiSelect || p.Second.Picked.Count + (p.Second.OtherText.Length == 0 ? 0 : 1) == 1));

    public List<Answer> EmptyAnswers() => Items.Select(_ => new Answer()).ToList();

    public bool Equals(AgentQuestion? o) => o is not null && Source == o.Source && Items.SequenceEqual(o.Items);
    public override bool Equals(object? obj) => Equals(obj as AgentQuestion);
    public override int GetHashCode() => HashCode.Combine(Source, Items.Count);

    /// <summary>The AskUserQuestion input Claude Code writes to its transcript.</summary>
    public static AgentQuestion? FromToolInput(JsonObject input, string toolUseId)
    {
        var raw = input.Objects("questions").ToList();
        var items = new List<Item>();
        for (var i = 0; i < raw.Count; i++)
        {
            var q = raw[i];
            if (q.Str("question") is not { Length: > 0 } text) continue;
            var options = q.Objects("options").Select((o, j) => o.Str("label") is { } label
                ? new Option((j + 1).ToString(CultureInfo.InvariantCulture), label, o.Str("description")) : null)
                .OfType<Option>().ToList();
            if (options.Count == 0) continue;
            items.Add(new Item($"q{i + 1}", text, q.Str("header"), q.Bool("multiSelect") ?? false, options));
        }
        // A dialog Clawd can't mirror one to one is left to Orca.
        return items.Count > 0 && items.Count == raw.Count ? new AgentQuestion(new QuestionSource.Terminal(toolUseId), items) : null;
    }

    /// <summary>A pending <c>question</c> item from a chat session's history.</summary>
    public static AgentQuestion? FromSessionItem(JsonObject item)
    {
        var body = item.Obj("body");
        if (body.Str("kind") != "question" || body.Obj("resolution").Str("state") != "pending"
            || item.Str("itemId") is not { } itemId || item.Int("revision") is not { } revision) return null;
        static List<Option> Options(IEnumerable<JsonObject> raw) => raw
            .Select(o => o.Str("id") is { } id && o.Str("label") is { } label ? new Option(id, label, o.Str("description")) : null)
            .OfType<Option>().ToList();
        var items = body.Objects("questions")
            .Select(q => q.Str("id") is { } id && q.Str("question") is { } text
                ? new Item(id, text, q.Str("header"), q.Bool("multiSelect") ?? false, Options(q.Objects("options"))) : null)
            .OfType<Item>().ToList();
        // Older hosts send a single question at the top level.
        if (items.Count == 0 && body.Str("question") is { } single)
            items.Add(new Item(body.Str("freeTextQuestionId") ?? "q1", single, null, false, Options(body.Objects("options"))));
        return items.Count > 0 ? new AgentQuestion(new QuestionSource.Session(itemId, revision), items) : null;
    }
}

/// <summary>
/// Claude Code's question dialog, as keystrokes and as the text it shows. Measured against Claude
/// Code 2.1: a digit picks a single-choice option and moves on; in a multiple-choice question it
/// toggles the option's box and leaves the cursor on the first row, so reaching the "Type
/// something" row takes ↓ once per option. → and ← move between questions (→ from the last one to
/// the review), Tab steps out of the text field, and the review is confirmed with 1.
/// </summary>
public static partial class QuestionDialog
{
    public const string Down = "\u001b[B";
    public const string Next = "\u001b[C";
    public const string Previous = "\u001b[D";

    /// <summary>The keystrokes answering one question, each sent on its own. <paramref name="ticked"/>
    /// holds the boxes already ticked in a multiple-choice question (the "Type something" row is index
    /// <c>Options.Count</c>), which a digit would untick. Null when the dialog holds text of its own
    /// that keystrokes can't safely replace.</summary>
    public static List<string>? Keys(AgentQuestion.Item item, AgentQuestion.Answer answer, IReadOnlySet<int>? ticked = null)
    {
        ticked ??= new HashSet<int>();
        static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        var other = answer.OtherText;
        var typeRow = item.Options.Count;
        if (!item.MultiSelect)
        {
            if (other.Length > 0) return [N(typeRow + 1), other, "\r"];
            return answer.Picked.Count > 0 ? [N(answer.Picked.Min() + 1)] : [];
        }
        var keys = Enumerable.Range(0, item.Options.Count).Where(j => answer.Picked.Contains(j) != ticked.Contains(j)).Select(j => N(j + 1)).ToList();
        if (other.Length == 0)
        {
            if (ticked.Contains(typeRow)) keys.Add(N(typeRow + 1));
            keys.Add(Next);
        }
        else
        {
            if (ticked.Contains(typeRow)) return null;
            keys.AddRange(Enumerable.Repeat(Down, item.Options.Count));
            keys.AddRange([other, "\t", "\r"]);
        }
        return keys;
    }

    private static IEnumerable<string> Bottom(IReadOnlyList<string> screen) =>
        screen.Skip(Math.Max(0, screen.Count - PermissionPrompt.SearchDepth));

    [GeneratedRegex(@"^(❯\s*)?1\.\s")]
    private static partial Regex FirstOption();

    [GeneratedRegex(@"^(?:❯\s*)?(\d+)\.\s+\[✔\]")]
    private static partial Regex TickedRow();

    /// <summary>The ticked boxes of a multiple-choice question on screen, by option index.</summary>
    public static HashSet<int> Ticked(IReadOnlyList<string> screen)
    {
        var result = new HashSet<int>();
        foreach (var line in Bottom(screen))
        {
            var m = TickedRow().Match(line.Trim());
            if (m.Success) result.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1);
        }
        return result;
    }

    /// <summary>The question the dialog is asking right now: the lines above its first option.</summary>
    public static string? ShownQuestion(IReadOnlyList<string> screen)
    {
        var lines = Bottom(screen).Select(l => l.Trim()).ToList();
        if (!lines.Any(l => l.Contains("Esc to cancel", StringComparison.Ordinal))) return null;
        var first = lines.FindIndex(l => FirstOption().IsMatch(l));
        if (first < 0) return null;
        var text = new List<string>();
        for (var i = first - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Length == 0 || line.Contains("───", StringComparison.Ordinal) || line.Contains('☐') || line.Contains('☒') || line.Contains('✔')) break;
            text.Insert(0, line);
        }
        return text.Count == 0 ? null : string.Join(" ", text);
    }

    /// <summary>The dialog is on <paramref name="item"/>. The screen may cut a long question short;
    /// what it shows has to open the question.</summary>
    public static bool Showing(AgentQuestion.Item item, IReadOnlyList<string> screen)
    {
        if (Text.Snippet(ShownQuestion(screen), 10_000) is not { } shown || Text.Snippet(item.Question, 10_000) is not { } wanted) return false;
        return shown == wanted || (shown.Length >= 12 && wanted.StartsWith(shown.TrimEnd('…'), StringComparison.Ordinal));
    }

    /// <summary>The "Review your answers" step that several questions (or a multiple-choice one) end on.</summary>
    public static bool Reviewing(IReadOnlyList<string> screen) =>
        Bottom(screen).Any(l => l.Contains("Ready to submit your answers?", StringComparison.Ordinal));
}
