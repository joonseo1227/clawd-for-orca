using Clawd.Core;
using Clawd.Core.Bridge;
using Clawd.Core.Chat;
using Clawd.Core.Orca;
using Clawd.Terminal;

namespace Clawd;

// The live terminal: Orca's terminal stream through the bridge, into xterm.js in WebView2.
internal sealed partial class AppController
{
    private OrcaBridge? _bridge;
    private LiveTerminalView? _live;
    private (string Id, string Pane)? _liveSub;     // terminal stream feeding the live view
    private OrcaAgent? _liveAgent;
    private (int Cols, int Rows)? _liveSize;
    private double _liveClosedAt = double.NegativeInfinity;   // when Orca last ended the live stream
    private (string Pane, double Until)? _liveReconnect;      // retrying after Orca ended it
    private const double LiveReconnectFor = 60;

    /// <summary>One long-lived view: WebView2 takes a moment to start, so it is created once and moved
    /// into the terminal tab whenever that shows.</summary>
    private void StartLive()
    {
        _bridge = new OrcaBridge(Credentials, () => Orca.Installation,
            Path.Combine(AppContext.BaseDirectory, "Bridge", "clawd-bridge.js"), SynchronizationContext.Current);
        _live = new LiveTerminalView();
        _live.Input += keys => { if (_liveAgent is { } a) Orca.SendKeys(keys, a); };
        _live.Resized += (cols, rows) => { _liveSize = (cols, rows); UpdateFit(); };
        _live.Reloaded += () =>
        {
            // The renderer crashed and the page reloaded: subscribe again for a fresh screen.
            if (_liveSub is { } sub) _bridge.Unsubscribe(sub.Id);
            _liveSub = null;
            UpdateLive();
        };
        // Without WebView2 the view stays empty: say why rather than "Connecting…" for ever.
        _live.InitFailed += error =>
        {
            if (_liveSub is { } sub) _bridge.Unsubscribe(sub.Id);
            _liveSub = null;
            _liveAgent = null;
            Model.LiveStatus = L.Format("Live_ViewFailed", error);
        };
        _live.Shortcut += key =>
        {
            if (key == "w") _chat.Close();
            else Model.Mode = ChatMode.Chat;
        };
        _chat.LiveTerminalHost.Content = _live;
    }

    /// <summary>Starts or stops the live stream to match what the chat shows.</summary>
    private async void UpdateLive()
    {
        if (_bridge is null || _live is null) return;
        var want = _chat.IsOpen && Model.ShowsTerminal && Model.Live ? Model.Current?.Agent : null;
        if (_liveSub is { } sub && sub.Pane != want?.PaneKey)
        {
            _bridge.Unsubscribe(sub.Id);
            _liveSub = null;
            _liveAgent = null;
        }
        if (want is not { } agent || _liveSub is not null) return;
        if (_live.InitError is { } error)
        {
            Model.LiveStatus = L.Format("Live_ViewFailed", error);
            return;
        }
        _liveAgent = agent;
        await _live.Clear();   // a terminal that fails to connect never shows the previous one
        var reconnecting = _liveReconnect is { } r && r.Pane == agent.PaneKey && Clock.Uptime < r.Until;
        Model.LiveStatus = L.Get(reconnecting ? "Live_Reconnecting" : "Live_Connecting");
        // Each attempt has its own token: switching A → B → A quickly leaves two lookups for A in
        // flight, and only the latest may subscribe, or A's output would be written twice.
        var attempt = "pending-" + Guid.NewGuid().ToString("N");
        _liveSub = (attempt, agent.PaneKey);
        var handle = await Orca.TerminalHandleAsync(agent);
        if (_liveSub?.Id != attempt) return;
        var id = handle is null ? null : _bridge.Subscribe(handle, e => LiveEvent(e, agent.PaneKey));
        if (id is null)
        {
            _liveSub = null;
            if (RetryLive(agent.PaneKey)) return;
            // No handle: the terminal wasn't found. No id: the bridge didn't start, and says why.
            Model.LiveStatus = (handle is null ? null : _bridge.LastError) ?? L.Get("Live_StartFailed");
            return;
        }
        _liveSub = (id, agent.PaneKey);
    }

    private async void LiveEvent(OrcaBridge.Event e, string pane)
    {
        if (_liveSub?.Pane != pane || _live is null) return;
        switch (e)
        {
            case OrcaBridge.Event.Snapshot s:
                Model.LiveStatus = null;
                _liveReconnect = null;
                await _live.Load(s.Text);
                _live.FocusTerminal();
                break;
            case OrcaBridge.Event.Data d:
                await _live.Feed(d.Chunk);
                break;
            case OrcaBridge.Event.Failed f:
                _liveSub = null;
                ForgetLiveHandle(pane);
                // A rejected pairing (revoked in Orca, Orca reinstalled) needs a new code.
                var auth = f.Message.Contains("token", StringComparison.OrdinalIgnoreCase) || f.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
                if (!auth && RetryLive(pane)) break;
                Model.LiveStatus = auth ? L.Get("Live_Expired") : L.Format("Live_Error", f.Message);
                break;
            case OrcaBridge.Event.Closed:
                // Orca ended the stream (restarted, the terminal was replaced): say so over the frozen
                // screen and keep trying for a minute. A stream that keeps closing gets one such round
                // every 30 seconds.
                _liveSub = null;
                ForgetLiveHandle(pane);
                var now = Clock.Uptime;
                if (now - _liveClosedAt <= 30)
                {
                    Model.LiveStatus = L.Get("Live_Closed");
                    break;
                }
                _liveClosedAt = now;
                _liveReconnect = (pane, now + LiveReconnectFor);
                RetryLive(pane);
                break;
        }
    }

    /// <summary>The terminal may have been replaced, or Orca restarted: look its handle up afresh.</summary>
    private void ForgetLiveHandle(string pane)
    {
        if (_liveAgent is { } a && a.PaneKey == pane) OrcaClient.ForgetHandle(a);
    }

    /// <summary>While reconnecting to <paramref name="pane"/>, tries again in a moment and returns true;
    /// false once the time for it is up (or there was no reconnect), for the caller to say what went wrong.</summary>
    private bool RetryLive(string pane)
    {
        if (_liveReconnect is not { } r || r.Pane != pane || Clock.Uptime >= r.Until)
        {
            _liveReconnect = null;
            return false;
        }
        Model.LiveStatus = L.Get("Live_Reconnecting");
        After(2, () =>
        {
            if (_liveSub is not null || Model.Current?.Id != pane) return;
            // Orca isn't back yet: without its pipe no terminal would be found.
            if (!Orca.Running)
            {
                if (!RetryLive(pane)) Model.LiveStatus = L.Get("Live_Closed");
                return;
            }
            UpdateLive();
        });
        return true;
    }

    private void StopLive()
    {
        _bridge?.Stop();
        _live?.Close();
    }
}
