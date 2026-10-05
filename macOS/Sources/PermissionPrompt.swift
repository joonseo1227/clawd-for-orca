import Foundation

// MARK: - Permission prompts

/// A Claude Code "Do you want to proceed?" dialog read off the terminal screen.
nonisolated struct PermissionPrompt: Equatable, Sendable {
    struct Option: Equatable, Identifiable, Sendable {
        var number: Int
        var label: String
        var id: Int { number }

        /// Short localized label for the standard answers; anything else keeps its own text.
        /// Matching goes by `label`, Claude Code's own English text, never by this title.
        var title: String {
            if label == "Yes" { return String(localized: "Allow", comment: "Permission dialog answer") }
            // "Yes, and switch to auto mode · auto mode handles these prompts for you" allows this once.
            if label.hasPrefix("Yes, and switch to auto mode") {
                return String(localized: "Allow, then auto mode", comment: "Permission dialog answer: Claude Code’s “Yes, and switch to auto mode”")
            }
            if label.hasPrefix("Yes, and") { return String(localized: "Always allow", comment: "Permission dialog answer: allow and don’t ask again") }
            if label.hasPrefix("No") { return String(localized: "Deny", comment: "Permission dialog answer") }
            return label
        }

        /// ⌘1 to ⌘9; options past nine have no single-key shortcut.
        var shortcut: Character? { (1...9).contains(number) ? Character(String(number)) : nil }
    }

    var question: String
    var detail: [String]   // the dialog's title ("Bash command"), then its body lines
    var options: [Option]
    /// Lines of `detail` drawn inside a dashed box: the command, in Bash and PowerShell dialogs.
    var boxed: Range<Int>? = nil

    /// "Bash command", "Edit file", …
    var title: String? { detail.first }
    /// The command or file the dialog asks about: what the dashed box holds, otherwise the first
    /// line under the title.
    var command: String? {
        if let boxed { return detail[boxed].joined(separator: "\n") }
        return detail.dropFirst().first
    }
    /// What Claude says the command is for.
    var explanation: [String] {
        guard let boxed else { return Array(detail.dropFirst(2)) }
        return detail.indices.filter { $0 > 0 && !boxed.contains($0) }.map { detail[$0] }
    }

    /// The hints Claude Code shows above a dialog's description, as it writes them.
    static let tips = ["Tip: auto mode handles these prompts for you — choose \"switch to auto mode\" below"]

    /// Longest command kept from a dashed box; its first lines say the most.
    static let boxedLines = 6

    /// The dialog sits at the bottom of the screen; looking further up would find questions
    /// Claude asked in its replies ("Do you want me to: 1. … 2. …").
    static let searchDepth = 30

    /// Finds the dialog at the bottom of the screen, or nil when none is showing. A dialog has
    /// a question, then options numbered 1, 2, 3… in order with exactly one selected ("❯").
    static func parse(_ screen: [String]) -> PermissionPrompt? {
        let lines = Array(screen.suffix(searchDepth)).map(unframed)
        guard let q = lines.lastIndex(where: { $0.hasPrefix("Do you want") }) else { return nil }

        var options: [Option] = []
        var selected = 0
        for line in lines[(q + 1)...] {
            let isSelected = line.hasPrefix("❯")
            let body = isSelected ? line.dropFirst().trimmingCharacters(in: .whitespaces) : line
            // Blank lines, wrapped labels and the footer ("Esc to cancel") sit between options.
            guard let dot = body.firstIndex(of: "."), let n = Int(body[..<dot]) else { continue }
            guard n == options.count + 1 else { return nil }
            if isSelected { selected += 1 }
            options.append(Option(number: n, label: body[body.index(after: dot)...].trimmingCharacters(in: .whitespaces)))
        }
        guard options.count >= 2, selected == 1 else { return nil }

        // Everything between the dialog's top rule and the question: tool name, description, command.
        // Bash and PowerShell dialogs put the description first and the command between two dashed
        // rules ("╌"); other dialogs list the file or command first, without rules.
        let top = lines[..<q].lastIndex { $0.contains("───") }.map { $0 + 1 } ?? 0
        var detail: [String] = []
        var rules: [Int] = []
        var tip: String?   // the hint read so far, while its wrapped lines may follow
        for line in lines[top..<q] {
            if line.isEmpty { continue }
            // Claude Code's own hints ("Tip: auto mode handles these prompts for you…") aren't the
            // request. In a narrow pane a hint wraps: its next lines go as far as they spell it.
            if line.hasPrefix("Tip:") { tip = line; continue }
            if let read = tip {
                let joined = read + " " + line
                if tips.contains(where: { $0.hasPrefix(joined) }) { tip = joined; continue }
                tip = nil
            }
            if line.hasPrefix("╌") { rules.append(detail.count); continue }
            detail.append(line)
        }
        guard rules.count >= 2, rules[0] > 0, rules[1] > rules[0] else {
            return PermissionPrompt(question: lines[q], detail: Array(detail.suffix(6)), options: options)
        }
        let end = min(rules[1], rules[0] + boxedLines)
        detail.removeSubrange(end..<rules[1])
        return PermissionPrompt(question: lines[q], detail: detail, options: options, boxed: rules[0]..<end)
    }

    /// A screen line without the dialog's side borders and surrounding spaces.
    private static func unframed(_ line: String) -> String {
        line.trimmingCharacters(in: CharacterSet(charactersIn: "│┃ \u{a0}"))
    }
}
