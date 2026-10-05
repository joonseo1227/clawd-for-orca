import Foundation
import Testing
@testable import Clawd

/// Claude's AskUserQuestion input, as Claude Code 2.1 wrote it to the transcript.
nonisolated(unsafe) let colorAndFruits: [String: Any] = ["questions": [
    ["question": "Which color?", "header": "Color", "multiSelect": false,
     "options": [["label": "Red", "description": "Red color"], ["label": "Green", "description": "Green color"],
                 ["label": "Blue", "description": "Blue color"]]],
    ["question": "Which fruits?", "header": "Fruits", "multiSelect": true,
     "options": [["label": "Apple", "description": "Apple fruit"], ["label": "Banana", "description": "Banana fruit"],
                 ["label": "Cherry", "description": "Cherry fruit"]]],
]]

/// The dialog as Orca's `terminal.read --screen` returned it (rules shortened).
let colorScreen = """
 ▐▛███▛█   Claude Code v2.1.289
❯ Use the AskUserQuestion tool right now to ask me two questions
────────────────────────────────────────
←  ☐ Color  ☐ Fruits  ✔ Submit  →
Which color?
❯ 1. Red
     Red color
  2. Green
     Green color
  3. Blue
     Blue color
  4. Type something.
────────────────────────────────────────
  5. Chat about this
Enter to select · Tab/Arrow keys to navigate · Esc to cancel
""".components(separatedBy: "\n")

let fruitScreen = """
────────────────────────────────────────
←  ☒ Color  ☐ Fruits  ✔ Submit  →
Which fruits?
❯ 1. [ ] Apple
         Apple fruit
  2. [ ] Banana
         Banana fruit
  3. [ ] Cherry
         Cherry fruit
  4. [ ] Type something
     Submit
────────────────────────────────────────
  5. Chat about this
Enter to select · Tab/Arrow keys to navigate · Esc to cancel
""".components(separatedBy: "\n")

let reviewScreen = """
────────────────────────────────────────
←  ☒ Color  ☒ Fruits  ✔ Submit  →
Review your answers
 ● Which color?
   → Green
 ● Which fruits?
   → Apple, Cherry
Ready to submit your answers?
❯ 1. Submit answers
  2. Cancel
""".components(separatedBy: "\n")

@Suite struct QuestionTests {
    let question = AgentQuestion.parse(toolInput: colorAndFruits, toolUseId: "toolu_q")!

    @Test func parsesTranscriptInput() {
        #expect(question.source == .terminal(toolUseId: "toolu_q"))
        #expect(question.items.map(\.question) == ["Which color?", "Which fruits?"])
        #expect(question.items.map(\.multiSelect) == [false, true])
        #expect(question.items[1].options.map(\.label) == ["Apple", "Banana", "Cherry"])
        #expect(!question.answersOnClick)
        // A question without choices can't be mirrored; the whole dialog is left to Orca.
        #expect(AgentQuestion.parse(toolInput: ["questions": [["question": "Why?", "options": []]]], toolUseId: "t") == nil)
    }

    @Test func completeness() {
        #expect(!question.complete([.init(picked: [1]), .init()]))
        #expect(question.complete([.init(picked: [1]), .init(picked: [0, 2])]))
        #expect(question.complete([.init(other: "Purple"), .init(other: "Kiwi")]))
        // One choice in a single-choice question: a pick and typed text together are two.
        #expect(!question.complete([.init(picked: [1], other: "Purple"), .init(picked: [0])]))
    }

    @Test func keystrokes() {
        let color = question.items[0], fruits = question.items[1]
        #expect(QuestionDialog.keys(for: color, .init(picked: [1])) == ["2"])
        #expect(QuestionDialog.keys(for: color, .init(other: "Deep\npurple ")) == ["4", "Deep purple", "\r"])
        #expect(QuestionDialog.keys(for: fruits, .init(picked: [2, 0])) == ["1", "3", QuestionDialog.next])
        let down = QuestionDialog.down
        #expect(QuestionDialog.keys(for: fruits, .init(picked: [0], other: "Kiwi")) == ["1", down, down, down, "Kiwi", "\t", "\r"])
        // Boxes already ticked in Orca: only the differences are toggled.
        #expect(QuestionDialog.keys(for: fruits, .init(picked: [0, 1]), ticked: [1, 2, 3]) == ["1", "3", "4", QuestionDialog.next])
        #expect(QuestionDialog.keys(for: fruits, .init(other: "Kiwi"), ticked: [3]) == nil)
    }

