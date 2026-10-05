using System.ComponentModel;
using Clawd.Core;
using Clawd.Core.Chat;
using Clawd.Core.Orca;
using Clawd.Native;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Clawd.UI;

/// <summary>
/// The chat: a flyout-style window that opens above Clawd (or the notification-area icon), closes
/// when you click elsewhere or press Esc, and keeps keyboard focus while open. Messages-style
/// layout: agents in a sidebar on the acrylic, the selected agent's exchange on the content layer.
/// </summary>
internal sealed class ChatWindow
{
    public const double Width = 920, Height = 640;

    private readonly AppController _app;
    private ChatModel Model => _app.Model;
    private Window? _window;
    private double _closedAt;
    private Grid? _root;
    private StackPanel? _sidebar;
    private Grid? _right;
    private TimelineView? _timeline;
    private TerminalScreenView? _terminal;
    private string? _terminalAgent;     // whose terminal it shows
    private TextBox? _composer;
    private InfoBar? _notice;
    private Button? _send;
    private string? _shownKey;          // what the right side was built for
    private bool _syncingSelection;

    public ChatWindow(AppController app)
    {
        _app = app;
        Model.PropertyChanged += ModelChanged;
    }

    public event Action? Closed;

    public bool IsOpen { get; private set; }

    /// <summary>True right after the chat closed. A click on Clawd first dismisses the chat (it loses
    /// focus) and then arrives as a click; without this it would reopen at once instead of toggling.</summary>
    public bool JustClosed => Clock.Uptime - _closedAt < 0.35;

    /// <summary>Columns and rows of the approximate terminal view, when it is showing.</summary>
    public (int Cols, int Rows)? TerminalGrid => IsOpen && Model.ShowsTerminal ? _terminal?.Grid : null;

    /// <summary>Where the live terminal view (WebView2 + xterm.js) goes once paired.</summary>
    public ContentControl LiveTerminalHost { get; } = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private Panel? _liveTerminalParent;

    /// <summary>The chat's visible area while it is open, physical pixels (without the invisible resize frame).</summary>
    public Win32.Rect? Bounds
    {
        get
        {
            if (!IsOpen || _window?.AppWindow is not { } app) return null;
            var left = app.Position.X + (app.Size.Width - app.ClientSize.Width) / 2;
            return new Win32.Rect { Left = left, Top = app.Position.Y, Right = left + app.ClientSize.Width, Bottom = app.Position.Y + app.ClientSize.Height };
        }
    }

    public void ShowAbove(Win32.Rect anchor)
    {
        var window = _window ??= Create();
        WindowChrome.Place(window.AppWindow, WindowChrome.Above(anchor, Width, Height));
        if (!IsOpen)
        {
            IsOpen = true;
            _shownKey = null;
            Rebuild();
        }
        window.Activate();
        Win32.SetForegroundWindow(WindowChrome.Handle(window));
        FocusComposer();
    }

    public void Close()
    {
        if (!IsOpen || _window is null) return;
        IsOpen = false;
        _closedAt = Clock.Uptime;
        _window.AppWindow.Hide();
        Closed?.Invoke();
    }

