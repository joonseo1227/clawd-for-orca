using Clawd.Core;
using Clawd.Core.Chat;
using Clawd.Core.Orca;
using Clawd.Core.Pet;
using Clawd.Native;
using Clawd.Pet;
using Clawd.Services;
using Clawd.UI;
using Microsoft.UI.Dispatching;

namespace Clawd;

/// <summary>
/// The app: pet window, notification-area icon, alerts, chat wiring. The Mac app's AppDelegate;
/// split across AppController.*.cs by topic, all on the UI thread.
/// </summary>
internal sealed partial class AppController : IPetInput
{
    public DispatcherQueue Dispatcher { get; } = DispatcherQueue.GetForCurrentThread();
    public Settings Settings { get; } = Settings.Load();
    public OrcaWatcher Orca { get; } = new();
    public Attention Attention { get; } = new();
    public ChatModel Model { get; } = new();
    public Notifier Notifier { get; }
    public CredentialPairingStore Credentials { get; } = new();

    private readonly PetBrain _pet = new(5);
    private readonly PetWindow _petWindow;
    private readonly List<(Snack Snack, SnackWindow Window)> _snacks = [];
    private readonly HostWindow _host = new();
    private readonly TrayIcon _tray;
    private readonly GlobalHotKey _hotKey;
    private readonly SignWindow _sign;
    private readonly ChatWindow _chat;
    private SettingsWindow? _settingsWindow;
    private WelcomeWindow? _welcome;
    private DispatcherQueueTimer? _timer;
    private double _rate;
    private double _lastFrame = Clock.Uptime;
    private Vec _lastMouse;
    private double _pollIn, _timelinePollIn, _screenPollIn, _rowsIn, _hoverTime, _lastScreenChange;
    private bool _hoverDismissed;
    private bool _paused;              // a menu is open
    private ScreenSpace _space;
    private Rasterizer? _frameImage;
    private Dictionary<string, string> _titles = [];
    private (Sign Sign, double Until, Action? Action)? _flash;   // short-lived cards
    private double _frontCheckedAt = -1;
    private bool _orcaFront;

    public AppController()
    {
        _petWindow = new PetWindow(this);
        _tray = new TrayIcon(_host);
        _hotKey = new GlobalHotKey(_host, HotKeyPressed);
        _sign = new SignWindow();
        _chat = new ChatWindow(this);
        Notifier = new Notifier(Dispatcher);
    }

    public bool Hidden => Settings.Hidden;

    public bool ShouldNotify => Settings.Notifications switch
    {
        NotificationPolicy.Always => true,
        NotificationPolicy.WhenHidden => Hidden,
        _ => false,
    };

    public void Start()
    {
        DarkMenus.FollowSystem();
        Attention.Looking = Looking;
        Orca.Launcher = OrcaApp.Launch;
        Orca.Enabled = Settings.Orca;
        Orca.Changed += OrcaChanged;
        Orca.RunningChanged += _ => { UpdateStatus(); RefreshChat(); };

        _host.TrayEvent += TrayEvent;
        _host.DisplayChanged += () => KeepOnScreen(PetSpace());

        // Drop in from the top of the screen.
        var start = TestHooks.StartAt is var (sx, sy) ? new Win32.Point { X = sx, Y = sy } : Monitors.Cursor;
        _space = new ScreenSpace(Monitors.At(start));
        var b = _space.Bounds;
        _pet.Pos = new Vec(b.MidX, b.MaxY - Sprite.CanvasH * 5);
        _pet.Thrown(Vec.Zero);
        UpdateStatus();

        if (!_hotKey.Register(Settings.Shortcut))
            Say(L.Format("Say_ShortcutTaken", Settings.Shortcut.Display), SignSymbol.Keyboard, 6);

        Notifier.Responded += NotificationResponse;
        Notifier.Start();
        StartUpdates();

        StartLive();
        Model.Live = _bridge!.IsPaired;
        Model.SelectionChanged += key => Dispatcher.TryEnqueue(() => SelectionChanged(key));
        // Re-fit when the view switches mode or the terminal area changes size; a short delay lets
        // a window resize settle before the PTY is resized, and a newer change replaces a pending one
        // so the agent's terminal isn't resized through every size in between.
        var layout = Dispatcher.CreateTimer();
        layout.Interval = TimeSpan.FromSeconds(0.15);
        layout.IsRepeating = false;
        layout.Tick += (_, _) =>
        {
            try { UpdateFit(); UpdateLive(); }
            catch (Exception e) { Log.Error($"re-fit: {e}"); }
        };
        Model.LayoutChanged += () => { layout.Stop(); layout.Start(); };
        _chat.Closed += () => { Model.Notice = null; UpdateFit(); UpdateLive(); };

        if (TestHooks.TerminalView) Model.Mode = ChatMode.Terminal;
        if (TestHooks.OpenChat) After(3, () => OpenChat(fromTray: TestHooks.StartAt is null));
        if (TestHooks.ShowPairing) After(1, () => OpenSettings(pairing: true));
        if (TestHooks.OpenSettings) After(1, () => OpenSettings());
        if (TestHooks.Welcome || !Settings.Welcomed)
        {
            Settings.Welcomed = true;
            Settings.Save();
            After(1, ShowWelcome);
        }
        ScheduleFrames();
    }

