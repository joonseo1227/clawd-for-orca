using Clawd.Core.Orca;

namespace Clawd.Core.Tests;

[UseCulture("ko-KR")]
public class PermissionPromptTests
{
    /// <summary>A Claude Code Bash permission dialog as Orca renders it, with some conversation above it.</summary>
    public static List<string> BashDialog(int selected = 1)
    {
        string Option(int n, string label) => (n == selected ? "❯ " : "  ") + $"{n}. {label}";
        return
        [
            "⏺ I'll run the test suite to check the change.",
            "",
            new string('─', 80),
            " Bash command",
            "",
            "   npm test -- --watch=false",
            "   Run the unit tests",
            "",
            " Do you want to proceed?",
            " " + Option(1, "Yes"),
            " " + Option(2, "Yes, and don't ask again for npm test commands in C:\\Users\\me\\project"),
            " " + Option(3, "No, and tell Claude what to do differently (esc)"),
            "",
            " Esc to cancel · Tab to amend · ctrl+e to explain",
        ];
    }

    [Fact]
    public void ParsesClaudeCodeBashDialog()
    {
        var prompt = PermissionPrompt.Parse(BashDialog());
        Assert.NotNull(prompt);
        Assert.Equal("Do you want to proceed?", prompt.Question);
        Assert.Equal(["Bash command", "npm test -- --watch=false", "Run the unit tests"], prompt.Detail);
        Assert.Equal([1, 2, 3], prompt.Options.Select(o => o.Number));
        Assert.Equal(["허용", "항상 허용", "거절"], prompt.Options.Select(o => o.Title));
        Assert.Equal("Yes, and don't ask again for npm test commands in C:\\Users\\me\\project", prompt.Options[1].Label);
        Assert.Equal(['1', '2', '3'], prompt.Options.Select(o => o.Shortcut!.Value));
        Assert.Equal("Bash command", prompt.Title);
        Assert.Equal("npm test -- --watch=false", prompt.Command);
        Assert.Equal(["Run the unit tests"], prompt.Explanation);
    }

    /// <summary>A PowerShell dialog as Claude Code 2.1.288 and later draw it: the description first, then
    /// the command between dashed rules.</summary>
    public static List<string> PowerShellDialog()
    {
        var dashes = new string('╌', 78);
        return
        [
            "⏺ I'll write the test file.",
            "",
            new string('─', 80),
            " PowerShell command",
            " Write clawd-test to note1.txt",
            $" {dashes} ",
            " Set-Content note1.txt clawd-test",
            $" {dashes} ",
            " Do you want to proceed?",
            " ❯ 1. Yes",
            "   2. Yes, and don’t ask again for: Set-Content *",
            "   3. No",
            "",
            " Esc to cancel · Tab to amend",
        ];
    }

    [Fact]
    public void ParsesDialogWithCommandInDashedBox()
    {
        var prompt = PermissionPrompt.Parse(PowerShellDialog());
        Assert.NotNull(prompt);
        Assert.Equal("PowerShell command", prompt.Title);
        Assert.Equal("Set-Content note1.txt clawd-test", prompt.Command);
        Assert.Equal(["Write clawd-test to note1.txt"], prompt.Explanation);
        Assert.Equal(["허용", "항상 허용", "거절"], prompt.Options.Select(o => o.Title));
    }

    [Fact]
    public void DashedBoxKeepsMultiLineCommandAndNotesBelowIt()
    {
        var dashes = new string('╌', 60);
        string[] screen =
        [
            new string('─', 64),
            " Bash command",
            " │ Build the app and",
            " │ run its tests",
            $" {dashes} ",
            " │ dotnet build &&",
            " │ dotnet test",
            $" {dashes} ",
            " Network access to github.com",
            "",
            " Do you want to proceed?",
            " ❯ 1. Yes",
            "   2. No",
        ];
        var prompt = PermissionPrompt.Parse(screen);
        Assert.NotNull(prompt);
        Assert.Equal("dotnet build &&\ndotnet test", prompt.Command);
        Assert.Equal(["Build the app and", "run its tests", "Network access to github.com"], prompt.Explanation);
    }