    private Window Create()
    {
        var w = new Window { SystemBackdrop = new DesktopAcrylicBackdrop(), Title = "Clawd" };
        WindowChrome.MakeFlyout(w);
        w.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) Close();
            // Focus set before the window is active doesn't stick: type straight into the chat.
            else if (IsOpen) FocusComposer();
        };
        // Closing from the system (Alt+F4) only hides it: the window is reused.
        w.AppWindow.Closing += (_, e) => { e.Cancel = true; Close(); };
        _root = new Grid();
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition());
        _sidebar = new StackPanel { Spacing = 2, Padding = new Thickness(8, 12, 8, 12) };
        var sideScroll = new ScrollViewer { Content = _sidebar };
        _root.Children.Add(sideScroll);
        var divider = Ui.Divider(vertical: true);
        Grid.SetColumn(divider, 1);
        _root.Children.Add(divider);
        var layer = new Border { Style = Ui.Res<Style>("ClawdLayer") };
        _right = new Grid();
        layer.Child = _right;
        Grid.SetColumn(layer, 2);
        _root.Children.Add(layer);
        _root.PreviewKeyDown += KeyDown;
        _root.AllowDrop = true;
        _root.DragOver += (_, e) =>
        {
            // Nothing to drop into for a session in Orca's chat.
            if (!e.DataView.Contains(StandardDataFormats.StorageItems) || Model.Current?.Agent.HasTerminal != true) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = L.Get(Model.ShowsTerminal ? "Chat_DropTerminal" : "Chat_DropMessage");
        };
        _root.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            Drop(items.Select(i => i.Path).ToList(), Model.Current);
        };
        w.Content = _root;
        return w;
    }

    // MARK: Keyboard

    private void KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.W) { Close(); e.Handled = true; }
        else if (ctrl && e.Key == VirtualKey.T)
        {
            if (Model.Current?.Agent.HasTerminal == true) Model.Mode = Model.Mode == ChatMode.Chat ? ChatMode.Terminal : ChatMode.Chat;
            e.Handled = true;
        }
        else if (ctrl && e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number9 && Model.Prompt is { } p && Model.Current is { } row && !Model.ShowsTerminal)
        {
            var n = e.Key - VirtualKey.Number0;
            if (p.Options.Any(o => o.Number == n) && !Model.Busy) _app.ChatAnswer(n, row.Agent);
            e.Handled = true;
        }
        // Esc closes the chat; in the terminal view it belongs to the terminal.
        else if (e.Key == VirtualKey.Escape && !Model.ShowsTerminal) { Close(); e.Handled = true; }
    }

    // MARK: Model → view

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsOpen || _root is null) return;
        switch (e.PropertyName)
        {
            case nameof(ChatModel.Rows) or nameof(ChatModel.Selected) or nameof(ChatModel.ShowResting) or nameof(ChatModel.Current):
                RebuildSidebar();
                if (RightKey() != _shownKey) RebuildRight();
                else { UpdateHeader(); _timeline?.Update(Model, _app); UpdateComposer(); }
                break;
            case nameof(ChatModel.Connection) or nameof(ChatModel.Mode) or nameof(ChatModel.Live):
                RebuildRight();
                break;
            case nameof(ChatModel.Timeline) or nameof(ChatModel.TimelineReady) or nameof(ChatModel.Prompt) or nameof(ChatModel.Busy):
                _timeline?.Update(Model, _app);
                UpdateComposer();
                // A permission dialog read off the screen: its answers take the focus the composer can't.
                if (e.PropertyName == nameof(ChatModel.Prompt) && _composer is { IsEnabled: false }) FocusComposer();
                break;
            case nameof(ChatModel.Notice):
                UpdateNotice();
                break;
            case nameof(ChatModel.Draft):
                if (_composer is not null && _composer.Text != Model.Draft) SetComposerText(Model.Draft);
                break;
            case nameof(ChatModel.Screen) or nameof(ChatModel.FittedRows) or nameof(ChatModel.Composing):
                _terminal?.Update(Model);
                break;
            case nameof(ChatModel.LiveStatus):
                UpdateLiveStatus();
                break;
        }
    }

    /// <summary>The right side is rebuilt only when what it is about changes; the header, timeline
    /// and composer update in place, so scrolling, expanded steps and typing are never interrupted
    /// (the status text changes with every tool and every minute, the state with every request).</summary>
    private string RightKey() => Model.Connection != Connection.Connected ? "conn:" + Model.Connection
        : Model.Current is { } r ? $"{r.Id}|{r.Agent.HasTerminal}|{Model.ShowsTerminal}|{Model.Live}" : "none";

    /// <summary>The agent of the row the right side was built for, as it is now: its state may have
    /// moved on since (the right side isn't rebuilt for that).</summary>
    private OrcaAgent Fresh(ChatRow built) => Model.Current is { } r && r.Id == built.Id ? r.Agent : built.Agent;

    private void Rebuild()
    {
        RebuildSidebar();
        RebuildRight();
    }

    private void RebuildSidebar()
    {
        if (_sidebar is null) return;
        _sidebar.Children.Clear();
        (string Title, RowKind[] Kinds)[] groups =
        [
            (L.Get("Chat_GroupNeedsYou"), [RowKind.Permission, RowKind.Question]),
            (L.Get("Chat_GroupWorking"), [RowKind.Working]),
            (L.Get("Chat_GroupFinished"), [RowKind.Finished]),
        ];
        foreach (var (title, kinds) in groups)
        {
            var rows = Model.Rows.Where(r => kinds.Contains(r.Kind)).ToList();
            if (rows.Count == 0) continue;
            _sidebar.Children.Add(SectionHeader(title));
            _sidebar.Children.Add(RowList(rows));
        }
        var resting = Model.Rows.Where(r => r.Kind == RowKind.Resting).ToList();
        if (resting.Count > 0)
        {
            // The whole header toggles the section.
            var header = new Button
            {
                Content = Ui.Row(6, Ui.Icon(Model.ShowResting ? Ui.Glyph.ChevronDown : Ui.Glyph.ChevronRight, "ClawdSecondaryIcon", 10), Ui.Caption(L.Format("Chat_GroupResting", resting.Count))),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 8, 0, 0),
            };
            header.Click += (_, _) => Model.ShowResting = !Model.ShowResting;
            _sidebar.Children.Add(header);
            if (Model.ShowResting) _sidebar.Children.Add(RowList(resting));
        }
    }

    private static TextBlock SectionHeader(string title)
    {
        var t = Ui.Caption(title);
        t.Margin = new Thickness(12, 10, 0, 4);
        return t;
    }

    private ListView RowList(List<ChatRow> rows)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single };
        foreach (var row in rows)
        {
            var item = new ListViewItem { Content = SidebarRow(row), Tag = row.Id, AllowDrop = true };
            // Drop files on an agent to start a message to it with their paths, or, on the agent whose
            // terminal is showing, to paste them there, as on the Mac.
            item.DragOver += (_, e) =>
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                e.Handled = true;
                // A session in Orca's chat takes nothing from Clawd; the window's handler would accept.
                if (!row.Agent.HasTerminal) { e.AcceptedOperation = DataPackageOperation.None; return; }
                e.AcceptedOperation = DataPackageOperation.Copy;
                // The row's own caption; the window's describes the agent on the right.
                e.DragUIOverride.Caption = L.Get(Model.ShowsTerminal && Model.Current?.Id == row.Id ? "Chat_DropTerminal" : "Chat_DropMessage");
            };
            item.Drop += async (_, e) =>
            {
                e.Handled = true;
                var files = await e.DataView.GetStorageItemsAsync();
                Drop(files.Select(f => f.Path).ToList(), row);
            };
            list.Items.Add(item);
            if (row.Id == Model.Current?.Id) list.SelectedItem = item;
        }
        list.SelectionChanged += (_, _) =>
        {
            if (_syncingSelection || list.SelectedItem is not ListViewItem { Tag: string id }) return;
            _syncingSelection = true;
            Model.Selected = id;
            _syncingSelection = false;
            FocusComposer();
        };
        return list;
    }

    private static Grid SidebarRow(ChatRow row)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Ui.Stack(1, Ui.Text(row.Agent.Name, wrap: false, maxLines: 1), Ui.Caption(row.Title ?? row.Status));
        grid.Children.Add(text);
        // Only states worth a glance get a mark; resting agents stay quiet.
        FrameworkElement? mark = row.Kind switch
        {
            RowKind.Permission or RowKind.Question => Ui.Icon(Ui.Glyph.Dot, "ClawdOrangeIcon", 10),
            RowKind.Working => Ui.Spinner(14),
            RowKind.Finished => Ui.Icon(Ui.Glyph.Check, "ClawdSuccessIcon", 12),
            _ => null,
        };
        if (mark is not null)
        {
            mark.VerticalAlignment = VerticalAlignment.Center;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mark, L.Get(row.Kind switch
            {
                RowKind.Permission => "Chat_MarkPermission", RowKind.Question => "Chat_MarkQuestion", RowKind.Working => "Chat_MarkWorking", _ => "Chat_MarkFinished",
            }));
            Grid.SetColumn(mark, 1);
            grid.Children.Add(mark);
        }
        return grid;
    }

    private void RebuildRight()
    {
        if (_right is null) return;
        // Leaving an agent's terminal for another agent: what the IME holds isn't for the new one,
        // nor, half-typed, for the old.
        if (_terminal is not null && _terminalAgent != Model.Current?.Id) _terminal.Discard();
        _shownKey = RightKey();
        _right.Children.Clear();
        _right.RowDefinitions.Clear();
        _timeline = null;
        _terminal = null;
        _composer = null;
        _notice = null;
        _send = null;
        _subtitle = null;
        _subtitleStyle = null;
        if (Model.Connection != Connection.Connected)
        {
            _right.Children.Add(ConnectionPlaceholder());
            return;
        }
        if (Model.Current is not { } row)
        {
            _right.Children.Add(Placeholder(Ui.Glyph.Chat, L.Get("Chat_NoAgents"), L.Get("Chat_NoAgentsBody")));
            return;
        }
        _right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _right.RowDefinitions.Add(new RowDefinition());
        _right.Children.Add(Header(row));
        FrameworkElement body = Model.ShowsTerminal ? TerminalArea(row) : Conversation(row);
        Grid.SetRow(body, 1);
        _right.Children.Add(body);
    }

    /// <summary>Title and subtitle as a window's title area would show them, with the view switch and
    /// the Orca button.</summary>
    private Grid Header(ChatRow row)
    {
        var grid = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Text and colour are kept current by UpdateHeader.
        _subtitle = Ui.Text("", "ClawdSecondaryText", wrap: false, maxLines: 1);
        grid.Children.Add(Ui.Stack(1, Ui.Text(row.Agent.Name, "BodyStrongTextBlockStyle", wrap: false, maxLines: 1), _subtitle));
        UpdateHeader();

        var bar = new SelectorBar();
        var chat = new SelectorBarItem { Text = L.Get("Chat_TabChat"), Icon = new FontIcon { Glyph = Ui.Glyph.Chat } };
        var terminal = new SelectorBarItem { Text = L.Get("Chat_TabTerminal"), Icon = new FontIcon { Glyph = Ui.Glyph.Terminal } };
        bar.Items.Add(chat);
        bar.Items.Add(terminal);
        bar.SelectedItem = Model.Mode == ChatMode.Chat ? chat : terminal;
        bar.SelectionChanged += (_, _) => Model.Mode = bar.SelectedItem == terminal ? ChatMode.Terminal : ChatMode.Chat;
        ToolTipService.SetToolTip(bar, L.Get("Chat_TabTip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, L.Get("Chat_ViewPicker"));
        var open = Ui.IconButton(Ui.Glyph.OpenIn, L.Get("Chat_OpenInOrca"), () => _app.OpenFromChat(row.Agent));
        // A session without a terminal has only the chat view.
        var controls = row.Agent.HasTerminal ? Ui.Row(8, bar, open) : Ui.Row(8, open);
        Grid.SetColumn(controls, 1);
        grid.Children.Add(controls);
        return grid;
    }

    private TextBlock? _subtitle;

    private void UpdateHeader()
    {
        if (_subtitle is null || Model.Current is not { } row) return;
        _subtitle.Text = string.Join(" · ", new[] { row.Title, row.Status }.Where(s => s is not null));
        var style = row.NeedsYou ? "ClawdOrangeText" : "ClawdSecondaryText";
        if (_subtitleStyle != style) { _subtitleStyle = style; _subtitle.Style = Ui.Res<Style>(style); }
    }

    private string? _subtitleStyle;

    // MARK: Conversation

    private Grid Conversation(ChatRow row)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _timeline = new TimelineView(row.Id);
        _timeline.Update(Model, _app);
        grid.Children.Add(_timeline.Root);
        var composer = Composer(row);
        Grid.SetRow(composer, 1);
        grid.Children.Add(composer);
        return grid;
    }

    /// <summary>Replaces the draft with the caret after it, so typing continues where it left off.</summary>
    private void SetComposerText(string text)
    {
        _composer!.Text = text;
        _composer.Select(text.Length, 0);
    }

    private StackPanel Composer(ChatRow row)
    {
        _notice = new InfoBar { IsClosable = false, IsOpen = false, Margin = new Thickness(0, 0, 0, 6) };
        _composer = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120,
            Text = Model.Draft,
        };
        _composer.SelectionStart = _composer.Text.Length;
        _composer.TextChanged += (_, _) => { Model.Draft = _composer.Text; UpdateComposer(); };
        // The first time the chat opens, the window activates before the composer is in it.
        _composer.Loaded += (_, _) => { if (IsOpen) FocusComposer(); };
        // As on the Mac, an Enter that lands while Korean (or any IME) text is being composed only
        // commits it; the next Enter sends. Windows reports compositions unevenly: the IME starts the
        // next syllable's before ending the previous one's (start 녕, end 안), ends a syllable on
        // Space or an arrow without saying so, and may end the last one just before passing on the
        // Enter that ended it. So a composition counts as open from its start until it ends or a key
        // gets past the IME (it only lets keys through once it holds nothing), and a composition that
        // ended since the last key came up was ended by this Enter.
        var composing = 0;
        var endedByThisKey = false;
        _composer.TextCompositionStarted += (_, _) => composing++;
        _composer.TextCompositionEnded += (_, _) => { composing = Math.Max(0, composing - 1); endedByThisKey = true; };
        _composer.KeyUp += (_, _) => endedByThisKey = false;
        _composer.LostFocus += (_, _) => { composing = 0; endedByThisKey = false; };
        _composer.PreviewKeyDown += (_, e) =>
        {
            var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (e.Key == VirtualKey.Enter && !shift)
            {
                e.Handled = true;
                if (composing > 0 || endedByThisKey)
                {
                    if (composing > 0) CompleteComposition();
                    composing = 0;
                    endedByThisKey = false;
                    return;
                }
                Submit();
                return;
            }
            if (!IsModifierOrImeKey(e.Key)) composing = 0;
            // With nothing typed, ↑ and ↓ walk the agents; otherwise they move the cursor.
            if (e.Key is VirtualKey.Up or VirtualKey.Down && _composer.Text.Length == 0)
            {
                e.Handled = true;
                Model.Move(e.Key == VirtualKey.Up ? -1 : 1);
            }
        };
        _send = new Button
        {
            Content = new FontIcon { Glyph = Ui.Glyph.Send, FontSize = 14 },
            Style = Ui.Res<Style>("AccentButtonStyle"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(10, 7, 10, 7),
        };
        ToolTipService.SetToolTip(_send, L.Get("Chat_SendTip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_send, L.Get("Chat_Send"));
        _send.Click += (_, _) => Submit();
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(_composer);
        Grid.SetColumn(_send, 1);
        line.Children.Add(_send);
        UpdateComposer();
        UpdateNotice();
        var panel = new StackPanel { Padding = new Thickness(16) };
        panel.Children.Add(_notice);
        panel.Children.Add(line);
        return panel;
    }

    /// <summary>Asks the IME to commit what it is composing in the focused window; TSF passes this
    /// IMM call on to its IMEs. Unlike moving focus away and back, it doesn't hand activation to the
    /// IME's own windows, which would close the chat.</summary>
    private static void CompleteComposition()
    {
        var focus = Win32.GetFocus();
        var context = Win32.ImmGetContext(focus);
        if (context == IntPtr.Zero) return;
        Win32.ImmNotifyIME(context, Win32.NI_COMPOSITIONSTR, Win32.CPS_COMPLETE, 0);
        Win32.ImmReleaseContext(focus, context);
    }

    internal static bool IsModifierOrImeKey(VirtualKey k) => k is VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.Menu or VirtualKey.LeftMenu
        or VirtualKey.RightMenu or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.Hangul or VirtualKey.Hanja
        or VirtualKey.CapitalLock or (VirtualKey)229;   // VK_PROCESSKEY: the IME is handling it

    private void UpdateComposer()
    {
        if (_composer is null || _send is null || Model.Current is not { } row) return;
        // A permission dialog takes only its own answers: typed text would land in it. A session
        // without a terminal takes nothing from Clawd.
        var canType = row.Kind != RowKind.Permission && row.Agent.HasTerminal;
        // A permission answered: typing goes back to the composer, which the dialog's answers had
        // the focus instead of.
        var reopened = canType && !_composer.IsEnabled;
        _composer.IsEnabled = canType;
        if (reopened && IsOpen) FocusComposer();
        _composer.PlaceholderText = canType ? L.Get(row.Kind == RowKind.Question ? "Chat_PlaceholderReply" : "Chat_PlaceholderMessage")
            : row.Kind != RowKind.Permission ? L.Get("Timeline_AnswerInOrca")
            : Model.Prompt is { } p ? L.Format("Chat_PlaceholderAnswer", p.Options.Count) : L.Get("Chat_PlaceholderAnswerInOrca");
        // Like Messages: no greyed-out button on an empty field; it appears once there is something to send.
        _send.Visibility = canType && Model.Draft.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _send.IsEnabled = !Model.Busy;
    }

    private void UpdateNotice()
    {
        if (_notice is null) return;
        if (Model.Notice is not { } notice)
        {
            _notice.IsOpen = false;
            return;
        }
        _notice.Severity = notice.Tone switch
        {
            NoticeTone.Success => InfoBarSeverity.Success,
            NoticeTone.Failure => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        _notice.Message = notice.Text;
        _notice.IsOpen = true;
        // Confirmations step aside after a moment; failures stay until something else happens.
        if (!notice.Lingers)
        {
            var id = notice.Id;
            _app.After(4, () => { if (Model.Notice?.Id == id) Model.Notice = null; });
        }
    }

    private void Submit()
    {
        if (Model.Current is not { } row || row.Kind == RowKind.Permission || !row.Agent.HasTerminal || Model.Busy) return;
        var text = Model.Draft.Trim();
        if (text.Length == 0) return;
        _app.ChatSend(text, row.Agent);
    }

    private void FocusComposer() => _app.Dispatcher.TryEnqueue(() =>
    {
        if (_composer is { IsEnabled: true }) _composer.Focus(FocusState.Programmatic);
        // A permission dialog: on its first answer, as on the Mac nothing else takes Enter. Left to
        // WinUI, focus would land on "Open in Orca", and Enter would leave the chat for Orca.
        else if (_timeline?.FirstAnswer is { } answer) Ui.FocusWhenLoaded(answer);
        else _terminal?.Focus();
    });

    // MARK: Terminal

    private Grid TerminalArea(ChatRow row)
    {
        var grid = new Grid();
        if (Model.Live)
        {
            // The app's one long-lived WebView2 terminal, moved in here.
            var host = new Grid();
            // Detached from where it was last put: its Parent reads null until that page has loaded.
            _liveTerminalParent?.Children.Remove(LiveTerminalHost);
            host.Children.Add(LiveTerminalHost);
            _liveTerminalParent = host;
            _liveStatusText = Ui.Secondary("");
            _liveStatus = Ui.Card(_liveStatusText, 12);
            _liveStatus.HorizontalAlignment = HorizontalAlignment.Center;
            _liveStatus.VerticalAlignment = VerticalAlignment.Center;
            host.Children.Add(_liveStatus);
            UpdateLiveStatus();
            grid.Children.Add(host);
        }
        else
        {
            _terminal = new TerminalScreenView(keys => _app.SendKeys(keys, Fresh(row)), text => Model.Composing = text,
                size => Model.Viewport = size);
            _terminalAgent = row.Id;
            _terminal.Update(Model);
            grid.Children.Add(_terminal.Root);
        }
        return grid;
    }

    private Border? _liveStatus;
    private TextBlock? _liveStatusText;

    /// <summary>Connecting / error text over the live terminal, changed in place so the WebView2 stays put.</summary>
    private void UpdateLiveStatus()
    {
        if (_liveStatus is null || _liveStatusText is null) return;
        _liveStatusText.Text = Model.LiveStatus ?? "";
        _liveStatus.Visibility = Model.LiveStatus is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // MARK: Drops and placeholders

    /// <summary>Dropped files: pasted into the terminal in the terminal view, otherwise added to the
    /// agent's message as paths.</summary>
    private void Drop(List<string> paths, ChatRow? row)
    {
        var text = FileDrop.Text(paths);
        if (text.Length == 0 || row is null || !row.Agent.HasTerminal) return;
        if (Model.ShowsTerminal && Model.Current?.Id == row.Id)
        {
            _app.SendKeys(FileDrop.Paste(text), row.Agent);
            return;
        }
        Model.SetDraft(row.Id, FileDrop.Append(text, Model.Drafts.GetValueOrDefault(row.Id) ?? ""));
        Model.Selected = row.Id;
        Model.Mode = ChatMode.Chat;
        if (_composer is not null) SetComposerText(Model.Draft);
        FocusComposer();
    }

    public static StackPanel Placeholder(string glyph, string title, string body, UIElement? action = null)
    {
        var panel = Ui.Stack(8, Ui.Icon(glyph, "ClawdSecondaryIcon", 40), Ui.Text(title, "SubtitleTextBlockStyle"), Ui.Secondary(body));
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Center;
        panel.MaxWidth = 420;
        foreach (var t in panel.Children.OfType<TextBlock>()) t.TextAlignment = TextAlignment.Center;
        foreach (var c in panel.Children.OfType<FrameworkElement>()) c.HorizontalAlignment = HorizontalAlignment.Center;
        if (action is FrameworkElement a)
        {
            a.Margin = new Thickness(0, 8, 0, 0);
            a.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(a);
        }
        return panel;
    }

    /// <summary>Why there is nothing to show, and the one thing that fixes it.</summary>
    private StackPanel ConnectionPlaceholder() => Model.Connection switch
    {
        Connection.NotRunning => Placeholder(Ui.Glyph.Moon, L.Get("Chat_OrcaNotRunning"), L.Get("Chat_OrcaNotRunningBody"),
            Ui.Button(L.Get("Chat_OpenOrca"), () => _app.Orca.Launch(), accent: true)),
        Connection.Off => Placeholder(Ui.Glyph.Pause, L.Get("Chat_OrcaOff"), L.Get("Chat_OrcaOffBody"),
            Ui.Button(L.Get("Chat_OpenSettings"), () => _app.OpenSettings())),
        _ => Placeholder(Ui.Glyph.Download, L.Get("Chat_NoOrca"), L.Get("Chat_NoOrcaBody"),
            new HyperlinkButton { Content = L.Get("Chat_DownloadOrca"), NavigateUri = OrcaInstallation.Homepage }),
    };

}
