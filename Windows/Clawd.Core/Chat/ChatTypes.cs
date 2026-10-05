using Clawd.Core.Orca;

namespace Clawd.Core.Chat;

/// <summary>Sidebar groups, most pressing first.</summary>
public enum RowKind { Permission, Question, Working, Finished, Resting }

public sealed record ChatRow(OrcaAgent Agent, RowKind Kind, string? Title, string Status)
{
    /// <summary>Terminal tab title, tells apart agents sharing a worktree.</summary>
    public string Id => Agent.PaneKey;
    public bool NeedsYou => Kind <= RowKind.Question;
}

public enum NoticeTone { Success, Info, Failure }

/// <summary>A one-line result under the composer: what was sent, or why it wasn't.</summary>
public sealed record ChatNotice(NoticeTone Tone, string Text)
{
    /// <summary>The same text twice in a row still restarts the hide timer.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    public static ChatNotice Success(string text) => new(NoticeTone.Success, text);
    public static ChatNotice Info(string text) => new(NoticeTone.Info, text);
    public static ChatNotice Failure(string text) => new(NoticeTone.Failure, text);

    /// <summary>Confirmations step aside after a moment; failures stay until something else happens.</summary>
    public bool Lingers => Tone == NoticeTone.Failure;
}

public enum ChatMode { Chat, Terminal }
public enum Connection { Connected, NotRunning, Off, NotInstalled }

/// <summary>The approximate terminal view's content.</summary>
public abstract record ScreenState
{
    public sealed record Loading : ScreenState;
    public sealed record Failed : ScreenState;
    public sealed record Lines(IReadOnlyList<string> Rows) : ScreenState
    {
        public bool Equals(Lines? other) => other is not null && Rows.SequenceEqual(other.Rows);
        public override int GetHashCode() => Rows.Count;
    }
}
