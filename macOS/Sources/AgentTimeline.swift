import SwiftUI

// MARK: - Timeline
// The selected agent's current turn, step by step: the prompt, what Claude says, and the tool
// calls and thinking in between. Runs of steps fold into one row, the way Claude's own apps do,
// so the replies stay readable; the newest step stays visible while it runs.

/// What the timeline lays out: a single item, or a run of consecutive steps.
enum TimelineEntry: Identifiable, Equatable {
    case item(TimelineItem)
    case steps([TimelineItem])

    var id: String {
        switch self {
        case .item(let item): item.id
        case .steps(let steps): "steps-" + (steps.first?.id ?? "")
        }
    }

    /// Groups tool calls and thinking between messages. Thinking without a summary says
    /// nothing, so it only shows while it is the latest thing happening.
    static func build(_ items: [TimelineItem], live: Bool) -> [TimelineEntry] {
        var entries: [TimelineEntry] = []
        var run: [TimelineItem] = []
        func flush() {
            if run.count == 1 { entries.append(.item(run[0])) } else if !run.isEmpty { entries.append(.steps(run)) }
            run = []
        }
        for (i, item) in items.enumerated() {
            switch item.kind {
            case .thinking where item.text.isEmpty && !(live && i == items.count - 1):
                continue
            case .thinking, .tool:
                run.append(item)
            case .user, .text:
                flush()
                entries.append(.item(item))
            }
        }
        flush()
        return entries
    }
}

struct AgentTimeline<Footer: View>: View {
    let items: [TimelineItem]
    /// The agent shown; switching jumps to the bottom of the new conversation.
    let agent: String?
    /// The agent is still working, so the last step may be in progress.
    let live: Bool
    @ViewBuilder let footer: Footer

    @State private var atBottom = true   // follow new steps only while the reader is at the bottom
    @State private var expanded: Set<String> = []

    var body: some View {
        let entries = TimelineEntry.build(items, live: live)
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 10) {
                    ForEach(entries) { entry in
                        row(entry, isLast: entry.id == entries.last?.id).id(entry.id)
                    }
                    footer
                    Color.clear.frame(height: 1).id(Self.bottom)
                }
                .padding(20)
            }
            .modifier(SoftScrollEdge())
            .onScrollGeometryChange(for: Bool.self) { geo in
                geo.contentOffset.y + geo.containerSize.height >= geo.contentSize.height - 40
            } action: { _, bottom in
                atBottom = bottom
            }
            .onAppear { proxy.scrollTo(Self.bottom, anchor: .bottom) }
            .onChange(of: agent) { _, _ in
                atBottom = true
                expanded = []
                proxy.scrollTo(Self.bottom, anchor: .bottom)
            }
            .onChange(of: items) { _, _ in
                // Scrolled up to read something: leave the view where it is.
                guard atBottom else { return }
                withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo(Self.bottom, anchor: .bottom) }
            }
        }
    }

    private static var bottom: String { "bottom" }

    @ViewBuilder
    private func row(_ entry: TimelineEntry, isLast: Bool) -> some View {
        switch entry {
        case .item(let item):
            switch item.kind {
            case .user: UserBubble(text: item.text).padding(.vertical, 4)
            case .text: MarkdownView(item.text).padding(.vertical, 4)
            case .thinking, .tool: StepRow(item: item, running: live && isLast)
            }
        case .steps(let steps):
            DisclosureGroup(isExpanded: binding(entry.id)) {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(steps) { StepRow(item: $0, running: live && isLast && $0.id == steps.last?.id) }
                }
                .padding(.top, 6)
            } label: {
                if live && isLast, let latest = steps.last {
                    // Working: the step in progress, with how many came before it.
                    HStack {
                        StepLabel(item: latest)
                        Text(verbatim: "+\(steps.count - 1)").font(.callout.monospacedDigit()).foregroundStyle(.tertiary)
                        Spacer(minLength: 0)
                        ProgressView().controlSize(.small)
                    }
                } else {
                    StepsSummary(steps: steps)
                }
            }
        }
    }

    private func binding(_ id: String) -> Binding<Bool> {
        Binding(get: { expanded.contains(id) },
                set: { if $0 { expanded.insert(id) } else { expanded.remove(id) } })
    }
}

