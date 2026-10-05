import SwiftUI

// MARK: - Markdown
// Claude answers in Markdown. SwiftUI's Text only renders inline Markdown, so lists, code
// blocks, headings and tables are split into blocks here and laid out one by one.

nonisolated enum MarkdownBlock: Equatable, Sendable {
    case heading(level: Int, text: String)
    case paragraph(String)
    case listItem(marker: String, indent: Int, text: String)
    case code(language: String, text: String)
    case quote(String)
    case table(header: [String], rows: [[String]])
    case rule

    static func parse(_ source: String) -> [MarkdownBlock] {
        var blocks: [MarkdownBlock] = []
        var paragraph: [String] = []
        let lines = source.components(separatedBy: "\n")
        var i = 0

        func flush() {
            if !paragraph.isEmpty { blocks.append(.paragraph(paragraph.joined(separator: "\n"))) }
            paragraph = []
        }
        func cells(_ line: String) -> [String] {
            var t = line.trimmingCharacters(in: .whitespaces)
            if t.hasPrefix("|") { t.removeFirst() }
            if t.hasSuffix("|") { t.removeLast() }
            return t.components(separatedBy: "|").map { $0.trimmingCharacters(in: .whitespaces) }
        }

        while i < lines.count {
            let line = lines[i]
            let t = line.trimmingCharacters(in: .whitespaces)

            if t.hasPrefix("```") {
                flush()
                let language = String(t.dropFirst(3))
                var code: [String] = []
                i += 1
                while i < lines.count, !lines[i].trimmingCharacters(in: .whitespaces).hasPrefix("```") {
                    code.append(lines[i]); i += 1
                }
                blocks.append(.code(language: language, text: code.joined(separator: "\n")))
                i += 1
                continue
            }
            if t.isEmpty { flush(); i += 1; continue }
            if t == "---" || t == "***" { flush(); blocks.append(.rule); i += 1; continue }
            if let hashes = t.firstIndex(where: { $0 != "#" }), t.hasPrefix("#"), t[hashes] == " " {
                flush()
                blocks.append(.heading(level: t.distance(from: t.startIndex, to: hashes), text: String(t[hashes...]).trimmingCharacters(in: .whitespaces)))
                i += 1; continue
            }
            // A table: a pipe row followed by a |---|---| separator row.
            if t.hasPrefix("|"), i + 1 < lines.count,
               lines[i + 1].trimmingCharacters(in: .whitespaces).range(of: #"^\|?\s*:?-{2,}"#, options: .regularExpression) != nil {
                flush()
                let header = cells(t)
                var rows: [[String]] = []
                i += 2
                while i < lines.count, lines[i].trimmingCharacters(in: .whitespaces).hasPrefix("|") {
                    rows.append(cells(lines[i])); i += 1
                }
                blocks.append(.table(header: header, rows: rows))
                continue
            }
            if t.hasPrefix("> ") { flush(); blocks.append(.quote(String(t.dropFirst(2)))); i += 1; continue }
            let indent = line.prefix { $0 == " " }.count / 2
            if let r = t.range(of: #"^([-*+]|\d+[.)])\s+"#, options: .regularExpression) {
                flush()
                let raw = t[r].trimmingCharacters(in: .whitespaces)
                let marker = raw.first.map { "-*+".contains($0) } == true ? "•" : raw
                blocks.append(.listItem(marker: marker, indent: indent, text: String(t[r.upperBound...])))
                i += 1; continue
            }
            paragraph.append(line)
            i += 1
        }
        flush()
        return blocks
    }
}

struct MarkdownView: View {
    let blocks: [MarkdownBlock]

    init(_ source: String) { blocks = MarkdownBlock.parse(source) }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(Array(blocks.enumerated()), id: \.offset) { _, block in
                view(for: block)
            }
        }
        .textSelection(.enabled)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    func inline(_ text: String) -> Text {
        let options = AttributedString.MarkdownParsingOptions(interpretedSyntax: .inlineOnlyPreservingWhitespace)
        return Text((try? AttributedString(markdown: text, options: options)) ?? AttributedString(text))
    }

    @ViewBuilder
    func view(for block: MarkdownBlock) -> some View {
        switch block {
        case .heading(let level, let text):
            inline(text)
                .font(level <= 1 ? .title2.bold() : level == 2 ? .title3.bold() : .headline)
                .padding(.top, 4)
        case .paragraph(let text):
            inline(text).lineSpacing(3)
        case .listItem(let marker, let indent, let text):
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(marker).monospacedDigit().foregroundStyle(.secondary)
                    .frame(minWidth: 14, alignment: .trailing)
                inline(text).lineSpacing(3)
            }
            .padding(.leading, CGFloat(indent) * 18)
        case .code(_, let text):
            GroupBox {
                ScrollView(.horizontal, showsIndicators: false) {
                    Text(text).font(.callout.monospaced()).fixedSize().padding(4)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        case .quote(let text):
            HStack(spacing: 10) {
                Divider()
                inline(text).foregroundStyle(.secondary)
            }
            .fixedSize(horizontal: false, vertical: true)
        case .table(let header, let rows):
            GroupBox {
                ScrollView(.horizontal, showsIndicators: false) {
                    Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 8) {
                        GridRow { ForEach(Array(header.enumerated()), id: \.offset) { inline($0.element).bold() } }
                        Divider()
                        ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                            GridRow { ForEach(Array(row.enumerated()), id: \.offset) { inline($0.element) } }
                        }
                    }
                    .padding(4)
                }
            }
        case .rule:
            Divider()
        }
    }
}
