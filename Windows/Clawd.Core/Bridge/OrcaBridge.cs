using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Clawd.Core.Orca;
using Clawd.Core.Platform;

namespace Clawd.Core.Bridge;

/// <summary>
/// Runs clawd-bridge.js under Orca's bundled Node (ELECTRON_RUN_AS_NODE=1 Orca.exe) and relays
/// terminal subscriptions, speaking newline-delimited JSON over its stdin and stdout. Everything
/// the process reports is posted to the synchronization context given at construction (the UI
/// thread), so state is only ever touched there.
/// </summary>
public sealed class OrcaBridge
{
    public abstract record Event
    {
        public sealed record Snapshot(string Text, int? Cols, int? Rows) : Event;
        public sealed record Data(string Chunk) : Event;
        public sealed record Resized(int Cols, int Rows) : Event;
        public sealed record Failed(string Message) : Event;
        public sealed record Closed : Event;
    }

    /// <summary>One run of the bridge process; events from an earlier run are recognised and dropped.</summary>
    private sealed class Run(Process process, StreamWriter input)
    {
        public Process Process { get; } = process;
        public StreamWriter Input { get; } = input;
        public int PendingEnds = 3;     // stdout EOF, stderr EOF and exit still to come
        public bool Ready;              // the bridge loaded Orca's module and took the pairing
        public readonly StringBuilder StderrTail = new();
    }

    /// <summary>The bridge is a full Node process (~80 MB); quit it once nothing has been streamed for a
    /// while. It starts again in about a second the next time the terminal view opens.</summary>
    public static readonly TimeSpan IdleShutdown = TimeSpan.FromMinutes(2);
    /// <summary>Only the end of stderr is kept: enough for Node's error message, bounded if it chatters.</summary>
    private const int StderrLimit = 4096;

    private readonly IPairingStore _store;
    private readonly Func<OrcaInstallation?> _installation;
    private readonly string _script;
    private readonly SynchronizationContext? _context;
    private Run? _run;
    private readonly HashSet<string> _streaming = [];      // subscriptions that received their first screen
    private readonly Dictionary<string, Action<Event>> _handlers = [];
    private OrcaPairing? _pairing;    // read from the credential store only when the bridge starts
    private bool _paired;
    private CancellationTokenSource? _idle;

    public OrcaBridge(IPairingStore store, Func<OrcaInstallation?> installation, string script, SynchronizationContext? context)
    {
        _store = store;
        _installation = installation;
        _script = script;
        _context = context;
        _paired = !TestHooks.NoLive && store.Exists();
    }

    public bool IsPaired => _paired;
    public string? LastError { get; private set; }

    /// <summary>Returns false when the credential store refused the pairing; nothing changes then.</summary>
    public bool Pair(OrcaPairing pairing)
    {
        if (!_store.Save(pairing))
        {
            LastError = Strings.Get("Bridge_SaveFailed");
            return false;
        }
        Stop();
        _pairing = pairing;
        _paired = true;
        LastError = null;
        return true;
    }

    public void Unpair()
    {
        _store.Clear();
        Stop();
        _pairing = null;
        _paired = false;
    }

    private void Post(Action action)
    {
        if (_context is null) action();
        else _context.Post(_ => action(), null);
    }

