namespace Clawd.Core;

/// <summary>
/// Environment switches used by scripted UI tests and the demo. The same names as the Mac app,
/// so Scripts/demo/make_demo.py output works unchanged. None are read in normal use.
/// </summary>
public static class TestHooks
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>Open the chat in the terminal view.</summary>
    public static bool TerminalView => Env("CLAWD_TERMINAL_VIEW") is not null;
    /// <summary>Show the pairing dialog a second after launch.</summary>
    public static bool ShowPairing => Env("CLAWD_SHOW_PAIRING") is not null;
    /// <summary>Select this pane when the chat opens.</summary>
    public static string? Select => Env("CLAWD_SELECT");
    /// <summary>Pre-fill the composer.</summary>
    public static string? Draft => Env("CLAWD_DRAFT");
    /// <summary>Replay a saved `orca worktree ps --json` instead of asking Orca.</summary>
    public static string? FakeOrca => Env("CLAWD_FAKE_ORCA");
    /// <summary>A folder of transcripts named after panes ("tab:leaf" with ":" as "_"), used instead
    /// of looking up Claude Code sessions. Together with FakeOrca, a complete demo.</summary>
    public static string? FakeTranscripts => Env("CLAWD_FAKE_TRANSCRIPTS");
    /// <summary>Behave as if not paired (no credential access).</summary>
    public static bool NoLive => Env("CLAWD_NO_LIVE") is not null;
    /// <summary>Force "light" or "dark", for screenshots.</summary>
    public static string? Appearance => Env("CLAWD_APPEARANCE") is "light" or "dark" ? Env("CLAWD_APPEARANCE") : null;
    /// <summary>Open the chat right after launch.</summary>
    public static bool OpenChat => Env("CLAWD_OPEN_CHAT") is not null;
    /// <summary>Show the first-launch welcome even if it was seen before.</summary>
    public static bool Welcome => Env("CLAWD_WELCOME") is not null;
    /// <summary>Open the settings window right after launch.</summary>
    public static bool OpenSettings => Env("CLAWD_OPEN_SETTINGS") is not null;
    /// <summary>"en" or "ko": show the UI in that language instead of the display language's, for
    /// screenshots and for checking a translation without signing out.</summary>
    public static string? UiLanguage => Env("CLAWD_UI_LANG") is "en" or "ko" ? Env("CLAWD_UI_LANG") : null;
    /// <summary>"x,y" in physical screen pixels: drop Clawd on the monitor there instead of the one
    /// under the pointer, and open the startup chat above Clawd rather than the tray icon, so a
    /// scripted run can stay on a test monitor.</summary>
    public static (int X, int Y)? StartAt => Env("CLAWD_START_AT")?.Split(',') is [var x, var y]
        && int.TryParse(x, out var px) && int.TryParse(y, out var py) ? (px, py) : null;

    /// <summary>A file path or URL to read the release list from instead of GitHub's API, in the
    /// API's format; its links may point anywhere, file: URLs included. Clawd then checks at every
    /// launch rather than once a day.</summary>
    public static string? UpdateFeed => Env("CLAWD_UPDATE_FEED");

    private static string PaneFile(string pane, string suffix) => pane.Replace(':', '_') + suffix;

    public static string? FakeTranscript(string pane)
    {
        if (FakeTranscripts is not { } dir) return null;
        var path = Path.Combine(dir, PaneFile(pane, ".jsonl"));
        return File.Exists(path) ? path : null;
    }

    /// <summary>A terminal screen for a pane, "&lt;pane&gt;.screen.txt" in the same folder.</summary>
    public static string[]? FakeScreen(string pane)
    {
        if (FakeTranscripts is not { } dir) return null;
        var path = Path.Combine(dir, PaneFile(pane, ".screen.txt"));
        try { return File.ReadAllText(path).Replace("\r\n", "\n").Split('\n'); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
