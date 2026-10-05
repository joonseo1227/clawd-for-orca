namespace Clawd.Core.Transcripts;

/// <summary>
/// What the timeline lays out: a single item, or a run of consecutive steps. Runs of tool calls
/// and thinking between messages fold into one row, the way Claude's own apps do, so the
/// replies stay readable; the newest step stays visible while it runs.
/// </summary>
public abstract record TimelineEntry
{
    public abstract string Id { get; }

    public sealed record Single(TimelineItem Item) : TimelineEntry
    {
        public override string Id => Item.Id;
    }

    public sealed record Steps(IReadOnlyList<TimelineItem> Items) : TimelineEntry
    {
        public override string Id => "steps-" + (Items.Count > 0 ? Items[0].Id : "");
        public bool Equals(Steps? other) => other is not null && Items.SequenceEqual(other.Items);
        public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>Groups tool calls and thinking between messages. Thinking without a summary says
    /// nothing, so it only shows while it is the latest thing happening.</summary>
    public static List<TimelineEntry> Build(IReadOnlyList<TimelineItem> items, bool live)
    {
        var entries = new List<TimelineEntry>();
        var run = new List<TimelineItem>();
        void Flush()
        {
            if (run.Count == 1) entries.Add(new Single(run[0]));
            else if (run.Count > 1) entries.Add(new Steps(run.ToList()));
            run.Clear();
        }
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            switch (item.Kind)
            {
                case TimelineKind.Thinking when item.Text.Length == 0 && !(live && i == items.Count - 1):
                    continue;
                case TimelineKind.Thinking or TimelineKind.Tool:
                    run.Add(item);
                    break;
                default:
                    Flush();
                    entries.Add(new Single(item));
                    break;
            }
        }
        Flush();
        return entries;
    }
}

/// <summary>Which icon a step shows; the app maps these to Segoe Fluent Icons.</summary>
public enum StepSymbol { Terminal, Document, Pencil, Search, Globe, People, Checklist, Puzzle, Wrench, Brain }

public static class StepText
{
    public static StepSymbol Symbol(string tool) => tool switch
    {
        "Bash" => StepSymbol.Terminal,
        "Read" => StepSymbol.Document,
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => StepSymbol.Pencil,
        "Grep" or "Glob" or "WebSearch" or "ToolSearch" => StepSymbol.Search,
        "WebFetch" => StepSymbol.Globe,
        "Agent" or "Task" => StepSymbol.People,
        "TodoWrite" => StepSymbol.Checklist,
        _ => tool.StartsWith("mcp__", StringComparison.Ordinal) ? StepSymbol.Puzzle : StepSymbol.Wrench,
    };

    /// <summary>"Thinking" while it says nothing yet, "Thought" once it has a summary.</summary>
    public static string ThinkingLabel(TimelineItem item) => Strings.Get(item.Text.Length == 0 ? "Step_Thinking" : "Step_Thought");

    /// <summary>"6 steps" and "Bash 3, Read 2, Thinking 1", and whether any step failed.</summary>
    public static (string Title, string Detail, bool Failed) Summary(IReadOnlyList<TimelineItem> steps)
    {
        var tools = new List<(string Name, int Count)>();
        var thoughts = 0;
        var failed = false;
        foreach (var step in steps)
        {
            if (step.Kind == TimelineKind.Tool && step.ToolName is { } name)
            {
                var i = tools.FindIndex(t => t.Name == name);
                if (i >= 0) tools[i] = (name, tools[i].Count + 1); else tools.Add((name, 1));
                failed = failed || (step.Failed && step.Result is not null);
            }
            else if (step.Kind == TimelineKind.Thinking)
            {
                thoughts++;
            }
        }
        var parts = tools.Take(3).Select(t => $"{t.Name} {t.Count}").ToList();
        if (tools.Count > 3) parts.Add(Strings.Format("Steps_MoreTools", tools.Count - 3));
        if (thoughts > 0) parts.Add(Strings.Format("Steps_Thoughts", thoughts));
        return (Strings.Plural("Steps_Title", steps.Count), string.Join(", ", parts), failed);
    }
}