/// "6 steps  Bash 3, Read 2, Thinking 1"
private struct StepsSummary: View {
    let steps: [TimelineItem]

    var body: some View {
        var tools: [(name: String, count: Int)] = []
        var thoughts = 0
        var failed = false
        for step in steps {
            switch step.kind {
            case .tool(let name, let result, let didFail):
                if let i = tools.firstIndex(where: { $0.name == name }) { tools[i].count += 1 } else { tools.append((name, 1)) }
                failed = failed || (didFail && result != nil)
            case .thinking: thoughts += 1
            default: break
            }
        }
        var parts = tools.prefix(3).map { "\($0.name) \($0.count)" }
        if tools.count > 3 { parts.append(String(localized: "+\(tools.count - 3) more", comment: "More tools in a step summary, after the first three")) }
        if thoughts > 0 { parts.append(String(localized: "Thinking \(thoughts)", comment: "Number of thinking steps in a step summary")) }
        return Label {
            Text("\(steps.count) steps").bold() + Text(verbatim: "  " + parts.joined(separator: ", ")).foregroundStyle(.secondary)
        } icon: {
            Image(systemName: failed ? "exclamationmark.circle" : "checklist")
                .foregroundStyle(failed ? AnyShapeStyle(.red) : AnyShapeStyle(.secondary))
        }
        .lineLimit(1)
    }
}

/// One tool call (expandable to its output) or one stretch of thinking.
private struct StepRow: View {
    let item: TimelineItem
    let running: Bool

    var body: some View {
        switch item.kind {
        case .thinking where item.text.isEmpty:
            HStack {
                StepLabel(item: item)
                if running { ProgressView().controlSize(.small) }
            }
        case .thinking:
            DisclosureGroup {
                Text(item.text).foregroundStyle(.secondary).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } label: {
                StepLabel(item: item)
            }
        case .tool(_, let result, let failed):
            DisclosureGroup {
                Text(result?.isEmpty == false ? result! : String(localized: "No output", comment: "A tool call that printed nothing"))
                    .font(.callout.monospaced())
                    .foregroundStyle(failed ? AnyShapeStyle(.red) : AnyShapeStyle(.secondary))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } label: {
                HStack {
                    StepLabel(item: item)
                    Spacer(minLength: 0)
                    if result == nil { ProgressView().controlSize(.small) }   // still running
                }
            }
            .disabled(result == nil)
        default:
            EmptyView()
        }
    }
}

/// Icon, tool name and what it is doing, on one line.
struct StepLabel: View {
    let item: TimelineItem

    var body: some View {
        switch item.kind {
        case .thinking:
            Label(item.text.isEmpty ? "Thinking…" as LocalizedStringKey : "Thinking", systemImage: "brain").foregroundStyle(.secondary)
        case .tool(let name, _, let failed):
            Label {
                Text(name).bold() + Text(verbatim: "  ") + Text(item.text).foregroundStyle(.secondary)
            } icon: {
                Image(systemName: Self.symbol(for: name))
                    .foregroundStyle(failed ? AnyShapeStyle(.red) : AnyShapeStyle(.secondary))
            }
            .lineLimit(1)
            .truncationMode(.middle)
        default:
            EmptyView()
        }
    }

    static func symbol(for tool: String) -> String {
        switch tool {
        case "Bash": "terminal"
        case "Read": "doc.text"
        case "Edit", "MultiEdit", "Write", "NotebookEdit": "pencil"
        case "Grep", "Glob", "WebSearch", "ToolSearch": "magnifyingglass"
        case "WebFetch": "globe"
        case "Agent", "Task": "person.2"
        case "TodoWrite": "checklist"
        default: tool.hasPrefix("mcp__") ? "puzzlepiece.extension" : "wrench.and.screwdriver"
        }
    }
}

/// The user's own message, on the right in the accent colour. There is no system chat bubble,
/// so this is the one deliberately custom shape.
struct UserBubble: View {
    let text: String

    var body: some View {
        let rendered = (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(text)
        HStack {
            Spacer(minLength: 80)
            Text(rendered)
                .textSelection(.enabled)
                .foregroundStyle(.white)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(.tint, in: .rect(cornerRadius: 16, style: .continuous))
        }
    }
}
