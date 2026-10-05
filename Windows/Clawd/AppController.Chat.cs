using Clawd.Core;
using Clawd.Core.Chat;
using Clawd.Core.Orca;
using Clawd.Core.Pet;
using Clawd.Core.Transcripts;
using Clawd.Native;
using Clawd.Services;

namespace Clawd;

// Attention: which agents need the user, the card above Clawd, and the chat window's data.
internal sealed partial class AppController
{
    private bool _readingTranscript, _readingScreen;
    private readonly Dictionary<string, (string Path, double Found)> _transcripts = [];   // pane -> transcript
    private (OrcaAgent Agent, int Cols, int Rows)? _fitted;   // terminal currently sized to Clawd

    public List<OrcaAgent> Pending => Attention.Pending(Orca.Waiting, DateTimeOffset.Now);

    public OrcaAgent? Agent(string key) =>
        Orca.Agents.FirstOrDefault(a => a.PaneKey == key) ?? (Attention.Finished.TryGetValue(key, out var f) ? f.Agent : null);

    private void OrcaChanged(IReadOnlyList<OrcaAgent> old, IReadOnlyList<OrcaAgent> current)
    {
        foreach (var e in Attention.Changed(old, current, DateTimeOffset.Now))
        {
            switch (e)
            {
                case AttentionEvent.Waiting w:
                    var reading = LoadPrompt(w.Agent);
                    if (w.Looking) break;
                    Nudge();
                    if (ShouldNotify) NotifyWaiting(w.Agent, reading);
                    break;
                case AttentionEvent.Done d:
                    AnnounceDone(d.Agent, d.Took);
                    break;
                case AttentionEvent.Nudge:
                    Nudge();
                    break;
            }
        }
        UpdateStatus();
        RefreshChat();
    }

    /// <summary>A toast for an agent that started waiting, once its screen was read: Orca reports a
    /// permission dialog as "waiting", like a question, and a question's toast takes a reply, which
    /// would be typed into the dialog.</summary>
    private async void NotifyWaiting(OrcaAgent a, Task reading)
    {
        await reading;
        var permission = Attention.Permissions.ContainsKey(a.PaneKey) || a.State == "blocked";
        Notifier.Post(permission ? Notifier.Category.Permission : Notifier.Category.Question, a,
            L.Get(permission ? "Toast_Permission" : "Toast_Reply"), a.Ask, Settings.Sound);
    }

    private void Nudge()
    {
        _pet.Alert();
        if (Settings.Sound) Sounds.Nudge();
    }

    private void AnnounceDone(OrcaAgent a, TimeSpan? took)
    {
        _flash = (Sign.Done(a, took), Clock.Uptime + 20, () => OpenChat(a.PaneKey));
        _pet.Celebrate();
        if (Settings.Sound) Sounds.Done();
        if (ShouldNotify) Notifier.Post(Notifier.Category.Done, a, L.Get("Toast_Done"), Text.Snippet(a.LastMessage, 200), false);
    }

    /// <summary>A short confirmation card.</summary>
    public void Say(string text, SignSymbol symbol = SignSymbol.Info, double seconds = 2.5) =>
        _flash = (new Sign(SignTone.Info, symbol, text), Clock.Uptime + seconds, null);

    /// <summary>Picks the one card worth showing right now: waiting agents beat news beat the hover summary.</summary>
    private void RefreshSign(bool hovering)
    {
        if (Hidden) { _sign.Set(null); return; }
        var now = DateTimeOffset.Now;
        var waiting = Pending;
        if (waiting.Count > 0)
        {
            var first = waiting[0];
            var prompt = Attention.Permissions.GetValueOrDefault(first.PaneKey);
            _sign.Set(Sign.Waiting(waiting, prompt, Attention.Since(first, now), now), () => OpenChat(first.PaneKey), () =>
            {
                // Seen it: no card or nagging for this request until the agent's state changes.
                foreach (var a in waiting) Attention.Acknowledged[a.PaneKey] = a.State;
                UpdateStatus();
            });
            return;
        }
        if (_flash is { } f && Clock.Uptime < f.Until)
        {
            _sign.Set(f.Sign, f.Action, () => _flash = null);
            return;
        }
        _flash = null;
        _sign.Set(hovering ? Sign.Summary(Orca.Available, Orca.Enabled, Orca.Working.ToList(), Attention.Finished.Values, now) : null,
            hovering ? () => OpenChat() : null, () => _hoverDismissed = true);
    }

