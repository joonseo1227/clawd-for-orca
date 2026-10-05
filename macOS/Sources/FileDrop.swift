import Foundation

// MARK: - File drops
// Files dropped on Clawd, the chat or the terminal become their paths, escaped the way
// Terminal.app writes a dropped file, so Claude Code reads them as paths (and images as images).

nonisolated enum FileDrop {
    /// Characters a shell would read specially; each gets a backslash.
    private static let special = Set(" !\"#$&'()*,;<=>?[\\]^`{|}~")

    static func escape(_ path: String) -> String {
        var out = ""
        for ch in path {
            if special.contains(ch) { out.append("\\") }
            out.append(ch)
        }
        return out
    }

    /// The dropped files' paths, separated by spaces.
    static func text(for urls: [URL]) -> String {
        urls.filter(\.isFileURL).map { escape($0.path) }.joined(separator: " ")
    }

    /// `text` added to a draft, with a space between them when the draft doesn't end in one.
    static func append(_ text: String, to draft: String) -> String {
        guard !text.isEmpty else { return draft }
        if draft.isEmpty || draft.last?.isWhitespace == true { return draft + text + " " }
        return draft + " " + text + " "
    }

    /// Typed into a terminal as a paste, so the paths arrive whole and nothing is submitted.
    static func paste(_ text: String) -> String {
        "\u{1b}[200~" + text + " \u{1b}[201~"
    }
}
