using System.Reflection;
using Clawd.Core;
using Clawd.Core.Orca;
using Clawd.Core.Pet;
using Clawd.Core.Updates;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Clawd.UI;

/// <summary>Settings-style rows: a card with a title, an optional description and a control, the
/// layout of Windows Settings.</summary>
internal static class SettingsCards
{
    public static Border Card(string title, string? description, UIElement control, string? glyph = null)
    {
        var grid = new Grid { ColumnSpacing = 16, MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph is not null)
        {
            var icon = Ui.Icon(glyph, null, 20);
            icon.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(icon);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(title));
        if (description is not null) text.Children.Add(Ui.Caption(description, maxLines: 0));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control is FrameworkElement f) f.VerticalAlignment = VerticalAlignment.Center;
        // Switches and lists have no text of their own; Narrator reads the card's title for them.
        if (control is ToggleSwitch or ComboBox) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, title);
        Grid.SetColumn((FrameworkElement)control, 2);
        grid.Children.Add(control);
        var card = Ui.Card(grid);
        card.Padding = new Thickness(16, 12, 16, 12);
        return card;
    }

    public static TextBlock Section(string title)
    {
        var t = Ui.Text(title, "BodyStrongTextBlockStyle");
        t.Margin = new Thickness(4, 16, 0, 4);
        return t;
    }

    public static TextBlock Footer(string text)
    {
        var t = Ui.Caption(text, maxLines: 0);
        t.Margin = new Thickness(4, 2, 0, 0);
        return t;
    }

    /// <summary>A switch for app state: <paramref name="toggle"/> flips it, and a flip that didn't take
    /// (turning on start at sign-in can fail) springs back to what <paramref name="state"/> says.</summary>
    public static ToggleSwitch Toggle(Func<bool> state, Action toggle)
    {
        var t = new ToggleSwitch { IsOn = state(), OnContent = "", OffContent = "", MinWidth = 0 };
        t.Toggled += (_, _) =>
        {
            if (t.IsOn == state()) return;   // set back below, or already in step
            toggle();
            if (t.IsOn != state()) t.IsOn = state();
        };
        return t;
    }

    /// <summary>Whether Clawd can see Orca, and the one action that helps when it can't.</summary>
    public static UIElement OrcaStatus(AppController app)
    {
        if (!app.Orca.Available)
            return Ui.Row(8, Ui.Icon(Ui.Glyph.Cancel, "ClawdSecondaryIcon", 12), Ui.Secondary(L.Get("Settings_NotInstalled"), wrap: false),
                new HyperlinkButton { Content = L.Get("Settings_Download"), NavigateUri = OrcaInstallation.Homepage });
        if (!app.Orca.Running)
            return Ui.Row(8, Ui.Icon(Ui.Glyph.Moon, "ClawdSecondaryIcon", 12), Ui.Secondary(L.Get("Settings_NotRunning"), wrap: false), Ui.Button(L.Get("Settings_Open"), app.Orca.Launch));
        return Ui.Row(8, Ui.Icon(Ui.Glyph.CheckCircle, "ClawdSuccessIcon", 14), Ui.Secondary(L.Plural("Settings_Running", app.Orca.Agents.Count), wrap: false));
    }

    /// <summary>Paired, or the button that pairs; Settings also offers to unpair, the welcome window doesn't.</summary>
    public static UIElement LivePairing(AppController app, Action refresh, bool canUnpair)
    {
        if (app.Model.Live)
        {
            var paired = Ui.Row(8, Ui.Icon(Ui.Glyph.CheckCircle, "ClawdSuccessIcon", 14), Ui.Secondary(L.Get("Settings_Connected"), wrap: false));
            if (canUnpair) paired.Children.Add(Ui.Button(L.Get("Settings_Disconnect"), () => { app.Unpair(); refresh(); }));
            return paired;
        }
        Button pair = null!;
        // The XamlRoot is read when clicked: a page built before its window has loaded has none yet.
        pair = Ui.Button(L.Get("Settings_Connect"), async () => { await PairingDialog.Show(app, pair.XamlRoot); refresh(); });
        return pair;
    }
}

