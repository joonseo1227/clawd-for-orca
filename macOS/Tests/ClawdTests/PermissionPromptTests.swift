import Testing
@testable import Clawd

/// A Claude Code Bash permission dialog as Orca renders it, with some conversation above it.
func bashDialog(selected: Int = 1) -> [String] {
    func option(_ n: Int, _ label: String) -> String { (n == selected ? "❯ " : "  ") + "\(n). \(label)" }
    return [
        "⏺ I'll run the test suite to check the change.",
        "",
        String(repeating: "─", count: 80),
        " Bash command",
        "",
        "   npm test -- --watch=false",
        "   Run the unit tests",
        "",
        " Do you want to proceed?",
        " " + option(1, "Yes"),
        " " + option(2, "Yes, and don't ask again for npm test commands in /Users/me/project"),
        " " + option(3, "No, and tell Claude what to do differently (esc)"),
        "",
        " Esc to cancel · Tab to amend · ctrl+e to explain",
    ]
}

/// A PowerShell dialog as Claude Code 2.1.288 and later draw it: the description first, then the
/// command between dashed rules.
func powerShellDialog() -> [String] {
    let dashes = String(repeating: "╌", count: 78)
    return [
        "⏺ I'll write the test file.",
        "",
        String(repeating: "─", count: 80),
        " PowerShell command",
        " Write clawd-test to note1.txt",
        " \(dashes) ",
        " Set-Content note1.txt clawd-test",
        " \(dashes) ",
        " Do you want to proceed?",
        " ❯ 1. Yes",
        "   2. Yes, and don’t ask again for: Set-Content *",
        "   3. No",
        "",
        " Esc to cancel · Tab to amend",
    ]
}

@Suite struct PermissionPromptTests {
    @Test func parsesClaudeCodeBashDialog() throws {
        let prompt = try #require(PermissionPrompt.parse(bashDialog()))
        #expect(prompt.question == "Do you want to proceed?")
        #expect(prompt.detail == ["Bash command", "npm test -- --watch=false", "Run the unit tests"])
        #expect(prompt.options.map(\.number) == [1, 2, 3])
        #expect(prompt.options.map(\.title) == ["Allow", "Always allow", "Deny"])
        #expect(prompt.options[1].label == "Yes, and don't ask again for npm test commands in /Users/me/project")
        #expect(prompt.options.map(\.shortcut) == ["1", "2", "3"])
    }

    @Test func parsesDialogWithCommandInDashedBox() throws {
        let prompt = try #require(PermissionPrompt.parse(powerShellDialog()))
        #expect(prompt.title == "PowerShell command")
        #expect(prompt.command == "Set-Content note1.txt clawd-test")
        #expect(prompt.explanation == ["Write clawd-test to note1.txt"])
        #expect(prompt.options.map(\.title) == ["Allow", "Always allow", "Deny"])
    }

    @Test func dashedBoxKeepsMultiLineCommandAndNotesBelowIt() throws {
        let dashes = String(repeating: "╌", count: 60)
        let screen = [
            String(repeating: "─", count: 64),
            " Bash command",
            " │ Build the app and",
            " │ run its tests",
            " \(dashes) ",
            " │ swift build &&",
            " │ swift test",
            " \(dashes) ",
            " Network access to github.com",
            "",
            " Do you want to proceed?",
            " ❯ 1. Yes",
            "   2. No",
        ]
        let prompt = try #require(PermissionPrompt.parse(screen))
        #expect(prompt.command == "swift build &&\nswift test")
        #expect(prompt.explanation == ["Build the app and", "run its tests", "Network access to github.com"])
    }

    @Test func longBoxedCommandKeepsItsFirstLines() throws {
        let dashes = String(repeating: "╌", count: 40)
        var screen = [String(repeating: "─", count: 40), "Bash command", "Generate fixtures", dashes]
        screen += (1...10).map { "line \($0)" }
        screen += [dashes, "Do you want to proceed?", "❯ 1. Yes", "  2. No"]
        let prompt = try #require(PermissionPrompt.parse(screen))
        #expect(prompt.title == "Bash command")
        #expect(prompt.command == (1...PermissionPrompt.boxedLines).map { "line \($0)" }.joined(separator: "\n"))
        #expect(prompt.explanation == ["Generate fixtures"])
    }

