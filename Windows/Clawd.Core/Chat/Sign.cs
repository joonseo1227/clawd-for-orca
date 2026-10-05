using Clawd.Core.Orca;

namespace Clawd.Core.Chat;

public enum SignTone { Urgent, Done, Info }

/// <summary>Icons for cards and confirmations; the app maps them to Segoe Fluent Icons.</summary>
public enum SignSymbol { Hand, Bubble, Check, CheckCircle, Info, Warning, Pause, Play, Moon, Hammer, Keyboard, Link, Unlink, Error }

/// <summary>What Clawd shows above its head.</summary>
public sealed record Sign(SignTone Tone, SignSymbol Symbol, string Title, string? Name = null, string? Detail = null, string? Hint = null)
{
    /// <summary>The oldest waiting agent, and how many more there are. <paramref name="isPermission"/>
    /// defaults to what the prompt and state say (<see cref="Attention.AsksPermission"/> knows better).</summary>
    public static Sign Waiting(IReadOnlyList<OrcaAgent> waiting, PermissionPrompt? prompt, DateTimeOffset since, DateTimeOffset now,
        AgentQuestion? question = null, bool? isPermission = null)
    {
        var first = waiting[0];
        // Orca reports permission dialogs as "waiting" too; the screen tells them apart.
        var permission = isPermission ?? (question is null && (prompt is not null || first.State == "blocked"));
        var waited = (now - since).TotalSeconds;
        // An agent Clawd can't message is answered in Orca; the click shows what it asks.
        var hint = waited < 60 ? Strings.Get(first.CanMessage ? "Sign_JustStartedWaiting" : "Sign_JustStartedWaitingView")
            : Strings.Format(first.CanMessage ? "Sign_WaitingHint" : "Sign_WaitingHintView", Text.Duration(waited));
        if (waiting.Count > 1) hint = Strings.Format("Sign_MoreWaiting", waiting.Count - 1, hint);
        var detail = !permission ? question?.Items[0].Question ?? first.Ask
            : prompt is { Detail.Count: > 0 } p ? p.Detail[^1] : first.Ask;
        return new Sign(SignTone.Urgent, permission ? SignSymbol.Hand : SignSymbol.Bubble,
            Strings.Get(permission ? "Sign_Permission" : "Sign_Reply"), first.Name, detail, hint);
    }

    /// <summary>A finished task.</summary>
    public static Sign Done(OrcaAgent a, TimeSpan? took)
    {
        var hint = took is { } t ? Strings.Format(a.CanMessage ? "Sign_DoneHint" : "Sign_DoneHintView", Text.Duration(t.TotalSeconds))
            : Strings.Get(a.CanMessage ? "Sign_DoneHintNoTime" : "Sign_DoneHintNoTimeView");
        return new(SignTone.Done, SignSymbol.Check, Strings.Get("Sign_Done"), a.Name, Text.Snippet(a.LastMessage, 160), hint);
    }

    /// <summary>The hover summary: working agents and recent completions.</summary>
    public static Sign Summary(bool available, bool enabled, IReadOnlyList<OrcaAgent> working,
        IEnumerable<Attention.FinishedTask> finished, DateTimeOffset now)
    {
        if (!available) return new Sign(SignTone.Info, SignSymbol.Warning, Strings.Get("Sign_NoOrca"));
        if (!enabled) return new Sign(SignTone.Info, SignSymbol.Pause, Strings.Get("Sign_Off"));
        var lines = working.Take(4).Select(a => a.Name + (a.Tool is { } t ? $"  {t}" : "")).ToList();
        lines.AddRange(finished.OrderByDescending(f => f.At).Take(3).Select(f => Strings.Format("Sign_FinishedLine", f.Agent.Name, Text.Ago(f.At, now))));
        if (lines.Count == 0) return new Sign(SignTone.Info, SignSymbol.Moon, Strings.Get("Sign_AllResting"));
        return new Sign(SignTone.Info, working.Count > 0 ? SignSymbol.Hammer : SignSymbol.CheckCircle,
            working.Count > 0 ? Strings.Format("Sign_Working", working.Count) : Strings.Get("Sign_RecentlyFinished"), Detail: string.Join("\n", lines));
    }

    /// <summary>The notification-area icon shows only the most pressing state.</summary>
    public static string StatusTitle(bool enabled, int waiting, int working, int finished)
    {
        if (!enabled) return "";
        if (waiting > 0) return Strings.Format("Status_Waiting", waiting);
        if (working > 0) return Strings.Format("Status_Working", working);
        return finished > 0 ? Strings.Format("Status_Finished", finished) : "";
    }
}