/// <summary>Takes the pairing code from Orca Mobile's "Copy pairing code".</summary>
internal static class PairingDialog
{
    public static async Task Show(AppController app, XamlRoot? root)
    {
        if (root is null) return;
        // A password box: the code carries a token that can drive your terminals.
        var code = new PasswordBox { PlaceholderText = "orca://pair?code=…", MinWidth = 300 };
        var error = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            IsClosable = false,
            IsOpen = false,
            Message = L.Get("Pairing_Invalid"),
        };
        var paste = Ui.Button(L.Get("Pairing_Paste"), async () =>
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text)) code.Password = await content.GetTextAsync();
        });
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(code);
        Grid.SetColumn(paste, 1);
        row.Children.Add(paste);
        var body = Ui.Stack(12,
            Ui.Secondary(L.Get("Pairing_Why")),
            Ui.Stack(6, Step(L.Get("Pairing_Step1")), Step(L.Get("Pairing_Step2")), Step(L.Get("Pairing_Step3"))),
            row, error,
            Ui.Caption(L.Get("Pairing_Storage"), maxLines: 0));
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = L.Get("Pairing_Title"),
            Content = body,
            PrimaryButtonText = L.Get("Pairing_Connect"),
            CloseButtonText = L.Get("Pairing_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        code.PasswordChanged += (_, _) =>
        {
            error.IsOpen = false;
            dialog.IsPrimaryButtonEnabled = code.Password.Trim().Length > 0;
        };
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (OrcaPairing.FromCode(code.Password) is { } pairing) app.CompletePairing(pairing);
            else { error.IsOpen = true; e.Cancel = true; }
        };
        await dialog.ShowAsync();
    }

    /// <summary>A step with its **bold** names of Orca's controls, as on the Mac.</summary>
    private static TextBlock Step(string text)
    {
        var t = Ui.Text("");
        TimelineView.AddInlines(t.Inlines, text);
        return t;
    }
}

/// <summary>Orca's status in a card, replaced only when it changes (Orca opened or closed, agents
/// come and go), so the rest of the page isn't rebuilt for it.</summary>
internal sealed class OrcaStatusCell
{
    private readonly AppController _app;
    private string? _shown;

    public OrcaStatusCell(AppController app)
    {
        _app = app;
        Refresh();
    }

    public Border Root { get; } = new();

    public void Refresh()
    {
        var key = $"{_app.Orca.Available}|{_app.Orca.Running}|{_app.Orca.Agents.Count}";
        if (key == _shown) return;
        _shown = key;
        Root.Child = SettingsCards.OrcaStatus(_app);
    }
}

/// <summary>A button that shows the shortcut and records a new one when clicked.</summary>
internal sealed class ShortcutRecorder
{
    private readonly AppController _app;
    private readonly Button _button = new() { MinWidth = 130 };
    private readonly Button _reset;
    private readonly InfoBar _taken;
    private bool _recording;