    [Fact]
    public void LongBoxedCommandKeepsItsFirstLines()
    {
        var dashes = new string('╌', 40);
        List<string> screen = [new string('─', 40), "Bash command", "Generate fixtures", dashes];
        screen.AddRange(Enumerable.Range(1, 10).Select(i => $"line {i}"));
        screen.AddRange([dashes, "Do you want to proceed?", "❯ 1. Yes", "  2. No"]);
        var prompt = PermissionPrompt.Parse(screen);
        Assert.NotNull(prompt);
        Assert.Equal("Bash command", prompt.Title);
        Assert.Equal(string.Join("\n", Enumerable.Range(1, PermissionPrompt.BoxedLines).Select(i => $"line {i}")), prompt.Command);
        Assert.Equal(["Generate fixtures"], prompt.Explanation);
    }

    /// <summary>The Bash dialog of Claude Code 2.1.289 in manual mode, as read off Orca's screen: a tip
    /// above the description, and an answer that switches to auto mode.</summary>
    [Fact]
    public void ParsesTheDialogWithATipAndAutoMode()
    {
        var dashes = new string('╌', 98);
        var prompt = PermissionPrompt.Parse(
        [
            "● Writing hello to note1.txt",
            "  ⎿  $ echo hello > note1.txt",
            new string('─', 98),
            " Bash command",
            " Tip: auto mode handles these prompts for you — choose \"switch to auto mode\" below",
            " Write hello to note1.txt",
            dashes,
            " echo hello > note1.txt",
            dashes,
            " Do you want to proceed?",
            " ❯ 1. Yes",
            "   2. Yes, and always allow access to C:\\clawd3-work\\clawd-test from this project",
            "   3. Yes, and switch to auto mode · auto mode handles these prompts for you",
            "   4. No",
            " Esc to cancel · Tab to amend",
        ]);
        Assert.NotNull(prompt);
        Assert.Equal("echo hello > note1.txt", prompt.Command);
        Assert.Equal(["Write hello to note1.txt"], prompt.Explanation);
        Assert.Equal(["허용", "항상 허용", "허용 후 자동 모드", "거절"], prompt.Options.Select(o => o.Title));
    }

    /// <summary>The same dialog in a 60-column pane: the tip wraps onto a second line, which isn't the
    /// request either.</summary>
    [Fact]
    public void SkipsATipWrappedInANarrowPane()
    {
        var dashes = new string('╌', 60);
        var prompt = PermissionPrompt.Parse(
        [
            new string('─', 60),
            " Bash command",
            " Tip: auto mode handles these prompts for you — choose",
            " \"switch to auto mode\" below",
            " Write hello to note1.txt",
            dashes,
            " echo hello > note1.txt",
            dashes,
            " Do you want to proceed?",
            " ❯ 1. Yes",
            "   2. Yes, and always allow access to C:\\clawd3-work\\clawd-test",
            "   from this project",
            "   3. Yes, and switch to auto mode · auto mode handles these",
            "   prompts for you",
            "   4. No",
            " Esc to cancel · Tab to amend",
        ]);
        Assert.NotNull(prompt);
        Assert.Equal(["Bash command", "Write hello to note1.txt", "echo hello > note1.txt"], prompt.Detail);
        Assert.Equal("echo hello > note1.txt", prompt.Command);
        Assert.Equal(["Write hello to note1.txt"], prompt.Explanation);
    }

    [Fact]
    public void SelectionPositionDoesNotMatter() => Assert.Equal(3, PermissionPrompt.Parse(BashDialog(selected: 3))?.Options.Count);

    [Fact]
    public void ParsesBoxedDialog()
    {
        string[] screen =
        [
            "╭──────────────────────────────────────╮",
            "│ Edit file                            │",
            "│   Sources/App.swift                  │",
            "│ Do you want to make this edit?       │",
            "│ ❯ 1. Yes                             │",
            "│   2. Yes, allow all edits this session│",
            "│   3. No (esc)                        │",
            "╰──────────────────────────────────────╯",
        ];
        var prompt = PermissionPrompt.Parse(screen);
        Assert.NotNull(prompt);
        Assert.Equal("Do you want to make this edit?", prompt.Question);
        Assert.Equal(["Edit file", "Sources/App.swift"], prompt.Detail);
        Assert.Equal(["Yes", "Yes, allow all edits this session", "No (esc)"], prompt.Options.Select(o => o.Label));
    }

    [Fact]
    public void ClaudeReplyWithNumberedQuestionIsNotADialog()
    {
        // Claude asking in prose: numbered choices but nothing selected, then the input box.
        string[] screen =
        [
            "  The migration touches three tables.",
            "",
            "  Do you want me to:",
            "  1. Apply it now",
            "  2. Write a dry-run script first",
            "",
            new string('─', 80),
            "❯ ",
            new string('─', 80),
            "  ? for shortcuts",
        ];
        Assert.Null(PermissionPrompt.Parse(screen));
    }