    private bool EnsureRunning()
    {
        if (_run is { } current)
        {
            if (!current.Process.HasExited) return true;
            // Exited but its pipes haven't drained yet: settle that run before starting anew.
            EndRun(current);
        }
        if (_pairing is null && _paired) _pairing = _store.Load();
        var orca = _installation();
        if (_pairing is not { } pairing)
        {
            LastError = Strings.Get("Bridge_NoPairing");
            return false;
        }
        if (!File.Exists(_script))
        {
            LastError = Strings.Get("Bridge_NoScript");
            return false;
        }
        if (orca?.Executable is not { } node || orca.SharedModules is not { } shared)
        {
            LastError = Strings.Get("Bridge_NoOrca");
            return false;
        }

        var info = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Node's readline would take a byte-order mark as part of the first JSON line.
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add(_script);
        info.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        info.Environment["ORCA_SHARED_DIR"] = shared;
        // As Orca's own CLI launcher does: a user's NODE_OPTIONS must not change how Orca's Node runs.
        info.Environment.Remove("NODE_OPTIONS");

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Process.Start returned false");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            LastError = e.Message;
            process.Dispose();
            return false;
        }
        ChildProcessJob.Add(process);
        var run = new Run(process, process.StandardInput) { };
        run.Input.AutoFlush = true;
        _run = run;
        _streaming.Clear();
        _ = ReadOutput(run);
        _ = ReadErrors(run);
        process.Exited += (_, _) => Post(() => StreamEnded(run));
        // The pairing goes over stdin, never argv or the environment, which other processes can read.
        Write(new JsonObject
        {
            ["op"] = "pairing",
            ["pairing"] = new JsonObject
            {
                ["endpoint"] = pairing.LocalEndpoint,
                ["deviceToken"] = pairing.DeviceToken,
                ["publicKeyB64"] = pairing.PublicKeyB64,
            },
        });
        return true;
    }

    private async Task ReadOutput(Run run)
    {
        try
        {
            while (await run.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                Post(() => Receive(run, line));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        Post(() => StreamEnded(run));
    }

    private async Task ReadErrors(Run run)
    {
        var buffer = new char[1024];
        try
        {
            int n;
            while ((n = await run.Process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                var chunk = new string(buffer, 0, n);
                Post(() =>
                {
                    run.StderrTail.Append(chunk);
                    if (run.StderrTail.Length > StderrLimit) run.StderrTail.Remove(0, run.StderrTail.Length - StderrLimit);
                });
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        Post(() => StreamEnded(run));
    }

    private void Write(JsonObject message)
    {
        if (_run is not { } run) return;
        try { run.Input.Write(message.ToJsonString() + "\n"); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }   // the bridge died; its exit settles things
    }

    /// <summary>Why the bridge didn't come up, for subscriptions that never got a screen.</summary>
    private static string StartFailure(Run run)
    {
        if (run.Ready) return Strings.Get("Bridge_ClosedWithoutScreen");
        var detail = run.StderrTail.ToString().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return detail is null ? Strings.Get("Bridge_StartFailed") : Strings.Format("Bridge_StartFailedDetail", detail);
    }

    /// <summary>Counts down stdout EOF, stderr EOF and the exit, so every line and the error output are
    /// in before subscriptions are settled.</summary>
    private void StreamEnded(Run run)
    {
        if (!ReferenceEquals(_run, run)) return;
        if (--run.PendingEnds <= 0) EndRun(run);
    }

    /// <summary>The bridge is gone: every open subscription ends, as a failure if it never got a screen.</summary>
    private void EndRun(Run run)
    {
        if (!ReferenceEquals(_run, run)) return;
        var failure = StartFailure(run);
        if (!run.Ready)
        {
            LastError = failure;
            Log.Error($"bridge exited: {failure}");
        }
        var open = _handlers.ToList();
        var streamed = _streaming.ToHashSet();
        Teardown();
        foreach (var (id, handler) in open) handler(streamed.Contains(id) ? new Event.Closed() : new Event.Failed(failure));
    }

    private void Receive(Run run, string line)
    {
        if (!ReferenceEquals(_run, run) || Json.ParseObject(line) is not { } o) return;
        switch (o.Str("type"))
        {
            case "ready":
                run.Ready = true;
                return;
            case "fatal":
                // The bridge couldn't load Orca's client code (Orca moved, updated or damaged).
                var message = Strings.Format("Bridge_StartFailedDetail", o.Str("message") ?? Strings.Get("Bridge_UnknownError"));
                LastError = message;
                Log.Error(message);
                var open = _handlers.Values.ToList();
                Teardown();
                foreach (var h in open) h(new Event.Failed(message));
                return;
        }
        if (o.Str("id") is not { } id || !_handlers.TryGetValue(id, out var handler)) return;
        switch (o.Str("type"))
        {
            case "frame":
                if (o.Obj("result") is not { } r) return;
                switch (r.Str("type"))
                {
                    case "scrollback":
                        _streaming.Add(id);
                        var text = r.Str("serialized") ?? string.Join("\r\n", r.Strings("lines"));
                        handler(new Event.Snapshot(text, r.Int("cols"), r.Int("rows")));
                        break;
                    case "data":
                        if (r.Str("chunk") is { } chunk) handler(new Event.Data(chunk));
                        break;
                    case "fit-override-changed":
                        if (r.Int("cols") is { } c && r.Int("rows") is { } rr) handler(new Event.Resized(c, rr));
                        break;
                    case "end":
                        Finish(id, new Event.Closed());
                        break;
                }
                break;
            case "error":
                LastError = o.Str("message");
                Finish(id, new Event.Failed(LastError ?? Strings.Get("Bridge_ConnectionError")));
                break;
            case "closed":
                Finish(id, _streaming.Contains(id) ? new Event.Closed() : new Event.Failed(StartFailure(run)));
                break;
        }
    }

    /// <summary>A subscription's last event: forget its handler and close it in the bridge too.</summary>
    private void Finish(string id, Event last)
    {
        if (!_handlers.TryGetValue(id, out var handler)) return;
        Remove(id);
        Write(new JsonObject { ["op"] = "unsubscribe", ["id"] = id });
        handler(last);
    }

    /// <summary>Drops a handler; once none are left the idle countdown starts.</summary>
    private void Remove(string id)
    {
        _handlers.Remove(id);
        _streaming.Remove(id);
        if (_handlers.Count > 0) return;
        _idle?.Cancel();
        var idle = _idle = new CancellationTokenSource();
        _ = Task.Delay(IdleShutdown, idle.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Post(() => { if (_handlers.Count == 0 && ReferenceEquals(_idle, idle)) Stop(); });
        }, TaskScheduler.Default);
    }

    /// <summary>Streams a terminal's screen; returns an id for Unsubscribe, or null when not paired.</summary>
    public string? Subscribe(string terminal, Action<Event> onEvent)
    {
        _idle?.Cancel();
        if (!EnsureRunning()) return null;
        var id = Guid.NewGuid().ToString("D");
        _handlers[id] = onEvent;
        Write(new JsonObject { ["op"] = "subscribe", ["id"] = id, ["terminal"] = terminal });
        return id;
    }

    public void Unsubscribe(string id)
    {
        if (!_handlers.ContainsKey(id)) return;
        Remove(id);
        Write(new JsonObject { ["op"] = "unsubscribe", ["id"] = id });
    }

    public void Stop()
    {
        _idle?.Cancel();
        _idle = null;
        if (_run is { } run)
        {
            try { if (!run.Process.HasExited) run.Process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        Teardown();
    }

    /// <summary>Forgets the current run without notifying anyone; late events from it are ignored.</summary>
    private void Teardown()
    {
        _handlers.Clear();
        _streaming.Clear();
        if (_run is not { } run) return;
        _run = null;
        try { run.Input.Dispose(); } catch (IOException) { }   // the bridge exits when its stdin closes
        run.Process.Dispose();
    }
}
