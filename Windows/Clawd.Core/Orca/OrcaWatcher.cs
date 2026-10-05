using System.Diagnostics;

namespace Clawd.Core.Orca;

/// <summary>
/// Follows the agents running in Orca and acts on their terminals. Everything goes through
/// Orca's runtime pipe; the orca CLI is only a fallback for when the pipe can't be reached.
/// Call it from the UI thread: awaits resume there, so events arrive on the UI thread too.
/// </summary>
public sealed class OrcaWatcher
{
    private readonly OrcaRuntime _runtime;
    private OrcaClient? _client;
    private bool _hasBaseline;
    private bool _polling;
    private double _nextPoll;
    private double _nextLocate;
    // Keystrokes, fits and restores share one queue so they reach Orca in order.
    private readonly SerialQueue _keyQueue = new();

    /// <summary>How often to ask Orca for agent states; slower while Orca is closed.</summary>
    public const double PollInterval = 2;
    public const double IdlePollInterval = 10;

    public OrcaWatcher(OrcaRuntime? runtime = null)
    {
        _runtime = runtime ?? new OrcaRuntime(OrcaRuntime.DefaultMetadataPath);
        Installation = OrcaInstallation.Locate();
    }

    /// <summary>Looked up at launch and again while missing, so installing Orca later just works.</summary>
    public OrcaInstallation? Installation { get; private set; }
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<OrcaAgent> Agents { get; private set; } = [];
    /// <summary>False while Orca isn't running (its pipe is gone).</summary>
    public bool Running { get; private set; } = true;

    /// <summary>Raised with (previous, current) after every successful poll past the first.</summary>
    public event Action<IReadOnlyList<OrcaAgent>, IReadOnlyList<OrcaAgent>>? Changed;
    /// <summary>Raised when Orca starts or quits.</summary>
    public event Action<bool>? RunningChanged;

    /// <summary>Opens Orca or brings it to the front; the app supplies the Windows-specific part.</summary>
    public Action<OrcaInstallation>? Launcher { get; set; }

    public bool Available => Installation is not null || TestHooks.FakeOrca is not null;
    public IEnumerable<OrcaAgent> Working => Agents.Where(a => a.State == "working");
    public IEnumerable<OrcaAgent> Waiting => Agents.Where(a => a.NeedsYou);

    public OrcaClient Client
    {
        get
        {
            var cli = Installation?.Cli;
            if (_client is null || _client.Cli != cli) _client = new OrcaClient(_runtime, cli);
            return _client;
        }
    }

