import Foundation

// MARK: - Agent questions
// Claude's AskUserQuestion: up to four questions, each with a few choices, single or multiple
// pick, and room for an answer of one's own. A terminal session draws it as a dialog that only
// takes keystrokes, so the questions come from the transcript (the dialog's text is clipped and
// wrapped) and the answers go back as the keys a person would press. A session in Orca's chat
// carries them in its history and takes the answers through Orca's API.

nonisolated struct AgentQuestion: Equatable, Sendable {
    struct Option: Equatable, Sendable {
        let id: String
        let label: String
        let description: String?
    }
    struct Item: Equatable, Sendable {
        let id: String
        let question: String
        let header: String?
        let multiSelect: Bool
        let options: [Option]
    }
    /// One question's answer: the picked options by index, and text of one's own.
    struct Answer: Equatable, Sendable {
        var picked: Set<Int> = []
        var other = ""

        var otherText: String {
            other.split(whereSeparator: \.isNewline).joined(separator: " ").trimmingCharacters(in: .whitespaces)
        }
        var isEmpty: Bool { picked.isEmpty && otherText.isEmpty }
    }
    enum Source: Equatable, Sendable {
        case terminal(toolUseId: String)
        /// The history item and the revision it was read at; Orca refuses an answer to a stale one.
        case session(itemId: String, revision: Int)
    }

    let source: Source
    let items: [Item]

    /// One question with one choice: a click on a choice answers it.
    var answersOnClick: Bool { items.count == 1 && !items[0].multiSelect }

    /// Every question has an answer, and a single-choice one has at most one.
    func complete(_ answers: [Answer]) -> Bool {
        answers.count == items.count && zip(items, answers).allSatisfy { item, a in
            !a.isEmpty && (item.multiSelect || a.picked.count + (a.otherText.isEmpty ? 0 : 1) == 1)
        }
    }

    /// The AskUserQuestion input Claude Code writes to its transcript.
    static func parse(toolInput input: [String: Any], toolUseId: String) -> AgentQuestion? {
        let raw = input["questions"] as? [[String: Any]] ?? []
        let items = raw.enumerated().compactMap { i, q -> Item? in
            guard let text = q["question"] as? String, !text.isEmpty else { return nil }
            let options = (q["options"] as? [[String: Any]] ?? []).enumerated().compactMap { j, o -> Option? in
                guard let label = o["label"] as? String else { return nil }
                return Option(id: "\(j + 1)", label: label, description: o["description"] as? String)
            }
            guard !options.isEmpty else { return nil }
            return Item(id: "q\(i + 1)", question: text, header: q["header"] as? String,
                        multiSelect: q["multiSelect"] as? Bool ?? false, options: options)
        }
        // A dialog Clawd can't mirror one to one is left to Orca.
        guard !items.isEmpty, items.count == raw.count else { return nil }
        return AgentQuestion(source: .terminal(toolUseId: toolUseId), items: items)
    }

    /// A pending `question` item from a chat session's history.
    static func parse(sessionItem item: [String: Any]) -> AgentQuestion? {
        guard let body = item["body"] as? [String: Any], body["kind"] as? String == "question",
              (body["resolution"] as? [String: Any])?["state"] as? String == "pending",
              let itemId = item["itemId"] as? String, let revision = item["revision"] as? Int else { return nil }
        func options(_ raw: Any?) -> [Option] {
            (raw as? [[String: Any]] ?? []).compactMap { o in
                guard let id = o["id"] as? String, let label = o["label"] as? String else { return nil }
                return Option(id: id, label: label, description: o["description"] as? String)
            }
        }
        var items = (body["questions"] as? [[String: Any]] ?? []).compactMap { q -> Item? in
            guard let id = q["id"] as? String, let text = q["question"] as? String else { return nil }
            return Item(id: id, question: text, header: q["header"] as? String,
                        multiSelect: q["multiSelect"] as? Bool ?? false, options: options(q["options"]))
        }
        // Older hosts send a single question at the top level.
        if items.isEmpty, let text = body["question"] as? String {
            items = [Item(id: body["freeTextQuestionId"] as? String ?? "q1", question: text, header: nil,
                          multiSelect: false, options: options(body["options"]))]
        }
        guard !items.isEmpty else { return nil }
        return AgentQuestion(source: .session(itemId: itemId, revision: revision), items: items)
    }
}

