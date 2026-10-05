import AppKit
import SwiftUI

// MARK: - Terminal screen
// Orca's CLI returns the rendered screen as plain text, without colours. This view redraws it
// with Claude Code's own palette by recognising the parts of its interface, on a fixed cell
// grid that matches the size the terminal is fitted to.

/// One cell grid shared by the PTY fit and the drawing, so rows and columns line up exactly.
enum TerminalMetrics {
    static let font = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
    /// For the live terminal: D2Coding draws Hangul at exactly two cells, as a terminal lays it
    /// out; SF Mono has no Hangul and its fallback leaves gaps between syllables.
    static let liveFont = NSFont(name: "D2Coding", size: 13) ?? font
    static let cellWidth = ("M" as NSString).size(withAttributes: [.font: font]).width
    static let lineHeight = ceil(font.ascender - font.descender + font.leading) + 1
    static let padding: CGFloat = 12

    static func grid(for size: CGSize) -> (cols: Int, rows: Int) {
        (max(40, Int((size.width - 2 * padding) / cellWidth)), max(10, Int((size.height - 2 * padding) / lineHeight)))
    }
}

/// Where the input cursor is, marked inside a screen line (the screen text has no cursor).
nonisolated enum TerminalCursor {
    static let mark = "\u{E000}"
}

/// Claude Code's colours, from its light or dark theme to match the appearance. Nonisolated:
/// AppKit may resolve a dynamic colour off the main thread, where a main-actor closure would trap.
nonisolated enum ClaudePalette {
    static let claude = Color(red: 215 / 255, green: 119 / 255, blue: 87 / 255)
    static let success = themed(light: (44, 122, 57), dark: (78, 186, 101))
    static let error = themed(light: (171, 43, 63), dark: (255, 107, 128))
    static let permission = themed(light: (87, 105, 247), dark: (177, 185, 249))
    static let plan = themed(light: (0, 102, 102), dark: (72, 150, 140))
    static let dim = Color.secondary

    private static func themed(light: (Int, Int, Int), dark: (Int, Int, Int)) -> Color {
        func color(_ c: (Int, Int, Int)) -> NSColor {
            NSColor(srgbRed: CGFloat(c.0) / 255, green: CGFloat(c.1) / 255, blue: CGFloat(c.2) / 255, alpha: 1)
        }
        let light = color(light), dark = color(dark)
        return Color(nsColor: NSColor(name: nil) { @Sendable appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
        })
    }
}

struct TerminalLine: Identifiable {
    let id: Int
    let text: String
    let isRule: Bool
    let styled: AttributedString
}

enum TerminalStyler {
    static let spinner = CharacterSet(charactersIn: "✻✢✳✶✽·*")

    static func lines(_ screen: [String], rows: Int?) -> [TerminalLine] {
        var raw = screen.map { $0.replacingOccurrences(of: "\\s+$", with: "", options: .regularExpression) }
        if let rows {
            // Fitted: keep the full grid, top to bottom, so it fills the view exactly.
            if raw.count > rows { raw = Array(raw.suffix(rows)) }
            while raw.count < rows { raw.append("") }
        } else {
            while let last = raw.last, last.isEmpty { raw.removeLast() }
        }
        var dimBlock = false   // inside a tool's output, whose wrapped lines stay dim
        return raw.enumerated().map { i, row in
            let rule = !row.isEmpty && row.allSatisfy { "─╌━ ".contains($0) }
            let t = row.trimmingCharacters(in: .whitespaces)
            // Tool output starts with "⎿" and wraps onto lines indented by five spaces.
            if t.hasPrefix("⎿") { dimBlock = true } else if t.isEmpty || !row.hasPrefix("     ") { dimBlock = false }
            let styled = rule ? AttributedString() : cachedStyle(row, dim: dimBlock)
            return TerminalLine(id: i, text: row, isRule: rule, styled: styled)
        }
    }

    // Most rows don't change between refreshes; style each distinct row once.
    private static var cache: [String: AttributedString] = [:]

    static func cachedStyle(_ row: String, dim: Bool) -> AttributedString {
        let key = (dim ? "1" : "0") + row
        if let hit = cache[key] { return hit }
        if cache.count > 2000 { cache.removeAll(keepingCapacity: true) }
        let styled = style(row, dim: dim)
        cache[key] = styled
        return styled
    }

