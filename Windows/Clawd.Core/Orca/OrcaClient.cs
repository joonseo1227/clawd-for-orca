using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Clawd.Core.Orca;

/// <summary>Orca requests, runtime pipe first with the CLI as fallback. Safe to use from any thread.</summary>
public sealed partial class OrcaClient(OrcaRuntime runtime, string? cli)
{
    /// <summary>Identifies Clawd's terminal size override to Orca, which keeps one per client.</summary>
    public const string ClientId = "com.joonseo1227.clawd-for-orca.windows";

    public OrcaRuntime Runtime { get; } = runtime;
    public string? Cli { get; } = cli;

    /// <summary>Sends a request; falls back to the CLI only when the pipe was unreachable, never after
    /// Orca received it, so a keystroke is never typed twice.</summary>
    private async Task<JsonObject?> RequestAsync(string method, JsonObject parameters, string[]? cliArgs)
    {
        var outcome = await Runtime.RequestAsync(method, parameters).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case RuntimeStatus.Ok:
                return outcome.Frame;
            case RuntimeStatus.Unreachable:
                if (Cli is null || cliArgs is null) return null;
                // A clean exit is success, as on the Mac: `terminal send` and `terminal switch` run
                // without --json and print a line of text, not a frame.
                return await CommandLine.RunAsync(Cli, cliArgs).ConfigureAwait(false) is { } output
                    ? Json.ParseObject(output) ?? new JsonObject()
                    : null;
            default:
                Log.Debug(() => $"{method} failed: {outcome.Code ?? "-"} {outcome.Message ?? ""}");
                return null;
        }
    }

    public async Task<IReadOnlyList<OrcaAgent>?> WorktreesAsync()
    {
        if (TestHooks.FakeOrca is { } fake)
        {
            try { return OrcaAgent.Parse(Json.ParseObject(await File.ReadAllBytesAsync(fake).ConfigureAwait(false))); }
            catch (IOException) { return null; }
        }
        // The pipe only: starting the CLI every two seconds while Orca is closed would cost a
        // Node launch each time for nothing.
        var outcome = await Runtime.RequestAsync("worktree.ps", new JsonObject()).ConfigureAwait(false);
        return outcome.Status == RuntimeStatus.Ok ? OrcaAgent.Parse(outcome.Frame) : null;
    }

    public async Task<IReadOnlyList<JsonObject>> TerminalsAsync()
    {
        // Demo agents have no terminals. Without this, sends, answers and resizes in the demo would
        // be matched against the real Orca's terminals.
        if (TestHooks.FakeOrca is not null) return [];
        var frame = await RequestAsync("terminal.list", new JsonObject(), ["terminal", "list", "--json"]).ConfigureAwait(false);
        return OrcaRuntime.Result(frame).Objects("terminals").ToList();
    }

    public async Task FocusAsync(OrcaAgent agent)
    {
        // A chat session has no terminal: bring up its workspace, then its tab.
        if (agent.ChatTabId is { } tab)
        {
            if (agent.WorktreeId.Length == 0) return;
            var worktree = "id:" + agent.WorktreeId;
            await RequestAsync("worktree.activate", new JsonObject { ["worktree"] = worktree, ["navigation"] = "host" }, null).ConfigureAwait(false);
            await RequestAsync("session.tabs.activate", new JsonObject { ["worktree"] = worktree, ["tabId"] = tab, ["navigation"] = "host" }, null).ConfigureAwait(false);
            return;
        }
        if (await HandleAsync(agent).ConfigureAwait(false) is not { } handle) return;
        await RequestAsync("terminal.focus", new JsonObject { ["terminal"] = handle, ["navigation"] = "host" },
            ["terminal", "switch", "--terminal", handle]).ConfigureAwait(false);
    }

    /// <summary>Types <paramref name="text"/>, then Enter when <paramref name="enter"/> is set. A
    /// multi-line message goes in as a bracketed paste so its newlines don't submit it halfway.</summary>
    public async Task<bool> SendAsync(string text, OrcaAgent agent, bool enter)
    {
        // A chat session takes whole messages only; there are no keystrokes to send it.
        if (agent.SessionId is { } session) return enter && await SessionSendAsync(text, session).ConfigureAwait(false);
        if (await HandleAsync(agent).ConfigureAwait(false) is not { } handle) return false;
        var payload = enter && text.Contains('\n') ? $"\u001b[200~{text}\u001b[201~" : text;
        string[] args = enter
            ? ["terminal", "send", "--terminal", handle, $"--text={payload}", "--enter"]
            : ["terminal", "send", "--terminal", handle, $"--text={payload}"];
        var ok = await RequestAsync("terminal.send", new JsonObject { ["terminal"] = handle, ["text"] = payload, ["enter"] = enter }, args).ConfigureAwait(false) is not null;
        if (!ok) ForgetHandle(agent);
        return ok;
    }

    public async Task ResizeAsync(OrcaAgent agent, string mode, int? cols = null, int? rows = null)
    {
        if (await HandleAsync(agent).ConfigureAwait(false) is not { } handle) return;
        var p = new JsonObject { ["terminal"] = handle, ["mode"] = mode, ["clientId"] = ClientId };
        if (cols is { } c && rows is { } r) { p["cols"] = c; p["rows"] = r; }
        await RequestAsync("terminal.resizeForClient", p, null).ConfigureAwait(false);
    }

    /// <summary>The rendered screen, with whatever is being typed at the agent's prompt put back in.</summary>
    public async Task<IReadOnlyList<string>?> ScreenAsync(OrcaAgent agent)
    {
        if (TestHooks.FakeScreen(agent.PaneKey) is { } demo) return demo;
        if (await HandleAsync(agent).ConfigureAwait(false) is not { } handle) return null;
        var frame = await RequestAsync("terminal.read", new JsonObject { ["terminal"] = handle, ["screen"] = true },
            ["terminal", "read", "--terminal", handle, "--screen", "--json"]).ConfigureAwait(false);
        var terminal = OrcaRuntime.Result(frame).Obj("terminal");
        if (terminal?["tail"] is not JsonArray)
        {
            ForgetHandle(agent);
            return null;
        }
        return TerminalDraft.Merge(terminal.Str("draft") ?? "", terminal.Strings("tail").ToList());
    }

    // Handles are stable for a pane's lifetime, so look each up once. The terminal view reads the
    // screen several times a second and would otherwise list every terminal each time.
    private static readonly ConcurrentDictionary<string, string> HandleCache = new();

    public async Task<string?> HandleAsync(OrcaAgent agent)
    {
        if (HandleCache.TryGetValue(agent.PaneKey, out var cached)) return cached;
        if (Match(agent, await TerminalsAsync().ConfigureAwait(false)) is not { } handle) return null;
        HandleCache[agent.PaneKey] = handle;
        return handle;
    }

    /// <summary>The terminal an agent runs in. Orca names a terminal by the tab and pane it sits in, but
    /// only for workspaces whose tabs are open in its window; terminals of the others are listed
    /// under "pty:…" placeholders with just their folder. Those match by folder (and agent), and
    /// only when that leaves exactly one: keys must never go to the wrong terminal. A session without a
    /// terminal never matches: by folder it would find another agent's.</summary>
    public static string? Match(OrcaAgent agent, IReadOnlyList<JsonObject> terminals)
    {
        if (!agent.HasTerminal) return null;
        var parts = agent.PaneKey.Split(':', 2);
        if (parts.Length == 2 && terminals.FirstOrDefault(t => t.Str("tabId") == parts[0] && t.Str("leafId") == parts[1]) is { } exact)
            return exact.Str("handle");
        if (agent.Path.Length == 0) return null;
        var candidates = terminals.Where(t =>
            t.Str("tabId")?.StartsWith("pty:", StringComparison.Ordinal) == true
            && t.Str("worktreePath") == agent.Path
            && (agent.AgentType.Length == 0 || t.Str("agentIdentity") == agent.AgentType)).ToList();
        return candidates.Count == 1 ? candidates[0].Str("handle") : null;
    }

    /// <summary>Forget a handle that stopped working (terminal closed or restarted, Orca relaunched).</summary>
    public static void ForgetHandle(OrcaAgent agent) => HandleCache.TryRemove(agent.PaneKey, out _);

    /// <summary>Tab titles by pane, used to tell apart agents that share a worktree.</summary>
    public async Task<Dictionary<string, string>> TitlesAsync()
    {
        var result = new Dictionary<string, string>();
        foreach (var t in await TerminalsAsync().ConfigureAwait(false))
        {
            if (t.Str("tabId") is { } tab && t.Str("leafId") is { } leaf && t.Str("title") is { } title)
                result[$"{tab}:{leaf}"] = Text.WithoutSpinner(title);
        }
        return result;
    }
}