    public ShortcutRecorder(AppController app)
    {
        _app = app;
        _taken = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false, Message = L.Get("Shortcut_Taken") };
        _reset = Ui.IconButton(Ui.Glyph.Undo, L.Format("Shortcut_Reset", Shortcut.Standard.Display), () => Apply(Shortcut.Standard));
        _button.Click += (_, _) => { if (_recording) Stop(); else Start(); };
        _button.PreviewKeyDown += (_, e) =>
        {
            if (!_recording) return;
            e.Handled = true;
            bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);
            var mods = (Down(VirtualKey.Control) ? Shortcut.Mods.Control : 0) | (Down(VirtualKey.Menu) ? Shortcut.Mods.Alt : 0)
                | (Down(VirtualKey.Shift) ? Shortcut.Mods.Shift : 0) | (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? Shortcut.Mods.Windows : 0);
            if (e.Key == VirtualKey.Escape && mods == 0) { Stop(); return; }
            if (Shortcut.FromKeyPress((int)e.Key, mods) is { } s) Apply(s);
            // A key without Ctrl, Alt or Win can't be a shortcut: say so, as the Mac does.
            else if (!ChatWindow.IsModifierOrImeKey(e.Key)) Native.Win32.MessageBeep(Native.Win32.MB_OK);
        };
        Refresh();
    }

    // Built once: an element has one parent, and the settings page is rebuilt every few seconds,
    // moving this row (not new copies of its buttons) to the new page.
    private StackPanel? _control;
    public UIElement Control => _control ??= Ui.Row(6, _button, _reset);
    public UIElement Warning => _taken;

    private void Start()
    {
        _recording = true;
        _app.PauseHotKey(true);   // the current shortcut must not fire while keys are pressed
        Refresh();
    }

    /// <summary>Ends a recording left running, so the shortcut works again (the window is closing).</summary>
    public void Cancel()
    {
        if (_recording) Stop();
    }

    private void Stop()
    {
        _recording = false;
        _app.PauseHotKey(false);
        Refresh();
    }

    private void Apply(Shortcut s)
    {
        _recording = false;
        _taken.IsOpen = !_app.SetShortcut(s);
        Refresh();
    }

    private void Refresh()
    {
        _button.Content = _recording ? L.Get("Shortcut_Recording") : _app.Settings.Shortcut.Display;
        ToolTipService.SetToolTip(_button, L.Get(_recording ? "Shortcut_RecordingTip" : "Shortcut_ChangeTip"));
        _reset.Visibility = _app.Settings.Shortcut == Shortcut.Standard || _recording ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>The settings window: a grouped form like Windows Settings, on Mica.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppController _app;
    private readonly ScrollViewer _scroll = new();
    private readonly ShortcutRecorder _shortcut;
    // Where the shortcut recorder's elements were put last: they are moved to each rebuilt page,
    // and an element's Parent isn't reliable before the page it is on has loaded.
    private Grid? _shortcutHost;
    private Panel? _page;
    private OrcaStatusCell? _status;
    private string? _shownState;

    public SettingsWindow(AppController app)
    {
        _app = app;
        _shortcut = new ShortcutRecorder(app);
        Title = L.Get("Settings_Title");
        SystemBackdrop = new MicaBackdrop();
        // The caption follows Windows' app mode; by default it stays light even in dark mode.
        AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Clawd.ico"));
        Content = _scroll;
        Build();
        // State lives in the app, outside this window; follow it while the window is open. Only a
        // change rebuilds the page: a rebuild closes an open list and moves keyboard focus.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (_, _) =>
        {
            if (State() != _shownState) Build();
            else _status?.Refresh();
        };
        timer.Start();
        Closed += (_, _) =>
        {
            timer.Stop();
            _shortcut.Cancel();
        };
    }

    public void Present(bool pairing)
    {
        if (!AppWindow.IsVisible) WindowChrome.Center(AppWindow, 560, 720);
        Activate();
        if (pairing) WhenLoaded(async () => { await PairingDialog.Show(_app, Content.XamlRoot); Build(); });
    }

    /// <summary>A dialog needs the page's XamlRoot, which a just-created window doesn't have yet.</summary>
    private void WhenLoaded(Action action)
    {
        if (_scroll.XamlRoot is not null) { DispatcherQueue.TryEnqueue(() => action()); return; }
        void Once(object sender, RoutedEventArgs e) { _scroll.Loaded -= Once; action(); }
        _scroll.Loaded += Once;
    }

    /// <summary>What the page shows, apart from Orca's status (updated in place) and the shortcut
    /// (the recorder keeps itself current).</summary>
    private string State() => string.Join("|", Services.LoginItem.IsEnabled, _app.Hidden, _app.Settings.Notifications,
        _app.Settings.Sound, _app.Orca.Available, _app.Orca.Enabled, _app.Model.Live, _app.Settings.CheckForUpdates, _app.Updates?.Status);

    /// <summary>A change made on this page: the controls already show it, so it isn't a reason to rebuild.</summary>
    private Action Changed(Action action) => () => { action(); _shownState = State(); };

    private void Build()
    {
        var app = _app;
        _shownState = State();
        var panel = new StackPanel { Spacing = 4, Padding = new Thickness(24, 12, 24, 24), MaxWidth = 720 };
        panel.Children.Add(SettingsCards.Section(L.Get("Settings_General")));
        panel.Children.Add(SettingsCards.Card(L.Get("Settings_StartAtLogin"), null, SettingsCards.Toggle(() => Services.LoginItem.IsEnabled, Changed(app.ToggleLogin)), Ui.Glyph.Power));
        panel.Children.Add(SettingsCards.Card(L.Get("Settings_ShowClawd"), null, SettingsCards.Toggle(() => !app.Hidden, Changed(app.ToggleHidden))));
        var policy = new ComboBox { MinWidth = 180 };
        foreach (var p in Enum.GetValues<NotificationPolicy>()) policy.Items.Add(new ComboBoxItem { Content = p.Title(), Tag = p });
        policy.SelectedIndex = (int)app.Settings.Notifications;
        policy.SelectionChanged += (_, _) => { if (policy.SelectedItem is ComboBoxItem { Tag: NotificationPolicy p }) Changed(() => app.SetNotificationPolicy(p))(); };
        panel.Children.Add(SettingsCards.Card(L.Get("Settings_Notifications"), null, policy));
        panel.Children.Add(SettingsCards.Card(L.Get("Settings_Sound"), null, SettingsCards.Toggle(() => app.Settings.Sound, Changed(() => app.SetSound(!app.Settings.Sound)))));

        panel.Children.Add(SettingsCards.Section("Orca"));
        _status = new OrcaStatusCell(app);
        panel.Children.Add(SettingsCards.Card(L.Get("Settings_Status"), null, _status.Root));
        var orca = SettingsCards.Toggle(() => app.Orca.Enabled, Changed(app.ToggleOrca));
        orca.IsEnabled = app.Orca.Available;
        panel.Children.Add(SettingsCards.Card(L.Get("Settings_OrcaIntegration"), null, orca));
        panel.Children.Add(SettingsCards.Card(L.Get("Settings_LiveTerminal"), L.Get("Settings_LiveSubtitle"), SettingsCards.LivePairing(app, Build, canUnpair: true)));

        panel.Children.Add(SettingsCards.Section(L.Get("Settings_Shortcuts")));
        _shortcutHost?.Children.Remove(_shortcut.Control);
        _page?.Children.Remove(_shortcut.Warning);
        var shortcutCard = SettingsCards.Card(L.Get("Settings_TalkToClawd"), null, _shortcut.Control, Ui.Glyph.Keyboard);
        _shortcutHost = (Grid)shortcutCard.Child;
        _page = panel;
        panel.Children.Add(shortcutCard);
        panel.Children.Add(_shortcut.Warning);
        panel.Children.Add(SettingsCards.Card(L.Get("Settings_SwitchView"), null, Ui.Secondary("Ctrl+T", wrap: false)));
        panel.Children.Add(SettingsCards.Card(L.Get("Settings_CloseChat"), null, Ui.Secondary("Ctrl+W", wrap: false)));

        panel.Children.Add(SettingsCards.Section(L.Get("Settings_Updates")));
        if (app.Updates is { } updates)
        {
            panel.Children.Add(SettingsCards.Card(L.Get("Settings_AutoUpdate"), L.Get("Settings_AutoUpdateSubtitle"),
                SettingsCards.Toggle(() => app.Settings.CheckForUpdates, Changed(app.ToggleAutoUpdate))));
            panel.Children.Add(UpdateCard(updates.Status));
        }
        else
        {
            panel.Children.Add(SettingsCards.Card(L.Get("Update_InstalledOnly"), null,
                new HyperlinkButton { Content = L.Get("Settings_Download"), NavigateUri = UpdateFeed.ReleasesPage }));
        }

        panel.Children.Add(SettingsCards.Section(L.Get("Settings_About")));
        // The informational version keeps a pre-release suffix such as "-beta.1"; drop the "+commit" part.
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? L.Get("Settings_DevBuild");
        panel.Children.Add(SettingsCards.Card(L.Get("Settings_Version"), null, Ui.Secondary(version, wrap: false)));
        panel.Children.Add(SettingsCards.Card(L.Get("Settings_Author"), null, new HyperlinkButton { Content = "joonseo1227", NavigateUri = new Uri("https://joonseo1227.com") }));
        panel.Children.Add(SettingsCards.Footer(L.Get("Settings_Disclaimer")));
        var offset = _scroll.VerticalOffset;
        _scroll.Content = panel;
        _scroll.DispatcherQueue.TryEnqueue(() => _scroll.ChangeView(null, offset, null, true));
    }

    /// <summary>Where the updater is, and the one action that moves it on.</summary>
    private Border UpdateCard(UpdateStatus status)
    {
        var app = _app;
        var version = status.Release?.Version.ToString();
        var checkedAt = app.Settings.LastUpdateCheck is { } at ? L.Format("Update_LastChecked", Text.Ago(at, DateTimeOffset.Now)) : null;
        // Rebuilt at once to show the check under way, and again with what it found.
        Button Check(string label) => Ui.Button(label, async () =>
        {
            var check = app.CheckForUpdates(manual: true);
            Build();
            await check;
            Build();
        });
        return status.Stage switch
        {
            UpdateStage.Checking => SettingsCards.Card(L.Get("Update_Checking"), null, Ui.Spinner()),
            UpdateStage.Downloading => SettingsCards.Card(L.Format("Update_Downloading", version), null, Ui.Spinner()),
            UpdateStage.Installing => SettingsCards.Card(L.Get("Update_Installing"), null, Ui.Spinner()),
            UpdateStage.Ready => SettingsCards.Card(L.Format("Update_Ready", version),
                status.Failure == UpdateStage.Installing ? status.Error : L.Get("Update_ReadyBody"),
                Ui.Row(8, new HyperlinkButton { Content = L.Get("Update_WhatsNew"), NavigateUri = status.Release!.Page },
                    Ui.Button(L.Get("Update_Restart"), app.InstallUpdate, accent: true))),
            UpdateStage.UpToDate => SettingsCards.Card(L.Get("Update_UpToDate"), checkedAt, Check(L.Get("Update_CheckNow"))),
            UpdateStage.Failed => SettingsCards.Card(L.Get(status.Failure == UpdateStage.Downloading ? "Update_DownloadFailed" : "Update_CheckFailed"),
                status.Error, Check(L.Get("Update_TryAgain"))),
            _ => SettingsCards.Card(L.Get("Update_Idle"), checkedAt, Check(L.Get("Update_CheckNow"))),
        };
    }
}