    static func style(_ row: String, dim: Bool) -> AttributedString {
        let marker = row.range(of: TerminalCursor.mark)
        let clean = row.replacingOccurrences(of: TerminalCursor.mark, with: "")
        var s = AttributedString(clean.isEmpty ? " " : clean)
        let t = clean.trimmingCharacters(in: .whitespaces)
        func colorAll(_ c: Color) { s.foregroundColor = c }
        func colorPrefix(_ n: Int, _ c: Color) {
            let start = s.characters.index(s.startIndex, offsetBy: clean.count - t.count)
            let end = s.characters.index(start, offsetBy: min(n, t.count))
            s[start..<end].foregroundColor = c
        }

        if t.unicodeScalars.contains(where: { "▐▛▜▌▝▘█▀".unicodeScalars.contains($0) }) {
            // The Clawd logo in the welcome header.
            for r in s.characters.indices where "▐▛▜▌▝▘█▀".contains(s.characters[r]) {
                s[r..<s.characters.index(after: r)].foregroundColor = ClaudePalette.claude
            }
        } else if dim || t.hasPrefix("⎿") || t.hasPrefix("…") || t.hasPrefix("(ctrl") || t.hasPrefix("Tip:")
                    || t.contains("esc to interrupt") || t.contains("? for shortcuts") {
            colorAll(ClaudePalette.dim)
        } else if t.hasPrefix("⏺") {
            // Tool calls look like "⏺ Bash(...)"; plain "⏺ text" is Claude talking.
            let isTool = t.range(of: #"^⏺ [A-Za-z_][\w:.-]*\("#, options: .regularExpression) != nil
            colorPrefix(1, t.lowercased().contains("error") ? ClaudePalette.error : (isTool ? ClaudePalette.success : .primary))
        } else if let first = t.unicodeScalars.first, spinner.contains(first), t.contains("…") {
            colorAll(ClaudePalette.claude)                 // live spinner: "✻ Thinking…"
        } else if let first = t.unicodeScalars.first, spinner.contains(first), t.contains(" for ") {
            colorAll(ClaudePalette.dim)                    // finished: "✻ Baked for 3s"
        } else if t.contains("bypass permissions on") {
            colorAll(ClaudePalette.error)
        } else if t.contains("accept edits on") {
            colorAll(ClaudePalette.permission)
        } else if t.contains("plan mode on") {
            colorAll(ClaudePalette.plan)
        } else if t.contains("manual mode on") || t.hasPrefix("⏵") || t.hasPrefix("⏸") {
            colorAll(ClaudePalette.dim)
        } else if t.range(of: #"^❯\s*\d+\."#, options: .regularExpression) != nil {
            colorAll(ClaudePalette.permission)             // highlighted option in a prompt
        } else if t.hasPrefix("Do you want") {
            s.inlinePresentationIntent = .stronglyEmphasized
        } else if clean.range(of: #"^\s*\d+\s+\+"#, options: .regularExpression) != nil {
            colorAll(ClaudePalette.success)                // diff: added line
        } else if clean.range(of: #"^\s*\d+\s+-"#, options: .regularExpression) != nil {
            colorAll(ClaudePalette.error)                  // diff: removed line
        } else if t.hasPrefix("Error") || t.hasPrefix("error:") {
            colorAll(ClaudePalette.error)
        }

        if let marker {
            let offset = row.distance(from: row.startIndex, to: marker.lowerBound)
            if t.hasPrefix("❯") {
                // An empty prompt shows its placeholder dimmed, as Claude Code does.
                let after = s.characters.index(s.startIndex, offsetBy: min(offset, s.characters.count))
                if clean.count > offset { s[after...].foregroundColor = ClaudePalette.dim }
            }
            // Block cursor: inverse video on the character under it (or a trailing space).
            if offset >= s.characters.count { s.append(AttributedString(" ")) }
            let at = s.characters.index(s.startIndex, offsetBy: offset)
            let cell = at..<s.characters.index(after: at)
            s[cell].backgroundColor = Color.primary
            s[cell].foregroundColor = Color(nsColor: .windowBackgroundColor)
        }
        return s
    }

    /// Display width in terminal cells: Hangul and other wide characters take two.
    static func cells(_ text: String) -> Int {
        text.unicodeScalars.reduce(0) { w, u in
            let v = u.value
            let wide = (0x1100...0x115F).contains(v) || (0x2E80...0xA4CF).contains(v) || (0xAC00...0xD7A3).contains(v)
                || (0xF900...0xFAFF).contains(v) || (0xFE30...0xFE4F).contains(v) || (0xFF00...0xFF60).contains(v)
            return w + (wide ? 2 : 1)
        }
    }
}

struct TerminalScreen: View {
    let screen: [String]
    var rows: Int? = nil   // set when the PTY is fitted to this view

    var body: some View {
        let lines = TerminalStyler.lines(screen, rows: rows)
        if rows != nil {
            // Fitted: an exact grid, one fixed-height row per terminal row.
            grid(lines, size: 12)
                .padding(TerminalMetrics.padding)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        } else {
            // Not fitted (the resize was refused): shrink to fit what Orca's own size shows.
            let columns = max(40, lines.filter { !$0.isRule }.map { TerminalStyler.cells($0.text) }.max() ?? 80)
            GeometryReader { geo in
                let byWidth = (geo.size.width - 24) / (CGFloat(columns) * 0.602)
                let byHeight = (geo.size.height - 24) / (CGFloat(max(lines.count, 1)) * 1.3)
                ScrollView(.vertical) {
                    grid(lines, size: max(9, min(12, byWidth, byHeight))).padding(TerminalMetrics.padding)
                }
                .defaultScrollAnchor(.bottom)
            }
        }
    }

    func grid(_ lines: [TerminalLine], size: CGFloat) -> some View {
        let height = size / 12 * TerminalMetrics.lineHeight
        return VStack(alignment: .leading, spacing: 0) {
            ForEach(lines) { line in
                Group {
                    if line.isRule {
                        Rectangle().fill(.separator).frame(height: 1)
                    } else {
                        Text(line.styled).font(.system(size: size, design: .monospaced)).lineLimit(1)
                    }
                }
                .frame(maxWidth: .infinity, minHeight: height, maxHeight: height, alignment: .leading)
            }
        }
    }
}