    @Test func tickedBoxes() {
        let screen = ["Pick toppings?", "❯ 1. [ ] Ham", "  2. [✔] Olive", "     Olive topping", "  3. [✔] Onion",
                      "  4. [✔] Corn", "     Submit", "Enter to select · ↑/↓ to navigate · Esc to cancel"]
        #expect(QuestionDialog.ticked(screen) == [1, 2, 3])
        #expect(QuestionDialog.ticked(fruitScreen) == [])
    }

    @Test func readsTheDialog() {
        #expect(QuestionDialog.shownQuestion(colorScreen) == "Which color?")
        #expect(QuestionDialog.showing(question.items[0], on: colorScreen))
        #expect(!QuestionDialog.showing(question.items[1], on: colorScreen))
        #expect(QuestionDialog.showing(question.items[1], on: fruitScreen))
        // The review lists the questions too, but asks none of them.
        #expect(!QuestionDialog.showing(question.items[1], on: reviewScreen))
        #expect(QuestionDialog.reviewing(reviewScreen))
        #expect(!QuestionDialog.reviewing(fruitScreen))
        // A permission dialog is not a question dialog.
        let permission = ["Bash command", "  rm -rf build", "Do you want to proceed?", "❯ 1. Yes", "  2. No", "Esc to cancel"]
        #expect(!QuestionDialog.showing(question.items[0], on: permission))
    }

    @Test func wrappedQuestion() {
        let long = "Which of these deployment targets should the release pipeline publish to first?"
        let item = AgentQuestion.Item(id: "q1", question: long, header: nil, multiSelect: false,
                                      options: [.init(id: "1", label: "A", description: nil)])
        let screen = ["☐ Target", "Which of these deployment targets should the release", "pipeline publish to first?",
                      "❯ 1. A", "  2. Type something.", "Enter to select · Esc to cancel"]
        #expect(QuestionDialog.showing(item, on: screen))
    }

    @Test func transcriptReaderTracksTheQuestion() throws {
        let file = try TempTranscript()
        defer { file.remove() }
        try file.append(Entry.user("u1", "Ask me"))
        try file.append(Entry.assistant("a1", [Entry.toolUse("toolu_q", "AskUserQuestion", colorAndFruits)]))
        let reader = TranscriptReader(url: file.url)
        _ = reader.timeline()
        #expect(reader.question == question)
        try file.append(Entry.user("r1", [Entry.toolResult("toolu_q", "The user answered")]))
        _ = reader.timeline()
        #expect(reader.question == nil)
    }
}

// MARK: - Chat sessions

/// An `agentSession.history` page, trimmed from what Orca 1.4 returned.
nonisolated(unsafe) let historyPage: [String: Any] = [
    "fence": 1,
    "items": [
        ["itemId": "old", "revision": 0, "body": ["kind": "message", "role": "user", "blocks": [["type": "text", "text": "Earlier"]]]],
        ["itemId": "orca:m1", "revision": 0, "body": ["kind": "message", "role": "user",
                                                      "blocks": [["type": "text", "text": "Ask me, then write the file"]]]],
        ["itemId": "turn", "revision": 2, "body": ["kind": "turn", "turnId": "t", "state": "running"]],
        ["itemId": "orca:tool1", "revision": 2, "body": ["kind": "tool-call", "name": "Bash", "state": "completed",
                                                         "input": ["command": "echo hi > /tmp/x", "description": "Write test string"],
                                                         "output": ["head": "(Bash completed with no output)"]]],
        ["itemId": "orca:tool2", "revision": 1, "body": ["kind": "tool-call", "name": "Read", "state": "running",
                                                         "input": ["file_path": "/repo/README.md"]]],
        ["itemId": "orca:prompt", "revision": 1, "body": [
            "kind": "question", "question": "Which color?",
            "options": [["id": "q1:choice-1", "label": "Red"], ["id": "q1:choice-2", "label": "Green"]],
            "questions": [["id": "q1", "question": "Which color?", "header": "Color", "multiSelect": false,
                           "options": [["id": "q1:choice-1", "label": "Red", "description": "Choose red"],
                                       ["id": "q1:choice-2", "label": "Green", "description": "Choose green"]]]],
            "resolution": ["state": "pending", "selectedOptionId": NSNull()]]],
        ["itemId": "orca:approval", "revision": 1, "body": [
            "kind": "approval", "title": "Bash", "displayName": "Bash command", "detail": "rm -rf build",
            "description": "Clean the build folder",
            "options": [["id": "allow", "label": "Yes"], ["id": "deny", "label": "No"]],
            "resolution": ["state": "pending"]]],
        ["itemId": "orca:a1", "revision": 2, "body": ["kind": "message", "role": "assistant",
                                                      "blocks": [["type": "text", "text": "Done."]]]],
    ],
]