    // MARK: Actions

    public void Open(OrcaAgent a)
    {
        Orca.Focus(a);
        Notifier.Clear(a.PaneKey);
        Attention.Handled(a);
        UpdateStatus();
    }

    /// <summary>"Open in Orca" and "Reply in Orca" in the chat: the agent as it is now, and the chat
    /// steps aside, as on the Mac.</summary>
    public void OpenFromChat(OrcaAgent a)
    {
        Open(Agent(a.PaneKey) ?? a);
        _chat.Close();
    }

    /// <summary>Double-click on Clawd: Orca, on whichever agent matters most right now.</summary>
    public void OpenOrca()
    {
        _chat.Close();
        if ((Pending.FirstOrDefault() ?? Orca.Waiting.FirstOrDefault()) is { } a) Open(a);
        else if (Model.Selected is { } key && Agent(key) is { } selected) Open(selected);
        else Orca.Launch();
    }

    /// <summary>A notification was answered: reply in place, or open the agent in Clawd or Orca.</summary>
    private async void NotificationResponse(string pane, Notifier.Action action, string? text)
    {
        if (Agent(pane) is not { } a) return;
        switch (action)
        {
            case Notifier.Action.Reply:
                var message = text?.Trim() ?? "";
                if (message.Length == 0) return;
                if (await Orca.SendAsync(message, a)) Handled(a);
                else Notifier.Post(Notifier.Category.Question, a, L.Get("Toast_SendFailed"), L.Get("Toast_SendFailedBody"), false);
                break;
            case Notifier.Action.Open:
                Open(a);
                break;
            default:
                OpenChat(a.PaneKey, fromTray: Hidden);
                break;
        }
    }

    /// <summary>After answering one agent, move straight on to the next one that is waiting.</summary>
    private void Handled(OrcaAgent a)
    {
        Attention.Handled(a);
        Notifier.Clear(a.PaneKey);
        _pet.Poked();
        UpdateStatus();
        RefreshChat();
        if (Pending.FirstOrDefault(p => p.PaneKey != a.PaneKey) is { } next) Model.Selected = next.PaneKey;
    }

    // MARK: Chat

    /// <summary>Clicking Clawd toggles the chat. A click that just dismissed it must not reopen it.</summary>
    public void ToggleChat()
    {
        if (_chat.IsOpen) _chat.Close();
        else if (!_chat.JustClosed) OpenChat();
    }

    /// <summary>The hotkey or a click on Clawd: open the chat on <paramref name="key"/>, or on whoever needs the user most.</summary>
    public void OpenChat(string? key = null, bool fromTray = false)
    {
        Model.Notice = null;
        RefreshChat();
        if (key is not null) Model.Selected = key;
        else if (TestHooks.Select is { } debug) Model.Selected = debug;
        else if ((Pending.FirstOrDefault() ?? Orca.Waiting.FirstOrDefault()) is { } first) Model.Selected = first.PaneKey;
        else if (Model.Selected is null || Model.Current is null) Model.Selected = Model.Rows.FirstOrDefault()?.Id;
        // Open the resting section when there is nothing else to show or a resting agent is picked.
        Model.ShowResting = Model.Rows.All(r => r.Kind == RowKind.Resting) || Model.Current?.Kind == RowKind.Resting;
        SelectionChanged(Model.Selected);
        if (TestHooks.Draft is { } draft) Model.Draft = draft;
        _ = RefreshTitles();

        if (Hidden || fromTray)
        {
            // Above the notification-area icon, or above the taskbar's corner if it can't be found.
            var at = _tray.Location() ?? CornerOfTaskbar();
            _chat.ShowAbove(at);
        }
        else
        {
            // Over the top of Clawd's head.
            var r = _petWindow.Bounds;
            var unit = _space.UnitPixels;
            _chat.ShowAbove(new Win32.Rect { Left = r.Left + 5 * unit, Right = r.Left + 15 * unit, Top = r.Top + 10 * unit, Bottom = r.Top + 10 * unit + 1 });
        }
    }

    private static Win32.Rect CornerOfTaskbar()
    {
        var m = Monitors.At(Monitors.Cursor);
        return new Win32.Rect { Left = m.Work.Right - 40, Right = m.Work.Right - 8, Top = m.Work.Bottom, Bottom = m.Work.Bottom + 1 };
    }

