using System.Text.Json.Nodes;
using Clawd.Core.Pet;

namespace Clawd.Core.Orca;

/// <summary>One coding agent running in an Orca terminal pane.</summary>
public sealed record OrcaAgent(
    string PaneKey,          // "<tabId>:<leafId>"
    string Name,
    string State,            // working, blocked, waiting, done, idle
    string? Tool,
    string? ToolInput,
    string? LastMessage,
    string? Prompt,
    DateTimeOffset? StateStartedAt,
    bool WorktreeActive,     // the worktree currently selected in Orca
    string Path = "",        // worktree folder, used to find the Claude Code transcript
    string AgentType = "",
    string WorktreeId = "")
{
    /// <summary>Orca treats live states untouched for 30 minutes as idle.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public bool NeedsYou => State is "blocked" or "waiting";

    /// <summary>False for a session started in Orca's chat: Orca runs it without a terminal, so it can't be
    /// shown, typed into or answered from Clawd. Orca names its pane after the session
    /// ("structured-agent-session-&lt;id&gt;:&lt;leaf&gt;"); nothing else in `worktree.ps` marks it reliably.</summary>
    public bool HasTerminal => !PaneKey.StartsWith(ChatSessionPrefix, StringComparison.Ordinal);
    public const string ChatSessionPrefix = "structured-agent-session-";

    /// <summary>The tab Orca shows a chat session in ("agent-session:&lt;session id&gt;"); null for a terminal.</summary>
    public string? ChatTabId => HasTerminal ? null : "agent-session:" + PaneKey.Split(':', 2)[0][ChatSessionPrefix.Length..];

    /// <summary>What Clawd does to mirror this agent's current tool.</summary>
    public Activity Activity => Tool switch
    {
        "Bash" => Activity.Build,
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => Activity.Type,
        "Read" or "Grep" or "Glob" or "WebFetch" or "WebSearch" or "ToolSearch" => Activity.Think,
        "Agent" or "Task" or "Workflow" => Activity.Juggle,
        { } t when t.StartsWith("mcp__", StringComparison.Ordinal) => Activity.LookAround,
        _ => Activity.Type,
    };

    /// <summary>What the agent wants from the user: the command it asks to run, or its question.</summary>
    public string? Ask
    {
        get
        {
            if (State == "blocked")
            {
                var what = string.Join(": ", new[] { Tool, Text.Snippet(ToolInput, 90) }.Where(s => !string.IsNullOrEmpty(s)));
                return what.Length == 0 ? null : what;
            }
            return Text.Snippet(LastMessage, 160);
        }
    }

    /// <summary>Agents from a `worktree.ps` answer (runtime pipe or `orca worktree ps --json`).</summary>
    public static IReadOnlyList<OrcaAgent>? Parse(JsonObject? answer, DateTimeOffset? now = null)
    {
        if (answer.Obj("result")?["worktrees"] is not JsonArray worktrees) return null;
        var nowMs = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        var result = new List<OrcaAgent>();
        foreach (var w in worktrees.OfType<JsonObject>())
        {
            var display = w.Str("displayName") ?? "";
            var name = display.Length == 0 || display == "main" ? w.Str("repo") ?? "?" : display;
            foreach (var a in w.Objects("agents"))
            {
                if (a.Str("paneKey") is not { } key || a.Str("state") is not { } state) continue;
                if (a.Num("updatedAt") is { } updated && state != "done" && nowMs - updated > StaleAfter.TotalMilliseconds) state = "idle";
                result.Add(new OrcaAgent(
                    key, name, state,
                    a.Str("toolName"), a.Str("toolInput"), a.Str("lastAssistantMessage"), a.Str("prompt"),
                    a.Num("stateStartedAt") is { } started ? DateTimeOffset.FromUnixTimeMilliseconds((long)started) : null,
                    w.Bool("isActive") ?? false,
                    w.Str("path") ?? "",
                    a.Str("agentType") ?? "",
                    w.Str("worktreeId") ?? ""));
            }
        }
        return result;
    }

    public static IReadOnlyList<OrcaAgent>? Parse(byte[]? data) => data is null ? null : Parse(Json.ParseObject(data));
}