@Suite struct SessionTests {
    @Test func snapshot() throws {
        let s = try #require(OrcaSession.snapshot(historyPage))
        #expect(s.fence == 1)
        #expect(s.timeline.map(\.id) == ["orca:m1", "orca:tool1", "orca:tool2", "orca:a1"])
        #expect(s.timeline[0].kind == .user)
        #expect(s.timeline[1].kind == .tool(name: "Bash", result: "(Bash completed with no output)", failed: false))
        #expect(s.timeline[1].text == "Write test string")
        #expect(s.timeline[2].kind == .tool(name: "Read", result: nil, failed: false))

        let q = try #require(s.question)
        #expect(q.source == .session(itemId: "orca:prompt", revision: 1))
        #expect(q.items.first?.options.map(\.id) == ["q1:choice-1", "q1:choice-2"])
        #expect(q.answersOnClick)

        let a = try #require(s.approval)
        #expect(a.optionIds == ["allow", "deny"])
        #expect(a.prompt.title == "Bash command")
        #expect(a.prompt.command == "rm -rf build")
        #expect(a.prompt.options.map(\.title) == [String(localized: "Allow"), String(localized: "Deny")])
    }

    @Test func resolvedItemsAreNotPending() throws {
        var page = historyPage
        page["items"] = (historyPage["items"] as! [[String: Any]]).map { item -> [String: Any] in
            guard var body = item["body"] as? [String: Any], body["resolution"] != nil else { return item }
            body["resolution"] = ["state": "resolved"]
            var resolved = item
            resolved["body"] = body
            return resolved
        }
        let s = try #require(OrcaSession.snapshot(page))
        #expect(s.question == nil && s.approval == nil)
    }

    /// Reference values from Orca's own computeAgentSessionPayloadFingerprint.
    @Test func fingerprintMatchesOrca() {
        let body: [String: Any] = ["kind": "message", "role": "user",
                                   "blocks": [["type": "text", "text": "안녕 \"quote\" back\\slash\nline\ttab \u{01} / é 😀"]]]
        #expect(OrcaSession.fingerprint(method: "agentSession.send", sessionId: "1791198875931-95060fb050cf484e9e3c3f7e3b715119",
                                        fields: ["body": body]) == "d2c46d5c828a095feddba0d30f111aba94b80382387228eb9a0484b4fbbe259c")
        let fields: [String: Any] = ["itemId": "orca:x%3Ay", "expectedRevision": 3,
                                     "answers": [["questionId": "q1", "optionIds": ["q1:choice-2"], "other": "Purple"],
                                                 ["questionId": "q2", "optionIds": [String]()]]]
        #expect(OrcaSession.fingerprint(method: "agentSession.respondTo:question", sessionId: "abcdefgh12", fields: fields)
                == "e0528cd12cfe4a83d8e63c0fe3347ea3b178a2a76fd05cfff1a241fff6dafcc0")
    }

    @Test func sessionIds() {
        let chat = OrcaAgent(paneKey: "structured-agent-session-1791198875931-abc:leaf", name: "x", state: "blocked", tool: nil,
                             toolInput: nil, lastMessage: nil, prompt: nil, stateStartedAt: nil, worktreeActive: false)
        #expect(chat.sessionId == "1791198875931-abc")
        #expect(chat.canMessage && !chat.hasTerminal)
        #expect(chat.chatTabId == "agent-session:1791198875931-abc")
        let terminal = OrcaAgent(paneKey: "tab:leaf", name: "x", state: "waiting", tool: "AskUserQuestion",
                                 toolInput: nil, lastMessage: nil, prompt: nil, stateStartedAt: nil, worktreeActive: false)
        #expect(terminal.sessionId == nil && terminal.canMessage && terminal.asksQuestion)
    }
}