    private async Task RefreshTitles()
    {
        _titles = await Orca.TitlesAsync();
        RefreshChat();
    }

    public void RefreshChat()
    {
        Model.Connection = !Orca.Available ? Connection.NotInstalled : !Orca.Enabled ? Connection.Off : !Orca.Running ? Connection.NotRunning : Connection.Connected;
        var rows = Attention.Rows(Orca.Agents, _titles, DateTimeOffset.Now);
        Model.Rows = rows;
        // The selected agent went away (tab closed): move on to the first one, which also moves
        // the terminal fit off the dead pane.
        if (Model.Selected is { } selected && rows.All(r => r.Id != selected)) Model.Selected = rows.FirstOrDefault()?.Id;
        Model.Prompt = Model.Current is { } c ? Attention.Permissions.GetValueOrDefault(c.Id) : null;
    }

    private void SelectionChanged(string? key)
    {
        Model.Screen = new ScreenState.Loading();
        Model.Timeline = [];
        Model.TimelineReady = false;
        _timelinePollIn = 0;
        _screenPollIn = 0;
        Model.Composing = "";
        // Deferred, as on the Mac: OpenChat calls this before showing the window, and with the
        // chat not yet open neither the terminal fit nor the live stream would start.
        Dispatcher.TryEnqueue(() => { UpdateFit(); UpdateLive(); });
        RefreshChat();
        if (Model.Current is { } row && row.Agent.NeedsYou) _ = LoadPrompt(row.Agent);
        // Reading a finished agent's reply counts as seeing it: drop it from the count.
        if (key is not null && Attention.Finished.Remove(key)) UpdateStatus();
    }

    /// <summary>Follows the selected agent's Claude Code transcript. The file is only re-read when it
    /// grew, and the session lookup is cached for a while since panes rarely change session.</summary>
    private async void PollTimeline()
    {
        if (_readingTranscript) return;   // the last read is still running
        if (Model.Current?.Agent is not { } a || a.AgentType is not ("claude" or ""))
        {
            // Only Claude Code writes a transcript; other agents get the summary view.
            Model.Timeline = [];
            Model.TimelineReady = true;
            return;
        }
        _readingTranscript = true;
        var key = a.PaneKey;
        var now = Clock.Uptime;
        var cached = _transcripts.TryGetValue(key, out var t) && now - t.Found < 10 ? t.Path : null;
        var showingNothing = Model.Timeline.Count == 0;
        string? path = null;
        IReadOnlyList<TimelineItem>? items = null;
        try
        {
            (path, items) = await Task.Run(() =>
            {
                var p = cached ?? TestHooks.FakeTranscript(a.PaneKey) ?? Transcripts.Locate(a.PaneKey, a.Path, a.Prompt);
                if (p is null) return (p, (IReadOnlyList<TimelineItem>?)null);
                // Only what was appended since the last read is parsed; null when nothing changed.
                var read = Transcripts.Reader(p).Timeline();
                return (p, read.Changed || showingNothing ? read.Items : null);
            });
        }
        catch (Exception e) { Log.Error($"transcript: {e.Message}"); }
        _readingTranscript = false;
        if (path is not null && cached is null) _transcripts[key] = (path, now);
        if (Model.Current?.Id != key) return;
        Model.TimelineReady = true;
        // A lookup that misses once (session file being rewritten) must not blank the view.
        if (path is null && cached is null && !_transcripts.ContainsKey(key)) Model.Timeline = [];
        if (items is not null) Model.Timeline = items;
    }

    /// <summary>Refreshes the terminal view; one read at a time so a slow CLI never piles up.</summary>
    private async void PollScreen()
    {
        if (_readingScreen || Model.Current?.Agent is not { } a) return;
        _readingScreen = true;
        var lines = await Orca.ScreenAsync(a);
        _readingScreen = false;
        if (Model.Current?.Id != a.PaneKey) return;   // selection moved on
        ScreenState next = lines is null ? new ScreenState.Failed() : new ScreenState.Lines(lines);
        if (!Equals(next, Model.Screen))
        {
            Model.Screen = next;
            _lastScreenChange = Clock.Uptime;
        }
    }

    public void SendKeys(string keys, OrcaAgent a)
    {
        Orca.SendKeys(keys, a);
        _screenPollIn = Math.Min(_screenPollIn, 0.05);   // echo the keystroke right away
        _lastScreenChange = Clock.Uptime;
    }