// MARK: - Terminal dialog

/// Claude Code's question dialog, as keystrokes and as the text it shows. Measured against
/// Claude Code 2.1: a digit picks a single-choice option and moves on; in a multiple-choice
/// question it toggles the option's box and leaves the cursor on the first row, so reaching the
/// "Type something" row takes ↓ once per option. → and ← move between questions (→ from the
/// last one to the review), Tab steps out of the text field, and the review is confirmed with 1.
nonisolated enum QuestionDialog {
    static let down = "\u{1b}[B"
    static let next = "\u{1b}[C"
    static let previous = "\u{1b}[D"

    /// The keystrokes answering one question, each sent on its own. `ticked` holds the boxes
    /// already ticked in a multiple-choice question (the "Type something" row is index
    /// `options.count`), which a digit would untick. Nil when the dialog holds text of its own
    /// that keystrokes can't safely replace.
    static func keys(for item: AgentQuestion.Item, _ answer: AgentQuestion.Answer, ticked: Set<Int> = []) -> [String]? {
        let other = answer.otherText
        let typeRow = item.options.count
        if !item.multiSelect {
            if !other.isEmpty { return ["\(typeRow + 1)", other, "\r"] }
            return answer.picked.min().map { ["\($0 + 1)"] } ?? []
        }
        var keys = (0..<item.options.count).filter { answer.picked.contains($0) != ticked.contains($0) }.map { "\($0 + 1)" }
        if other.isEmpty {
            if ticked.contains(typeRow) { keys.append("\(typeRow + 1)") }
            keys.append(next)
        } else {
            if ticked.contains(typeRow) { return nil }
            keys += Array(repeating: down, count: item.options.count) + [other, "\t", "\r"]
        }
        return keys
    }

    /// The ticked boxes of a multiple-choice question on screen, by option index.
    static func ticked(_ screen: [String]) -> Set<Int> {
        var result: Set<Int> = []
        for line in screen.suffix(PermissionPrompt.searchDepth) {
            let t = line.trimmingCharacters(in: .whitespaces)
            guard let m = t.range(of: #"^(❯\s*)?\d+\.\s+\[✔\]"#, options: .regularExpression),
                  let n = Int(t[m].filter(\.isNumber)) else { continue }
            result.insert(n - 1)
        }
        return result
    }

    /// The question the dialog is asking right now: the lines above its first option.
    static func shownQuestion(_ screen: [String]) -> String? {
        let lines = Array(screen.suffix(PermissionPrompt.searchDepth)).map { $0.trimmingCharacters(in: .whitespaces) }
        guard lines.contains(where: { $0.contains("Esc to cancel") }),
              let first = lines.firstIndex(where: { $0.range(of: #"^(❯\s*)?1\.\s"#, options: .regularExpression) != nil }) else { return nil }
        var text: [String] = []
        var i = first - 1
        while i >= 0 {
            let line = lines[i]
            if line.isEmpty || line.contains("───") || line.contains("☐") || line.contains("☒") || line.contains("✔") { break }
            text.insert(line, at: 0)
            i -= 1
        }
        let joined = text.joined(separator: " ")
        return joined.isEmpty ? nil : joined
    }

    /// The dialog is on `item`. The screen may cut a long question short; what it shows has to
    /// open the question.
    static func showing(_ item: AgentQuestion.Item, on screen: [String]) -> Bool {
        guard let shown = shownQuestion(screen).flatMap({ snippet($0, 10_000) }),
              let wanted = snippet(item.question, 10_000) else { return false }
        return shown == wanted || (shown.count >= 12 && wanted.hasPrefix(shown.trimmingCharacters(in: CharacterSet(charactersIn: "…"))))
    }

    /// The "Review your answers" step that several questions (or a multiple-choice one) end on.
    static func reviewing(_ screen: [String]) -> Bool {
        screen.suffix(PermissionPrompt.searchDepth).contains { $0.contains("Ready to submit your answers?") }
    }
}