    @Test func selectionPositionDoesNotMatter() {
        #expect(PermissionPrompt.parse(bashDialog(selected: 3))?.options.count == 3)
    }

    @Test func parsesBoxedDialog() throws {
        let screen = [
            "╭──────────────────────────────────────╮",
            "│ Edit file                            │",
            "│   Sources/App.swift                  │",
            "│ Do you want to make this edit?       │",
            "│ ❯ 1. Yes                             │",
            "│   2. Yes, allow all edits this session│",
            "│   3. No (esc)                        │",
            "╰──────────────────────────────────────╯",
        ]
        let prompt = try #require(PermissionPrompt.parse(screen))
        #expect(prompt.question == "Do you want to make this edit?")
        #expect(prompt.detail == ["Edit file", "Sources/App.swift"])
        #expect(prompt.options.map(\.label) == ["Yes", "Yes, allow all edits this session", "No (esc)"])
    }

    @Test func claudeReplyWithNumberedQuestionIsNotADialog() {
        // Claude asking in prose: numbered choices but nothing selected, then the input box.
        let screen = [
            "  The migration touches three tables.",
            "",
            "  Do you want me to:",
            "  1. Apply it now",
            "  2. Write a dry-run script first",
            "",
            String(repeating: "─", count: 80),
            "❯ ",
            String(repeating: "─", count: 80),
            "  ? for shortcuts",
        ]
        #expect(PermissionPrompt.parse(screen) == nil)
    }

    @Test func nonContiguousNumberingIsNotADialog() {
        var screen = bashDialog()
        screen[11] = "   4. No, and tell Claude what to do differently (esc)"
        #expect(PermissionPrompt.parse(screen) == nil)
    }

    @Test func singleOptionIsNotADialog() {
        #expect(PermissionPrompt.parse(["Do you want to proceed?", "❯ 1. Yes"]) == nil)
    }

    @Test func dialogBeyondSearchDepthIsIgnored() {
        let screen = bashDialog() + Array(repeating: "", count: PermissionPrompt.searchDepth)
        #expect(PermissionPrompt.parse(screen) == nil)
        // Just inside the window it is still found.
        let near = bashDialog() + Array(repeating: "", count: PermissionPrompt.searchDepth - bashDialog().count)
        #expect(PermissionPrompt.parse(near) != nil)
    }

    @Test func detailKeepsAtMostSixLines() throws {
        var screen = [String(repeating: "─", count: 40)]
        screen += (1...9).map { "line \($0)" }
        screen += ["Do you want to proceed?", "❯ 1. Yes", "  2. No"]
        let prompt = try #require(PermissionPrompt.parse(screen))
        #expect(prompt.detail == (4...9).map { "line \($0)" })
    }