    [Fact]
    public void NonContiguousNumberingIsNotADialog()
    {
        var screen = BashDialog();
        screen[11] = "   4. No, and tell Claude what to do differently (esc)";
        Assert.Null(PermissionPrompt.Parse(screen));
    }

    [Fact]
    public void SingleOptionIsNotADialog() => Assert.Null(PermissionPrompt.Parse(["Do you want to proceed?", "❯ 1. Yes"]));

    [Fact]
    public void DialogBeyondSearchDepthIsIgnored()
    {
        var screen = BashDialog().Concat(Enumerable.Repeat("", PermissionPrompt.SearchDepth)).ToList();
        Assert.Null(PermissionPrompt.Parse(screen));
        // Just inside the window it is still found.
        var near = BashDialog().Concat(Enumerable.Repeat("", PermissionPrompt.SearchDepth - BashDialog().Count)).ToList();
        Assert.NotNull(PermissionPrompt.Parse(near));
    }

    [Fact]
    public void DetailKeepsAtMostSixLines()
    {
        var screen = new List<string> { new('─', 40) };
        screen.AddRange(Enumerable.Range(1, 9).Select(i => $"line {i}"));
        screen.AddRange(["Do you want to proceed?", "❯ 1. Yes", "  2. No"]);
        var prompt = PermissionPrompt.Parse(screen);
        Assert.NotNull(prompt);
        Assert.Equal(Enumerable.Range(4, 6).Select(i => $"line {i}"), prompt.Detail);
    }

    [Fact]
    public void OptionTitlesAndShortcuts()
    {
        var custom = new PermissionPrompt.Option(10, "Something else");
        Assert.Equal("Something else", custom.Title);
        Assert.Null(custom.Shortcut);
    }

    [Fact]
    public void PromptsCompareByContent()
    {
        // Answering re-reads the screen and sends only if the very same prompt is still there.
        var a = PermissionPrompt.Parse(BashDialog())!;
        Assert.Equal(a, PermissionPrompt.Parse(BashDialog()));
        Assert.NotEqual(a, new PermissionPrompt("Do you want to make this edit?", a.Detail, a.Options));
    }
}

public class TerminalDraftTests
{
    private static readonly string Rule = new('─', 60);

    private static List<string> InputScreen => ["⏺ Done. All 42 tests pass.", "", Rule, "❯ Try \"refactor the parser\"", Rule, "  ? for shortcuts", "", "", ""];

    [Fact]
    public void EmptyDraftPutsCursorAfterPrompt()
    {
        var merged = TerminalDraft.Merge("", InputScreen);
        Assert.Equal("❯ " + TerminalCursor.Mark + "Try \"refactor the parser\"", merged[3]);
        Assert.Equal(InputScreen.Count, merged.Count);
    }

    [Fact]
    public void EmptyPromptWithoutGapGetsOne() =>
        Assert.Equal("❯ " + TerminalCursor.Mark, TerminalDraft.Merge("", [Rule, "❯", Rule])[1]);

    [Fact]
    public void SingleLineDraftReplacesPlaceholder()
    {
        var merged = TerminalDraft.Merge("fix the build", InputScreen);
        Assert.Equal("❯ fix the build" + TerminalCursor.Mark, merged[3]);
        Assert.Equal(InputScreen.Count, merged.Count);
    }

    [Fact]
    public void MultiLineDraftInsertsRowsAndKeepsHeight()
    {
        var merged = TerminalDraft.Merge("first\nsecond\nthird", InputScreen);
        Assert.Equal(InputScreen.Count, merged.Count);
        Assert.Equal(["❯ first", "  second", "  third" + TerminalCursor.Mark], merged.Skip(3).Take(3));
        // The input box's lower rule and footer move down; blank rows below absorb the growth.
        Assert.Equal(Rule, merged[6]);
        Assert.Equal("  ? for shortcuts", merged[7]);
    }

    [Fact]
    public void PermissionDialogScreenIsUnchanged()
    {
        var screen = PermissionPromptTests.BashDialog();
        Assert.Equal(screen, TerminalDraft.Merge("", screen));
        Assert.Equal(screen, TerminalDraft.Merge("typed", screen));
    }

    [Fact]
    public void ScreenWithoutPromptIsUnchanged()
    {
        string[] screen = ["building…", "", ""];
        Assert.Equal(screen, TerminalDraft.Merge("x", screen));
    }
}