/// <summary>
/// Shown on first launch: what Clawd does, and the few things to set up, each with its state and
/// the one action that completes it. Nothing here is required to start.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    private readonly AppController _app;
    // Drawn at device pixels (DrawPortrait); Fill maps them back onto 160 × 120 DIPs one to one.
    // With Stretch.None the bitmap's pixels would count as DIPs, enlarged and cut off above 100%.
    private readonly Image _portrait = new() { Width = 160, Height = 120, Stretch = Stretch.Fill };
    private readonly StackPanel _cards = new() { Spacing = 4 };
    private OrcaStatusCell? _status;
    private string? _shownState;
    private WriteableBitmap? _bitmap;
    private readonly Rasterizer _frame = new(160, 120);

    public WelcomeWindow(AppController app)
    {
        _app = app;
        Title = L.Get("Welcome_WindowTitle");
        SystemBackdrop = new MicaBackdrop();
        // The caption follows Windows' app mode; by default it stays light even in dark mode.
        AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode;
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Clawd.ico"));
        WindowChrome.Center(AppWindow, 560, 700);

        var title = Ui.Text(L.Get("Welcome_Title"), "TitleLargeTextBlockStyle");
        var body = Ui.Secondary(L.Get("Welcome_Body"));
        body.TextAlignment = TextAlignment.Center;
        var start = Ui.Button(L.Get("Welcome_Start"), Close, accent: true);
        start.MinWidth = 240;
        start.HorizontalAlignment = HorizontalAlignment.Center;
        // The default button, as on the Mac: Enter that no focused control takes gets started. Not
        // while the pairing dialog is up, though; an Enter it leaves unhandled must not close this.
        var enter = new KeyboardAccelerator { Key = VirtualKey.Enter };
        enter.Invoked += (_, e) =>
        {
            e.Handled = true;
            if (!VisualTreeHelper.GetOpenPopupsForXamlRoot(start.XamlRoot).Any(p => p.Child is ContentDialog)) Close();
        };
        start.KeyboardAccelerators.Add(enter);
        start.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(40, 48, 40, 32) };
        foreach (var e in new FrameworkElement[] { _portrait, title, body }) { e.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(e); }
        panel.Children.Add(_cards);
        panel.Children.Add(start);
        Content = new ScrollViewer { Content = panel };
        BuildCards();

        // Clawd standing, breathing, blinking and now and then waving; Orca may be opened or
        // installed meanwhile, so the cards follow along.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1 / 15.0);
        var lastCards = 0.0;
        timer.Tick += (_, _) =>
        {
            var t = clock.Elapsed.TotalSeconds;
            // On the system clock, as on the Mac, rather than from the window's opening, which would
            // always find Clawd blinking and halfway through a wave.
            DrawPortrait(Clock.Uptime);
            if (t - lastCards > 2)
            {
                lastCards = t;
                // Rebuilt only on a change, which would otherwise take keyboard focus off the cards.
                if (State() != _shownState) BuildCards();
                else _status?.Refresh();
            }
        };
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    /// <summary>What the cards show, apart from Orca's status, which is updated in place.</summary>
    private string State() => string.Join("|", Services.LoginItem.IsEnabled, _app.Settings.Shortcut.Display, _app.Model.Live);

    private void BuildCards()
    {
        _shownState = State();
        _status = new OrcaStatusCell(_app);
        _cards.Children.Clear();
        _cards.Children.Add(SettingsCards.Card("Orca", null, _status.Root, Ui.Glyph.Connect));
        _cards.Children.Add(SettingsCards.Card(L.Get("Welcome_Shortcut"), null, Ui.Secondary(_app.Settings.Shortcut.Display, wrap: false), Ui.Glyph.Keyboard));
        _cards.Children.Add(SettingsCards.Card(L.Get("Welcome_StartAtLogin"), null,
            SettingsCards.Toggle(() => Services.LoginItem.IsEnabled, () => { _app.ToggleLogin(); _shownState = State(); }), Ui.Glyph.Power));
        _cards.Children.Add(SettingsCards.Card(L.Get("Welcome_LiveTerminal"), L.Get("Welcome_LiveTerminalBody"),
            SettingsCards.LivePairing(_app, BuildCards, canUnpair: false), Ui.Glyph.Terminal));
    }

    /// <summary>The sprite at a whole number of pixels per unit, so it stays crisp at any scale.</summary>
    private void DrawPortrait(double t)
    {
        var scale = Content?.XamlRoot?.RasterizationScale ?? 1;
        var w = (int)(160 * scale);
        var h = (int)(120 * scale);
        var unit = Math.Max(1, Math.Floor(Math.Min(w / Sprite.CanvasW, h / (Sprite.CanvasH - 6))));
        var frame = _frame.Width == w && _frame.Height == h ? _frame : new Rasterizer(w, h);
        frame.Clear();
        frame.Translate(Math.Round((w - Sprite.CanvasW * unit) / 2), h - Sprite.CanvasH * unit);
        frame.Scale(unit, unit);
        Sprite.Render(frame, Activities.PortraitPose(t), []);
        if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h);
            _portrait.Source = _bitmap;
        }
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(_bitmap.PixelBuffer))
            stream.Write(frame.Pixels, 0, frame.Pixels.Length);
        _bitmap.Invalidate();
    }
}