    /// The Bash dialog of Claude Code 2.1.289 in manual mode, as read off Orca's screen: a tip
    /// above the description, and an answer that switches to auto mode.
    @Test func parsesDialogWithTipAndAutoMode() throws {
        let dashes = String(repeating: "╌", count: 98)
        let prompt = try #require(PermissionPrompt.parse([
            "⏺ Writing hello to note1.txt",
            "  ⎿  $ echo hello > note1.txt",
            String(repeating: "─", count: 98),
            " Bash command",
            " Tip: auto mode handles these prompts for you — choose \"switch to auto mode\" below",
            " Write hello to note1.txt",
            dashes,
            " echo hello > note1.txt",
            dashes,
            " Do you want to proceed?",
            " ❯ 1. Yes",
            "   2. Yes, and always allow access to clawd-test/ from this project",
            "   3. Yes, and switch to auto mode · auto mode handles these prompts for you",
            "   4. No",
            " Esc to cancel · Tab to amend",
        ]))
        #expect(prompt.command == "echo hello > note1.txt")
        #expect(prompt.explanation == ["Write hello to note1.txt"])
        #expect(prompt.options.map(\.title) == ["Allow", "Always allow", "Allow, then auto mode", "Deny"])
    }

    /// The same dialog in a pane about 60 columns wide: the tip wraps onto a second line.
    @Test func wrappedTipStaysOutOfTheRequest() throws {
        let dashes = String(repeating: "╌", count: 58)
        let prompt = try #require(PermissionPrompt.parse([
            String(repeating: "─", count: 60),
            " Bash command",
            " Tip: auto mode handles these prompts for you — choose",
            " \"switch to auto mode\" below",
            " Write hello to note1.txt",
            dashes,
            " echo hello > note1.txt",
            dashes,
            " Do you want to proceed?",
            " ❯ 1. Yes",
            "   2. Yes, and always allow access to clawd-test/ from",
            "   this project",
            "   3. Yes, and switch to auto mode · auto mode handles",
            "   these prompts for you",
            "   4. No",
        ]))
        #expect(prompt.detail.first == "Bash command")
        #expect(prompt.command == "echo hello > note1.txt")
        #expect(prompt.explanation == ["Write hello to note1.txt"])
        #expect(prompt.options.map(\.title) == ["Allow", "Always allow", "Allow, then auto mode", "Deny"])

        // Narrower still: three lines of tip, and a description right after it.
        let narrow = try #require(PermissionPrompt.parse([
            String(repeating: "─", count: 32),
            " Bash command",
            " Tip: auto mode handles these",
            " prompts for you — choose",
            " \"switch to auto mode\" below",
            " prompts for you to review",
            String(repeating: "╌", count: 30),
            " ls",
            String(repeating: "╌", count: 30),
            " Do you want to proceed?",
            " ❯ 1. Yes",
            "   2. No",
        ]))
        #expect(narrow.explanation == ["prompts for you to review"])
    }

    @Test func optionTitlesAndShortcuts() {
        let custom = PermissionPrompt.Option(number: 10, label: "Something else")
        #expect(custom.title == "Something else")
        #expect(custom.shortcut == nil)
    }
}

@Suite struct TerminalDraftTests {
    let rule = String(repeating: "─", count: 60)

    var inputScreen: [String] {
        ["⏺ Done. All 42 tests pass.", "", rule, "❯ Try \"refactor the parser\"", rule, "  ? for shortcuts", "", "", ""]
    }

    @Test func emptyDraftPutsCursorAfterPrompt() {
        let merged = TerminalDraft.merge("", into: inputScreen)
        #expect(merged[3] == "❯ " + TerminalCursor.mark + "Try \"refactor the parser\"")
        #expect(merged.count == inputScreen.count)
    }

    @Test func emptyPromptWithoutGapGetsOne() {
        let merged = TerminalDraft.merge("", into: [rule, "❯", rule])
        #expect(merged[1] == "❯ " + TerminalCursor.mark)
    }

    @Test func singleLineDraftReplacesPlaceholder() {
        let merged = TerminalDraft.merge("fix the build", into: inputScreen)
        #expect(merged[3] == "❯ fix the build" + TerminalCursor.mark)
        #expect(merged.count == inputScreen.count)
    }

    @Test func multiLineDraftInsertsRowsAndKeepsHeight() {
        let merged = TerminalDraft.merge("first\nsecond\nthird", into: inputScreen)
        #expect(merged.count == inputScreen.count)
        #expect(Array(merged[3...5]) == ["❯ first", "  second", "  third" + TerminalCursor.mark])
        // The input box's lower rule and footer move down; blank rows below absorb the growth.
        #expect(merged[6] == rule)
        #expect(merged[7] == "  ? for shortcuts")
    }

    @Test func permissionDialogScreenIsUnchanged() {
        let screen = bashDialog()
        #expect(TerminalDraft.merge("", into: screen) == screen)
        #expect(TerminalDraft.merge("typed", into: screen) == screen)
    }

    @Test func screenWithoutPromptIsUnchanged() {
        let screen = ["building…", "", ""]
        #expect(TerminalDraft.merge("x", into: screen) == screen)
    }
}
