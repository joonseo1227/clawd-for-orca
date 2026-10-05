using System.Globalization;
using Clawd.Core.Orca;

namespace Clawd.Core.Chat;

/// <summary>Something the app should do after Orca reported a change.</summary>
public abstract record AttentionEvent
{
    /// <summary>An agent started waiting: read its screen for a permission dialog, and unless the user
    /// is already looking at it in Orca, hop, chime and (per policy) notify.</summary>
    public sealed record Waiting(OrcaAgent Agent, bool Looking) : AttentionEvent;
    /// <summary>A task the user wasn't watching finished.</summary>
    public sealed record Done(OrcaAgent Agent, TimeSpan? Took) : AttentionEvent;
    /// <summary>The periodic reminder for requests still unanswered.</summary>
    public sealed record Nudge : AttentionEvent;
}

/// <summary>
/// Which agents need the user, what they have already seen, and when Clawd nudges again. The Mac
/// app's Attention.swift without the AppKit parts, so it can be tested.
/// </summary>
public sealed class Attention
{
    public sealed record FinishedTask(OrcaAgent Agent, DateTimeOffset At, TimeSpan? Took);

    public static readonly TimeSpan NudgeEvery = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan RecentFinish = TimeSpan.FromHours(1);

    /// <summary>pane → state the user already opened or dismissed.</summary>
    public Dictionary<string, string> Acknowledged { get; } = [];
    public Dictionary<string, DateTimeOffset> WaitingSince { get; } = [];
    public Dictionary<string, DateTimeOffset> LastNudge { get; } = [];
    /// <summary>Completions the user hasn't looked at yet.</summary>
    public Dictionary<string, FinishedTask> Finished { get; } = [];
    /// <summary>pane → permission dialog on screen while waiting.</summary>
    public Dictionary<string, PermissionPrompt> Permissions { get; } = [];
    /// <summary>pane → a chat session's pending permission request.</summary>
    public Dictionary<string, SessionApproval> Approvals { get; } = [];
    /// <summary>pane → Claude's unanswered questions.</summary>
    public Dictionary<string, AgentQuestion> Questions { get; } = [];

    /// <summary>The agent waits on a permission request rather than a reply or Claude's questions. Orca
    /// reports a terminal's permission dialog as "waiting", like a question, so the screen tells them
    /// apart; a chat session reports its questions as "blocked", like a request.</summary>
    public bool AsksPermission(OrcaAgent a) =>
        !Questions.ContainsKey(a.PaneKey) && !a.AsksQuestion && (Permissions.ContainsKey(a.PaneKey) || a.State == "blocked");

    /// <summary>The user is already looking at this agent in Orca, so don't nag.</summary>
    public Func<OrcaAgent, bool> Looking { get; set; } = _ => false;

    /// <summary>Waiting agents the user hasn't seen yet, longest wait first.</summary>
    public List<OrcaAgent> Pending(IEnumerable<OrcaAgent> waiting, DateTimeOffset now) => waiting
        .Where(a => !Looking(a) && (!Acknowledged.TryGetValue(a.PaneKey, out var s) || s != a.State))
        .OrderBy(a => Since(a, now))
        .ToList();

    /// <summary>When the agent entered its current state: as seen by Clawd, or as Orca reports it for
    /// agents that were already waiting when Clawd started.</summary>
    public DateTimeOffset Since(OrcaAgent a, DateTimeOffset now) =>
        WaitingSince.TryGetValue(a.PaneKey, out var at) ? at : a.StateStartedAt ?? now;

    /// <summary>Completions worth listing: seen happening, or reported by Orca as recent.</summary>
    public DateTimeOffset? FinishedAt(OrcaAgent a, DateTimeOffset now)
    {
        if (Finished.TryGetValue(a.PaneKey, out var f)) return f.At;
        if (a.State != "done" || a.StateStartedAt is not { } at || now - at >= RecentFinish) return null;
        return Acknowledged.TryGetValue(a.PaneKey, out var s) && s == a.State ? null : at;
    }

