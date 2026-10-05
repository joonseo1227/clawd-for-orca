import SwiftUI

// MARK: - Chat model
// What the chat popover shows, kept by the app and observed by the views.

enum RowKind: Int, Comparable {
    case permission, question, working, finished, resting
    static func < (a: RowKind, b: RowKind) -> Bool { a.rawValue < b.rawValue }

    var tint: Color {
        switch self {
        case .permission, .question: return .orange
        case .working: return .blue
        case .finished: return .green
        case .resting: return .secondary
        }
    }
}

/// A one-line result under the composer: what was sent, or why it wasn't.
struct ChatNotice: Equatable {
    enum Tone { case success, info, failure }
    var tone: Tone
    var text: String
    let id = UUID()   // the same text twice in a row still restarts the hide timer

    static func success(_ text: String) -> ChatNotice { ChatNotice(tone: .success, text: text) }
    static func info(_ text: String) -> ChatNotice { ChatNotice(tone: .info, text: text) }
    static func failure(_ text: String) -> ChatNotice { ChatNotice(tone: .failure, text: text) }

    /// Confirmations step aside after a moment; failures stay until something else happens.
    var lingers: Bool { tone == .failure }
}

struct ChatRow: Identifiable, Equatable {
    var agent: OrcaAgent
    var kind: RowKind
    var title: String?     // terminal tab title, tells apart agents sharing a worktree
    var status: String
    var id: String { agent.paneKey }
}

@Observable
final class ChatModel {
    enum Mode { case chat, terminal }
    enum Screen: Equatable { case loading, failed, lines([String]) }
    enum Connection { case connected, notRunning, off, notInstalled }

    var mode: Mode = .chat { didSet { if mode != oldValue { onLayoutChange?() } } }
    var viewport: CGSize = .zero { didSet { if viewport != oldValue { onLayoutChange?() } } }   // terminal area, drives the PTY fit
    var selected: String? { didSet { if selected != oldValue { onSelect?(selected) } } }
    var timeline: [TimelineItem] = []   // live steps from the Claude Code transcript
    var timelineReady = false           // the first read for the selected agent came back
    var fittedRows: Int?                // rows the PTY was resized to, nil when not fitted
    var live = false                    // paired with Orca: real terminal stream + emulator
    var liveStatus: String?             // connecting / error text over the live terminal
    var composing = ""                  // IME text not yet committed to the terminal
    var screen: Screen = .loading
    var rows: [ChatRow] = []
    var prompt: PermissionPrompt?
    var question: AgentQuestion? { didSet { if question != oldValue { answers = question.map { Array(repeating: .init(), count: $0.items.count) } ?? [] } } }
    var answers: [AgentQuestion.Answer] = []   // choices made so far in `question`
    var drafts: [String: String] = [:]  // unsent text kept per agent
    var notice: ChatNotice?
    var busyPanes: Set<String> = []      // agents with a send or screen read in flight
    var connection: Connection = .connected
    var showResting = false

    /// Hooks for the app: the selected agent changed; the terminal view's mode or size changed.
    @ObservationIgnored var onSelect: ((String?) -> Void)?
    @ObservationIgnored var onLayoutChange: (() -> Void)?

    var current: ChatRow? { rows.first { $0.id == selected } ?? rows.first }

    /// The selected agent's question card is up.
    var asking: Bool { question != nil && current?.kind == .question }

    /// The terminal view is showing: chosen, and the selected agent has a terminal.
    var showsTerminal: Bool { mode == .terminal && current?.agent.hasTerminal == true }

    /// The selected agent has a request in flight.
    var busy: Bool { current.map { busyPanes.contains($0.id) } ?? false }

    var draft: String {
        get { current.flatMap { drafts[$0.id] } ?? "" }
        set { if let id = current?.id { drafts[id] = newValue.isEmpty ? nil : newValue } }
    }

    /// Arrow keys walk the visible rows; resting ones count only when their section is open.
    func move(_ step: Int) {
        let visible = showResting ? rows : rows.filter { $0.kind != .resting }
        guard !visible.isEmpty else { return }
        let i = visible.firstIndex { $0.id == current?.id } ?? 0
        selected = visible[max(0, min(visible.count - 1, i + step))].id
    }
}

/// What the panel asks the app to do.
struct ChatActions {
    var send: (OrcaAgent, String) -> Void
    var answer: (OrcaAgent, Int) -> Void
    var answerQuestion: (OrcaAgent, AgentQuestion, [AgentQuestion.Answer]) -> Void
    var key: (OrcaAgent, String) -> Void      // raw keystrokes for the terminal view
    var open: (OrcaAgent) -> Void
    var close: () -> Void
    var launchOrca: () -> Void
    var settings: () -> Void
}
