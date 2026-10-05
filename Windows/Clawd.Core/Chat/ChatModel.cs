using System.ComponentModel;
using System.Runtime.CompilerServices;
using Clawd.Core.Orca;
using Clawd.Core.Transcripts;

namespace Clawd.Core.Chat;

/// <summary>What the chat window shows, kept by the app and observed by the views.</summary>
public sealed class ChatModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The selected agent changed (deferred by the app, which reloads what it shows).</summary>
    public event Action<string?>? SelectionChanged;
    /// <summary>The view's mode or terminal area changed: the PTY fit and live stream follow.</summary>
    public event Action? LayoutChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private ChatMode _mode = ChatMode.Chat;
    public ChatMode Mode { get => _mode; set { if (Set(ref _mode, value)) LayoutChanged?.Invoke(); } }

    private (double Width, double Height) _viewport;
    /// <summary>Terminal area in DIPs, drives the PTY fit.</summary>
    public (double Width, double Height) Viewport { get => _viewport; set { if (Set(ref _viewport, value)) LayoutChanged?.Invoke(); } }

    private string? _selected;
    public string? Selected { get => _selected; set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(Current)); SelectionChanged?.Invoke(value); } } }

    private IReadOnlyList<TimelineItem> _timeline = [];
    /// <summary>Live steps from the Claude Code transcript.</summary>
    public IReadOnlyList<TimelineItem> Timeline { get => _timeline; set { if (!_timeline.SequenceEqual(value)) { _timeline = value; OnPropertyChanged(); } } }

    private bool _timelineReady;
    /// <summary>The first read for the selected agent came back.</summary>
    public bool TimelineReady { get => _timelineReady; set => Set(ref _timelineReady, value); }

    private int? _fittedRows;
    /// <summary>Rows the PTY was resized to, null when not fitted.</summary>
    public int? FittedRows { get => _fittedRows; set => Set(ref _fittedRows, value); }

    private bool _live;
    /// <summary>Paired with Orca: real terminal stream + emulator.</summary>
    public bool Live { get => _live; set => Set(ref _live, value); }

    private string? _liveStatus;
    /// <summary>Connecting / error text over the live terminal.</summary>
    public string? LiveStatus { get => _liveStatus; set => Set(ref _liveStatus, value); }

    private string _composing = "";
    /// <summary>IME text not yet committed to the terminal.</summary>
    public string Composing { get => _composing; set => Set(ref _composing, value); }

    private ScreenState _screen = new ScreenState.Loading();
    public ScreenState Screen { get => _screen; set => Set(ref _screen, value); }

    private IReadOnlyList<ChatRow> _rows = [];
    public IReadOnlyList<ChatRow> Rows
    {
        get => _rows;
        set { if (!_rows.SequenceEqual(value)) { _rows = value; OnPropertyChanged(); OnPropertyChanged(nameof(Current)); } }
    }

    private PermissionPrompt? _prompt;
    public PermissionPrompt? Prompt { get => _prompt; set { if (!Equals(_prompt, value)) { _prompt = value; OnPropertyChanged(); } } }

    /// <summary>Unsent text kept per agent.</summary>
    public Dictionary<string, string> Drafts { get; } = [];

    private ChatNotice? _notice;
    public ChatNotice? Notice { get => _notice; set => Set(ref _notice, value); }

    private readonly HashSet<string> _busyPanes = [];
    /// <summary>Agents with a send or screen read in flight.</summary>
    public void SetBusy(string pane, bool busy)
    {
        if (busy ? _busyPanes.Add(pane) : _busyPanes.Remove(pane)) OnPropertyChanged(nameof(Busy));
    }

    private Connection _connection = Connection.Connected;
    public Connection Connection { get => _connection; set => Set(ref _connection, value); }

    private bool _showResting;
    public bool ShowResting { get => _showResting; set => Set(ref _showResting, value); }

    public ChatRow? Current => Rows.FirstOrDefault(r => r.Id == Selected) ?? Rows.FirstOrDefault();

    /// <summary>The terminal view is showing: chosen, and the selected agent has a terminal.</summary>
    public bool ShowsTerminal => Mode == ChatMode.Terminal && Current?.Agent.HasTerminal == true;

    /// <summary>The selected agent has a request in flight.</summary>
    public bool Busy => Current is { } c && _busyPanes.Contains(c.Id);

    public string Draft
    {
        get => Current is { } c && Drafts.TryGetValue(c.Id, out var d) ? d : "";
        set
        {
            if (Current is not { } c || Draft == value) return;
            if (value.Length == 0) Drafts.Remove(c.Id); else Drafts[c.Id] = value;
            OnPropertyChanged();
        }
    }

    public void SetDraft(string pane, string? text)
    {
        if (string.IsNullOrEmpty(text)) Drafts.Remove(pane); else Drafts[pane] = text;
        if (pane == Current?.Id) OnPropertyChanged(nameof(Draft));
    }

    /// <summary>Arrow keys walk the visible rows; resting ones count only when their section is open.</summary>
    public void Move(int step)
    {
        var visible = ShowResting ? Rows : Rows.Where(r => r.Kind != RowKind.Resting).ToList();
        if (visible.Count == 0) return;
        var i = Math.Max(0, visible.ToList().FindIndex(r => r.Id == Current?.Id));
        Selected = visible[Math.Clamp(i + step, 0, visible.Count - 1)].Id;
    }

    public void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