    public void After(double seconds, Action action)
    {
        var t = Dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(seconds);
        t.IsRepeating = false;
        // An exception in a timer callback bypasses Application.UnhandledException and ends the
        // process (a stowed exception); log it instead, as Tick does for frames.
        t.Tick += (_, _) =>
        {
            try { action(); }
            catch (Exception e) { Log.Error($"deferred action: {e}"); }
        };
        t.Start();
    }

    // MARK: Frame clock

    /// <summary>Frames per second: smooth while Clawd moves, a trickle while it sleeps, and just enough
    /// to keep polling Orca while it is hidden, so an idle Clawd costs next to nothing.</summary>
    private double FrameRate
    {
        get
        {
            if (_chat.IsOpen) return 30;   // the chat follows the agent live
            if (Hidden) return _snacks.All(s => s.Snack.Grounded) ? 4 : 30;
            if (_pet.Current == PetBrain.State.Sleep && _pet.Effects.Count == 0) return 8;
            // Standing about only breathes and blinks; pixel art moves in whole pixels anyway.
            if (_pet.Current is PetBrain.State.Idle or PetBrain.State.Hold && _pet.Effects.Count == 0 && _snacks.Count == 0) return 15;
            return 30;
        }
    }

    private void ScheduleFrames()
    {
        _timer?.Stop();
        _rate = FrameRate;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1 / _rate);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        var now = Clock.Uptime;
        // Real elapsed time, capped so a stall (sleep, a busy UI thread) doesn't teleport Clawd.
        var dt = Math.Min(now - _lastFrame, 0.3);
        _lastFrame = now;
        try { Frame(dt); }
        catch (Exception e) { Log.Error($"frame: {e}"); }
        if (FrameRate != _rate) ScheduleFrames();
    }

    /// <summary>The monitor Clawd is on. Crossing to a display with another scale keeps it at the same
    /// physical position: the world is measured in that display's DIPs.</summary>
    private ScreenSpace PetSpace()
    {
        var unit = _space.UnitPixels;
        var (x, y) = _space.TopLeft(_pet.Pos, 22 * unit);
        var space = new ScreenSpace(Monitors.At(new Win32.Point { X = x + 10 * unit, Y = y + 16 * unit }));
        if (space.Scale != _space.Scale)
        {
            var k = _space.Scale / space.Scale;
            _pet.Pos = new Vec(_pet.Pos.X * k, _pet.Pos.Y * k);
            _pet.Vel = new Vec(_pet.Vel.X * k, _pet.Vel.Y * k);
        }
        _space = space;
        return space;
    }

    private void Frame(double dt)
    {
        _pollIn -= dt;
        if (_pollIn <= 0) { _pollIn = 2; Orca.Poll(); }
        // "1 min ago" in the open chat moves on even while no agent changes; rows that read the same
        // as before change nothing.
        _rowsIn -= dt;
        if (_chat.IsOpen && _rowsIn <= 0) { _rowsIn = 15; RefreshChat(); }
        if (_chat.IsOpen && !Model.ShowsTerminal)
        {
            _timelinePollIn -= dt;
            if (_timelinePollIn <= 0) { _timelinePollIn = 0.5; PollTimeline(); }
        }
        if (_chat.IsOpen && Model.ShowsTerminal && !Model.Live)
        {
            _screenPollIn -= dt;
            // Reads go straight to Orca's pipe (about 1 ms), so the view can follow the agent live.
            var quiet = Clock.Uptime - _lastScreenChange > 1;
            if (_screenPollIn <= 0) { _screenPollIn = quiet ? 0.5 : 0.2; PollScreen(); }
        }
        if (_chat.IsOpen) _sign.Set(null);   // the chat already shows everything the card would

        // Snacks have windows of their own: one in the air keeps falling while Clawd is hidden.
        var space = PetSpace();
        var bounds = space.Bounds;
        StepSnacks(dt, space, bounds);
        if (Hidden)
        {
            // Followed while hidden too, so the first frame back doesn't read the whole trip as speed.
            _lastMouse = _space.ToWorld(Monitors.Cursor);
            return;
        }

        var unit = space.UnitPixels;
        var widthPx = 20 * unit;
        var heightPx = 22 * unit;
        _pet.Scale = unit / space.Scale;
        _petWindow.Scale = space.Scale;
        var cursor = Monitors.Cursor;
        var mouse = space.ToWorld(cursor);
        var speed = new Vec(mouse.X - _lastMouse.X, mouse.Y - _lastMouse.Y).Length / Math.Max(dt, 1e-3);
        _lastMouse = mouse;

        // While a menu or the chat is open, or the pointer rests on Clawd, it keeps animating but
        // stays where it is, so nothing slides away from under the pointer. Being dragged or
        // thrown is the user moving it, so that still goes.
        var rect = _petWindow.Bounds;
        var hovering = rect.Contains(cursor) && _pet.Current != PetBrain.State.Drag;
        var hold = (_paused || _chat.IsOpen || hovering) && _pet.Current is not (PetBrain.State.Drag or PetBrain.State.Fly);
        _pet.Frozen = hold;
        var anchor = _pet.Pos;

        _pet.WorkActivities = Orca.Working.Select(a => a.Activity).ToList();
        _pet.Holding = Pending.Count > 0;
        // The chat opens over the top of Clawd's head, where the "!" is drawn, and Clawd's window is
        // above it; the Mac's popover covers the mark instead. The chat lists who is waiting anyway.
        _pet.Attention = _pet.Holding && !_chat.IsOpen;
        var size = new Vec(widthPx / space.Scale, heightPx / space.Scale);
        _pet.Step(dt, new PetEnv(bounds, size, mouse, speed, _snacks.Select(s => s.Snack).ToList()));
        if (hold)
        {
            _pet.Pos = anchor;
            _pet.Vel = Vec.Zero;
            // Held still while its display went away: bring it back rather than keep it out of sight.
            if (!bounds.Inset(-size.X, -size.Y).Contains(anchor)) KeepOnScreen(space);
        }

        if (_frameImage is null || _frameImage.Width != widthPx || _frameImage.Height != heightPx) _frameImage = new Rasterizer(widthPx, heightPx);
        _frameImage.Clear();
        _frameImage.Scale(unit, unit);
        Sprite.Render(_frameImage, _pet.Pose, _pet.Effects);
        var (x, y) = space.TopLeft(_pet.Pos, heightPx);
        // Likewise for what rises from Clawd (confetti, hearts, Zs): what would land on the chat is left out.
        if (_chat.Bounds is { } chat) _frameImage.ClearRect(chat.Left - x, chat.Top - y, chat.Right - chat.Left, chat.Bottom - chat.Top);
        // The card above Clawd's head covers the "!" as the Mac's card does, rather than the mark
        // being drawn over the card's last line.
        if (_sign.Bounds is { } card) _frameImage.ClearRect(card.Left - x, card.Top - y, card.Right - card.Left, card.Bottom - card.Top);
        _petWindow.Present(_frameImage, x, y);
        if ((int)(Clock.Uptime * 2) % 10 == 0) _petWindow.KeepOnTop();

        // Hovering over Clawd shows what the Orca agents are up to.
        _hoverTime = hovering ? _hoverTime + dt : 0;
        if (!hovering) _hoverDismissed = false;
        // Not under an open menu: the menu's frame loop keeps this running, and the summary card
        // would cover the menu Clawd was right-clicked for.
        if (!_chat.IsOpen) RefreshSign(!_paused && _hoverTime > 0.6 && !_hoverDismissed);
        // The card floats just above Clawd's head: 8 DIPs, where the Mac card's visible edge sits.
        _sign.Follow(x + widthPx / 2, y + (int)(Sprite.SpriteY * unit) - (int)(8 * space.Scale), space.Monitor);
    }

    private void StepSnacks(double dt, ScreenSpace space, Box bounds)
    {
        foreach (var gone in _snacks.Where(s => s.Snack.Gone).ToList())
        {
            gone.Window.Dispose();
            _snacks.Remove(gone);
        }
        var unit = space.UnitPixels;
        foreach (var (snack, window) in _snacks)
        {
            snack.Step(dt, bounds);
            var image = new Rasterizer(8 * unit, 7 * unit);
            image.Scale(unit, unit);
            snack.Render(image);
            window.Space = space;
            window.HeightPx = image.Height;
            var (x, y) = space.TopLeft(snack.Pos, image.Height);
            window.Present(image, x, y);
        }
    }

    public void DropSnack()
    {
        // On Clawd's display, where snacks are stepped, wherever the menu was opened from.
        var b = _space.Bounds;
        var x = b.MinX + 40 + Random.Shared.NextDouble() * Math.Max(1, b.Width - 120);
        var snack = new Snack(new Vec(x, b.MaxY - Snack.Height));
        _snacks.Add((snack, new SnackWindow(snack)));
    }

    /// <summary>Displays changed (one unplugged, resolution switched): bring Clawd back into view on
    /// <paramref name="space"/>, its own display or the nearest one. Whatever it was doing goes on,
    /// as on the Mac; if the ground moved away, it falls.</summary>
    private void KeepOnScreen(ScreenSpace space)
    {
        _space = space;
        var b = _space.Bounds;
        var w = 20 * _space.UnitPixels / _space.Scale;
        var h = 22 * _space.UnitPixels / _space.Scale;
        var ground = _pet.Ground(new PetEnv(b, default, default, 0, []));
        // Max before Min, as on the Mac: on a display too short for Clawd the top edge wins.
        _pet.Pos = new Vec(Math.Clamp(_pet.Pos.X, b.MinX, b.MaxX - w), Math.Min(Math.Max(_pet.Pos.Y, ground), b.MaxY - h));
    }

    // MARK: Pet input

    public void Clicked()
    {
        // While an agent is waiting, a click takes you straight to it.
        ToggleChat();
        _pet.Poked();
    }

    public void DoubleClicked() => OpenOrca();

    public void ContextMenu(Win32.Point at)
    {
        _paused = true;
        try { PetMenu().Show(_host.Handle, at); }
        finally { _paused = false; }
    }

    private readonly DragTracker _drag = new();

    public void DragStarted() => _pet.Grabbed();

    public void Dragged(int x, int y)
    {
        _pet.Pos = _space.Origin(x, y, 22 * _space.UnitPixels);
        _drag.Add(_pet.Pos);
    }

    public void DragEnded() => _pet.Thrown(_drag.Velocity());

    public void FilesDropped(IReadOnlyList<string> paths)
    {
        _pet.Poked();   // a little hop: Clawd noticed
        DropFiles(paths);
    }

    // MARK: Preferences

    public void ToggleHidden()
    {
        Settings.Hidden = !Settings.Hidden;
        Settings.Save();
        if (Hidden) { _petWindow.Hide(); _sign.Set(null); } else _petWindow.Show();
    }

    public void SetSound(bool on) { Settings.Sound = on; Settings.Save(); }

    public void SetNotificationPolicy(NotificationPolicy p) { Settings.Notifications = p; Settings.Save(); }

    public void ToggleOrca()
    {
        if (Orca.Enabled)
        {
            Orca.Stop();
            Attention.Reset();
            _pet.WorkActivities = [];
            Say(L.Get("Say_OrcaOff"), SignSymbol.Pause);
        }
        else
        {
            Orca.Enabled = true;
            _pollIn = 0;
            Say(L.Get("Say_OrcaOn"), SignSymbol.Play);
        }
        Settings.Orca = Orca.Enabled;
        Settings.Save();
        UpdateStatus();
        RefreshChat();
    }

    public void ToggleLogin()
    {
        var on = !LoginItem.IsEnabled;
        if (LoginItem.Set(on) is { } error) Say(L.Format("Say_LoginFailed", error), SignSymbol.Warning, 4);
        else Say(L.Get(on ? "Say_LoginOn" : "Say_LoginOff"), on ? SignSymbol.CheckCircle : SignSymbol.Unlink);
    }

    /// <summary>Saves and registers the global shortcut; false when the combination is taken.</summary>
    public bool SetShortcut(Shortcut shortcut)
    {
        Settings.Shortcut = shortcut;
        Settings.Save();
        return _hotKey.Register(shortcut);
    }

    public void PauseHotKey(bool paused)
    {
        _hotKey.Paused = paused;
        if (!paused) _hotKey.Register(Settings.Shortcut);
    }

    public void CompletePairing(OrcaPairing pairing)
    {
        if (_bridge?.Pair(pairing) != true)
        {
            Say(_bridge?.LastError ?? L.Get("Say_PairFailed"), SignSymbol.Warning, 5);
            return;
        }
        Model.Live = true;
        Say(L.Get("Say_Paired"), SignSymbol.Link);
    }

    public void Unpair()
    {
        if (_liveSub is { } sub) _bridge?.Unsubscribe(sub.Id);
        _liveSub = null;
        _bridge?.Unpair();
        Model.Live = false;
        Say(L.Get("Say_Unpaired"), SignSymbol.Unlink);
    }

    public void OpenSettings(bool pairing = false)
    {
        _chat.Close();
        _settingsWindow ??= new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Present(pairing);
    }

    private void ShowWelcome()
    {
        _welcome ??= new WelcomeWindow(this);
        _welcome.Closed += (_, _) => _welcome = null;
        _welcome.Activate();
    }

    public void Quit()
    {
        // Give a resized terminal back to Orca, bounded so a stuck Orca can't hold up the exit.
        if (_fitted is { } f) Orca.RestoreSize(f.Agent).Wait(TimeSpan.FromSeconds(2));
        _timer?.Stop();
        StopLive();
        Notifier.Dispose();
        _hotKey.Dispose();
        _tray.Dispose();
        foreach (var (_, w) in _snacks) w.Dispose();
        _petWindow.Dispose();
        _host.Dispose();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    // MARK: Notification area

    private void TrayEvent(uint e, int x, int y)
    {
        switch (e)
        {
            case Win32.WM_LBUTTONUP or Win32.NIN_SELECT or Win32.NIN_KEYSELECT:
                if (_chat.IsOpen) _chat.Close();
                else if (!_chat.JustClosed) OpenChat(fromTray: true);
                break;
            case Win32.WM_CONTEXTMENU:
                _paused = true;
                try { TrayMenu().Show(_host.Handle, new Win32.Point { X = x, Y = y }); }
                finally { _paused = false; }
                break;
        }
    }

    private void HotKeyPressed()
    {
        if (_chat.IsOpen) _chat.Close(); else OpenChat();
    }

    /// <summary>The notification area shows only the most pressing state.</summary>
    public void UpdateStatus()
    {
        var waiting = Orca.Enabled ? Orca.Waiting.Count() : 0;
        _tray.Update(Sign.StatusTitle(Orca.Enabled, waiting, Orca.Working.Count(), Attention.Finished.Count), badge: waiting > 0);
    }

    /// <summary>The user is already looking at this agent in Orca, so don't nag. Checked at most twice
    /// a second: the card logic asks every frame.</summary>
    private bool Looking(OrcaAgent a)
    {
        var now = Clock.Uptime;
        if (now - _frontCheckedAt > 0.5)
        {
            _frontCheckedAt = now;
            _orcaFront = OrcaApp.IsFrontmost();
        }
        return _orcaFront && a.WorktreeActive;
    }
}
