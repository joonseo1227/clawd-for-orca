namespace Clawd.Core.Transcripts;

public enum TimelineKind { User, Text, Thinking, Tool }

/// <summary>One step of an agent's turn: a prompt, a reply, a stretch of thinking, or a tool call.</summary>
/// <param name="Text">Message text, or the tool call's one-line summary.</param>
/// <param name="Result">A tool's result; null while it is still running.</param>
public sealed record TimelineItem(string Id, TimelineKind Kind, string Text, string? ToolName = null, string? Result = null, bool Failed = false)
{
    public static TimelineItem Tool(string id, string name, string text, string? result = null, bool failed = false) =>
        new(id, TimelineKind.Tool, text, name, result, failed);

    /// <summary>A tool call still waiting for its result, or thinking: something in progress.</summary>
    public bool IsRunningStep => Kind == TimelineKind.Thinking || (Kind == TimelineKind.Tool && Result is null);
}