    public void Poll()
    {
        var now = Clock.Uptime;
        if (Installation is null && now > _nextLocate)
        {
            _nextLocate = now + 30;
            Installation = OrcaInstallation.Locate();
        }
        if (!Enabled || !Available || _polling || now < _nextPoll) return;
        _polling = true;
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        var client = Client;
        IReadOnlyList<OrcaAgent>? parsed;
        try { parsed = await Task.Run(client.WorktreesAsync); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"poll failed: {e.Message}");
            parsed = null;
        }
        _polling = false;
        if (!Enabled) return;
        var isRunning = parsed is not null;
        _nextPoll = Clock.Uptime + (isRunning ? PollInterval : IdlePollInterval);
        if (isRunning != Running)
        {
            Running = isRunning;
            RunningChanged?.Invoke(isRunning);
        }
        var next = parsed ?? [];
        var old = Agents;
        Agents = next;
        if (_hasBaseline && !old.SequenceEqual(next)) Changed?.Invoke(old, next);
        _hasBaseline = true;
    }

    /// <summary>Ask again right away (after an action that changes an agent's state).</summary>
    public void PollSoon() => _nextPoll = 0;

    public void Stop()
    {
        Enabled = false;
        Agents = [];
        _hasBaseline = false;
    }

    /// <summary>Opens Orca, or brings it to the front.</summary>
    public void Launch()
    {
        if (Installation is not { } orca) return;
        if (Launcher is { } launcher) { launcher(orca); return; }
        if (orca.Executable is null) return;
        try { Process.Start(new ProcessStartInfo(orca.Executable) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error($"launch failed: {e.Message}"); }
    }

    /// <summary>Brings the agent's terminal to the front in Orca.</summary>
    public void Focus(OrcaAgent agent)
    {
        var client = Client;
        _ = Task.Run(() => client.FocusAsync(agent));
        Launch();
    }

    /// <summary>Types <paramref name="text"/> into the agent's terminal and presses Enter.</summary>
    public Task<bool> SendAsync(string text, OrcaAgent agent)
    {
        var client = Client;
        return Task.Run(() => client.SendAsync(text, agent, enter: true));
    }

    /// <summary>Raw keystrokes (no Enter appended) for the terminal view.</summary>
    public void SendKeys(string text, OrcaAgent agent)
    {
        var client = Client;
        _keyQueue.Enqueue(() => client.SendAsync(text, agent, enter: false));
    }

    /// <summary>Resizes the agent's terminal to Clawd's viewport, the way Orca's phone app does.
    /// Orca remembers the desktop size and puts it back on RestoreSize.</summary>
    public void Fit(OrcaAgent agent, int cols, int rows)
    {
        var client = Client;
        _keyQueue.Enqueue(() => client.ResizeAsync(agent, "mobile-fit", cols, rows));
    }

    /// <summary>Completes once Orca has the size back; on quit the app waits for it, briefly.</summary>
    public Task RestoreSize(OrcaAgent agent)
    {
        var client = Client;
        return _keyQueue.Enqueue(() => client.ResizeAsync(agent, "restore"));
    }

    public enum AnswerResult { Sent, Gone, Failed }

    /// <summary>Presses an option number in the agent's permission prompt. Claude Code acts on the digit
    /// alone, so the screen is re-read first: if that exact prompt is no longer showing, the digit
    /// would land in the agent's input box instead, and nothing is sent.</summary>
    public Task<AnswerResult> AnswerAsync(int number, OrcaAgent agent, PermissionPrompt prompt)
    {
        var client = Client;
        return _keyQueue.Enqueue(async () =>
        {
            var screen = await client.ScreenAsync(agent).ConfigureAwait(false);
            if (screen is null || !Equals(PermissionPrompt.Parse(screen), prompt)) return AnswerResult.Gone;
            return await client.SendAsync(number.ToString(System.Globalization.CultureInfo.InvariantCulture), agent, enter: false).ConfigureAwait(false)
                ? AnswerResult.Sent : AnswerResult.Failed;
        });
    }

    /// <summary>Picks an answer in a chat session's permission request.</summary>
    public Task<AnswerResult> ApproveAsync(int number, SessionApproval approval, OrcaAgent agent)
    {
        var client = Client;
        if (agent.SessionId is not { } id) return Task.FromResult(AnswerResult.Failed);
        return _keyQueue.Enqueue(() => client.SessionApproveAsync(approval, number, id));
    }

    /// <summary>Answers Claude's questions: through a chat session, or by working the terminal's dialog
    /// the way a person would, one question at a time. Before the first key the dialog has to show the
    /// first question, and each step has to land before the next is typed: a key that misses the
    /// dialog would go to the agent's input box instead.</summary>
    public Task<AnswerResult> AnswerAsync(AgentQuestion question, IReadOnlyList<AgentQuestion.Answer> answers, OrcaAgent agent)
    {
        var client = Client;
        return _keyQueue.Enqueue(() => agent.SessionId is { } id
            ? client.SessionAnswerAsync(question, answers, id)
            : TypeAnswersAsync(question, answers, client, agent));
    }

    private static async Task<AnswerResult> TypeAnswersAsync(AgentQuestion question, IReadOnlyList<AgentQuestion.Answer> answers,
        OrcaClient client, OrcaAgent agent)
    {
        if (question.Items.Count == 0 || await client.ScreenAsync(agent).ConfigureAwait(false) is not { } screen) return AnswerResult.Failed;
        var first = question.Items[0];
        // Reads the screen until `done` holds; false after a couple of seconds without it.
        async Task<bool> Wait(Func<IReadOnlyList<string>, bool> done)
        {
            for (var i = 0; i < 25; i++)
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (await client.ScreenAsync(agent).ConfigureAwait(false) is { } s && done(s)) return true;
            }
            return false;
        }
        async Task<bool> Key(string key)
        {
            var ok = await client.SendAsync(key, agent, enter: false).ConfigureAwait(false);
            await Task.Delay(120).ConfigureAwait(false);   // Claude Code reads each key on its own
            return ok;
        }
        // Someone may have stepped through the dialog in Orca already: go back to the start.
        if (!QuestionDialog.Showing(first, screen))
        {
            if (!QuestionDialog.Reviewing(screen) && !question.Items.Any(i => QuestionDialog.Showing(i, screen))) return AnswerResult.Gone;
            foreach (var _ in question.Items) if (!await Key(QuestionDialog.Previous).ConfigureAwait(false)) return AnswerResult.Failed;
            if (!await Wait(s => QuestionDialog.Showing(first, s)).ConfigureAwait(false)) return AnswerResult.Failed;
        }
        for (var i = 0; i < question.Items.Count; i++)
        {
            var item = question.Items[i];
            var ticked = item.MultiSelect && await client.ScreenAsync(agent).ConfigureAwait(false) is { } now ? QuestionDialog.Ticked(now) : [];
            if (QuestionDialog.Keys(item, answers[i], ticked) is not { } keys) return AnswerResult.Failed;
            foreach (var key in keys) if (!await Key(key).ConfigureAwait(false)) return AnswerResult.Failed;
            var next = i + 1 < question.Items.Count ? question.Items[i + 1] : null;
            if (!await Wait(s => next is not null ? QuestionDialog.Showing(next, s) : !QuestionDialog.Showing(item, s)).ConfigureAwait(false))
                return AnswerResult.Failed;
        }
        // Several questions, or a multiple-choice one, end on a review to confirm.
        if (await client.ScreenAsync(agent).ConfigureAwait(false) is { } end && QuestionDialog.Reviewing(end))
        {
            if (!await client.SendAsync("1", agent, enter: false).ConfigureAwait(false)) return AnswerResult.Failed;
            if (!await Wait(s => !QuestionDialog.Reviewing(s)).ConfigureAwait(false)) return AnswerResult.Failed;
        }
        return AnswerResult.Sent;
    }

    /// <summary>A chat session's latest turn and what it waits on.</summary>
    public Task<SessionSnapshot?> SessionAsync(OrcaAgent agent)
    {
        var client = Client;
        return agent.SessionId is { } id ? Task.Run(() => client.SessionAsync(id)) : Task.FromResult<SessionSnapshot?>(null);
    }

    /// <summary>The rendered terminal screen, one string per row.</summary>
    public Task<IReadOnlyList<string>?> ScreenAsync(OrcaAgent agent)
    {
        var client = Client;
        return Task.Run(() => client.ScreenAsync(agent));
    }

    /// <summary>Tab titles by pane, used to tell apart agents that share a worktree.</summary>
    public Task<Dictionary<string, string>> TitlesAsync()
    {
        var client = Client;
        return Task.Run(client.TitlesAsync);
    }

    /// <summary>The runtime handle of the agent's terminal pane, for APIs that take one.</summary>
    public Task<string?> TerminalHandleAsync(OrcaAgent agent)
    {
        var client = Client;
        return _keyQueue.Enqueue(() => client.HandleAsync(agent));
    }
}