    public List<AttentionEvent> Changed(IReadOnlyList<OrcaAgent> old, IReadOnlyList<OrcaAgent> current, DateTimeOffset now)
    {
        var events = new List<AttentionEvent>();
        var before = new Dictionary<string, OrcaAgent>();
        foreach (var a in old) before.TryAdd(a.PaneKey, a);
        foreach (var a in current)
        {
            before.TryGetValue(a.PaneKey, out var prev);
            var looking = Looking(a);
            if (looking || a.State == "working") Finished.Remove(a.PaneKey);
            if (prev?.State == a.State) continue;
            Acknowledged.Remove(a.PaneKey);
            Permissions.Remove(a.PaneKey);
            Approvals.Remove(a.PaneKey);
            Questions.Remove(a.PaneKey);
            if (a.NeedsYou)
            {
                WaitingSince[a.PaneKey] = now;
                LastNudge[a.PaneKey] = now;
                events.Add(new AttentionEvent.Waiting(a, looking));
            }
            else if (prev?.State == "working" && a.State == "done" && !looking)
            {
                TimeSpan? took = prev.StateStartedAt is { } started ? now - started : null;
                Finished[a.PaneKey] = new FinishedTask(a, now, took);
                events.Add(new AttentionEvent.Done(a, took));
            }
        }

        var live = current.Where(a => a.NeedsYou).Select(a => a.PaneKey).ToHashSet();
        foreach (var key in WaitingSince.Keys.Where(k => !live.Contains(k)).ToList()) WaitingSince.Remove(key);
        foreach (var key in LastNudge.Keys.Where(k => !live.Contains(k)).ToList()) LastNudge.Remove(key);
        foreach (var key in Finished.Where(kv => now - kv.Value.At >= RecentFinish).Select(kv => kv.Key).ToList()) Finished.Remove(key);

        // The card stays up on its own; every few minutes Clawd also hops and chimes again.
        foreach (var a in Pending(current.Where(a => a.NeedsYou), now))
        {
            if (LastNudge.TryGetValue(a.PaneKey, out var last) && now - last > NudgeEvery)
            {
                LastNudge[a.PaneKey] = now;
                events.Add(new AttentionEvent.Nudge());
            }
        }
        return events;
    }

    /// <summary>The user dealt with the agent: no card or nagging until its state changes.</summary>
    public void Handled(OrcaAgent a)
    {
        Acknowledged[a.PaneKey] = a.State;
        Finished.Remove(a.PaneKey);
    }

    public void Reset()
    {
        WaitingSince.Clear();
        LastNudge.Clear();
        Finished.Clear();
        Permissions.Clear();
        Approvals.Clear();
        Questions.Clear();
        Acknowledged.Clear();
    }

    /// <summary>Sidebar rows: needs you (longest wait first), working, finished (newest first), resting.</summary>
    public List<ChatRow> Rows(IEnumerable<OrcaAgent> agents, IReadOnlyDictionary<string, string> titles, DateTimeOffset now)
    {
        RowKind Kind(OrcaAgent a)
        {
            if (a.NeedsYou) return AsksPermission(a) ? RowKind.Permission : RowKind.Question;
            if (a.State == "working") return RowKind.Working;
            return FinishedAt(a, now) is not null ? RowKind.Finished : RowKind.Resting;
        }
        string Status(OrcaAgent a, RowKind k) => k switch
        {
            RowKind.Permission => Strings.Format("Row_Permission", Text.Ago(Since(a, now), now)),
            RowKind.Question => Strings.Format("Row_Question", Text.Ago(Since(a, now), now)),
            RowKind.Working => a.Tool is { } t ? Strings.Format("Row_WorkingTool", t) : Strings.Get("Row_Working"),
            RowKind.Finished => Strings.Format("Row_Finished", Text.Ago(FinishedAt(a, now) ?? now, now)),
            _ => Strings.Get("Row_Resting"),
        };
        var rows = agents.Select(a =>
        {
            var k = Kind(a);
            return new ChatRow(a, k, titles.GetValueOrDefault(a.PaneKey), Status(a, k));
        }).ToList();
        rows.Sort((x, y) =>
        {
            if (x.Kind != y.Kind) return x.Kind.CompareTo(y.Kind);
            if (x.Kind <= RowKind.Question && Since(x.Agent, now) != Since(y.Agent, now)) return Since(x.Agent, now).CompareTo(Since(y.Agent, now));
            if (x.Kind == RowKind.Finished && FinishedAt(x.Agent, now) is { } fx && FinishedAt(y.Agent, now) is { } fy && fx != fy) return fy.CompareTo(fx);
            // Like Finder: case-insensitive, with numbers in numeric order ("agent 2" before "agent 10").
            return string.Compare(x.Agent.Name, y.Agent.Name, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
        });
        return rows;
    }
}