    /// <summary>Sizes the shown terminal to the chat while the terminal view is open, and gives it back
    /// to Orca (which restores its own size) as soon as it isn't.</summary>
    public void UpdateFit()
    {
        (OrcaAgent Agent, int Cols, int Rows)? target = null;
        // Live: the emulator's own grid. Otherwise the cell grid the text view draws with.
        var grid = Model.Live ? _liveSize : _chat.TerminalGrid;
        if (_chat.IsOpen && Model.ShowsTerminal && Model.Current?.Agent is { } agent && grid is { } g)
            target = (agent, g.Cols, g.Rows);
        if (_fitted is { } old && old.Agent.PaneKey != target?.Agent.PaneKey)
        {
            _ = Orca.RestoreSize(old.Agent);
            _fitted = null;
            Model.FittedRows = null;
        }
        if (target is { } t && (_fitted is null || _fitted.Value.Cols != t.Cols || _fitted.Value.Rows != t.Rows))
        {
            Orca.Fit(t.Agent, t.Cols, t.Rows);
            _fitted = t;
            Model.FittedRows = t.Rows;
            _screenPollIn = 0.2;
        }
    }

    /// <summary>Reads the agent's screen to see whether it is showing a permission dialog.</summary>
    private async Task LoadPrompt(OrcaAgent a)
    {
        if (!a.HasTerminal) return;   // answered in Orca
        Model.SetBusy(a.PaneKey, true);
        var lines = await Orca.ScreenAsync(a);
        var prompt = lines is null ? null : PermissionPrompt.Parse(lines);
        if (prompt is null) Attention.Permissions.Remove(a.PaneKey); else Attention.Permissions[a.PaneKey] = prompt;
        Model.SetBusy(a.PaneKey, false);
        RefreshChat();
    }

    public async void ChatSend(string text, OrcaAgent a)
    {
        Model.SetBusy(a.PaneKey, true);
        var ok = await Orca.SendAsync(text, a);
        Model.SetBusy(a.PaneKey, false);
        if (ok)
        {
            Model.SetDraft(a.PaneKey, null);
            Orca.PollSoon();
            Model.Notice = ChatNotice.Success(L.Format("Notice_Sent", a.Name));
            _screenPollIn = 0.3;
            Handled(a);
        }
        else
        {
            Model.Notice = ChatNotice.Failure(L.Get("Notice_SendFailed"));
        }
    }

    public async void ChatAnswer(int number, OrcaAgent a)
    {
        if (!Attention.Permissions.TryGetValue(a.PaneKey, out var prompt)) return;
        var title = prompt.Options.FirstOrDefault(o => o.Number == number)?.Title ?? L.Format("Notice_OptionNumber", number);
        Model.SetBusy(a.PaneKey, true);
        var result = await Orca.AnswerAsync(number, a, prompt);
        Model.SetBusy(a.PaneKey, false);
        Orca.PollSoon();
        switch (result)
        {
            case OrcaWatcher.AnswerResult.Sent:
                Model.Notice = ChatNotice.Success($"{a.Name}: {title}");
                Attention.Permissions.Remove(a.PaneKey);
                Handled(a);
                break;
            case OrcaWatcher.AnswerResult.Gone:
                // Someone answered it elsewhere (Orca, phone) or it timed out; nothing was sent.
                Model.Notice = ChatNotice.Info(L.Get("Notice_AlreadyHandled"));
                Attention.Permissions.Remove(a.PaneKey);
                RefreshChat();
                _ = LoadPrompt(a);
                break;
            default:
                Model.Notice = ChatNotice.Failure(L.Get("Notice_AnswerFailed"));
                break;
        }
    }

    /// <summary>Files dropped on Clawd: open the chat on whoever needs the user most, with the paths in the message.</summary>
    public void DropFiles(IReadOnlyList<string> paths)
    {
        var text = FileDrop.Text(paths);
        if (text.Length == 0) return;
        OpenChat();
        // A session in Orca's chat takes nothing from Clawd: the paths would be stuck in a draft
        // that can be neither sent nor cleared.
        if (Model.Current is not { Agent.HasTerminal: true, Id: var id }) return;
        Model.Mode = ChatMode.Chat;
        Model.SetDraft(id, FileDrop.Append(text, Model.Drafts.GetValueOrDefault(id) ?? ""));
    }
}
