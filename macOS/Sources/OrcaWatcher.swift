import AppKit

// MARK: - Orca watcher
// Follows the agents running in Orca and acts on their terminals. Everything goes through
// Orca's runtime socket; the orca CLI is only a fallback for when the socket can't be reached
// (an Orca build without it). All callbacks arrive on the main thread.

final class OrcaWatcher {
    /// Looked up at launch and again while missing, so installing Orca later just works.
    private(set) var installation = OrcaInstallation.locate()
    var enabled = true
    private(set) var agents: [OrcaAgent] = []
    /// False while Orca isn't running (its socket is gone).
    private(set) var running = true
    private var hasBaseline = false
    private var polling = false
    private var nextPoll: TimeInterval = 0
    private var nextLocate: TimeInterval = 0

    /// Called with (previous, current) after every successful poll past the first.
    var onChange: (([OrcaAgent], [OrcaAgent]) -> Void)?
    /// Called when Orca starts or quits.
    var onRunningChange: ((Bool) -> Void)?

    var cli: String? { installation?.cli }
    var available: Bool { installation != nil || TestHooks.fakeOrca != nil }
    var working: [OrcaAgent] { agents.filter { $0.state == "working" } }
    var waiting: [OrcaAgent] { agents.filter(\.needsYou) }

    /// How often to ask Orca for agent states; slower while Orca is closed.
    static let pollInterval: TimeInterval = 2
    static let idlePollInterval: TimeInterval = 10

    private var client: OrcaClient { OrcaClient(cli: cli) }

    func poll() {
        let now = ProcessInfo.processInfo.systemUptime
        if installation == nil, now > nextLocate {
            nextLocate = now + 30
            installation = OrcaInstallation.locate()
        }
        guard enabled, available, !polling, now >= nextPoll else { return }
        polling = true
        DispatchQueue.global(qos: .utility).async {
            let data: Data?
            if let fake = TestHooks.fakeOrca {
                data = try? Data(contentsOf: URL(fileURLWithPath: fake))
            } else {
                // The socket only: starting the CLI every two seconds while Orca is closed
                // would cost a Node launch each time for nothing.
                data = OrcaRuntime.call("worktree.ps", [:])
            }
            let parsed = OrcaAgent.parse(data)
            onMain {
                self.polling = false
                guard self.enabled else { return }
                let isRunning = parsed != nil
                self.nextPoll = ProcessInfo.processInfo.systemUptime + (isRunning ? Self.pollInterval : Self.idlePollInterval)
                if isRunning != self.running {
                    self.running = isRunning
                    self.onRunningChange?(isRunning)
                }
                let next = parsed ?? []
                let old = self.agents
                self.agents = next
                if self.hasBaseline, old != next { self.onChange?(old, next) }
                self.hasBaseline = true
            }
        }
    }

    /// Ask again right away (after an action that changes an agent's state).
    func pollSoon() { nextPoll = 0 }

    func stop() {
        enabled = false
        agents = []
        hasBaseline = false
    }

    /// Opens Orca, or brings it to the front.
    func launch() {
        guard let app = installation?.app else { return }
        NSWorkspace.shared.openApplication(at: app, configuration: NSWorkspace.OpenConfiguration())
    }

    /// Brings the agent's terminal to the front in Orca.
    func focus(_ agent: OrcaAgent) {
        let client = client
        DispatchQueue.global(qos: .userInitiated).async { client.focus(agent) }
        launch()
    }

    /// Types `text` into the agent's terminal and presses Enter.
    func send(_ text: String, to agent: OrcaAgent, done: @escaping @MainActor @Sendable (Bool) -> Void) {
        let client = client
        DispatchQueue.global(qos: .userInitiated).async {
            let ok = client.send(text, to: agent, enter: true)
            onMain { done(ok) }
        }
    }

    // Keystrokes, fits and restores share one serial queue so they reach Orca in order.
    private let keyQueue = DispatchQueue(label: "clawd.terminal-keys")

    /// Raw keystrokes (no Enter appended) for the terminal view.
    func sendKeys(_ text: String, to agent: OrcaAgent) {
        let client = client
        keyQueue.async { _ = client.send(text, to: agent, enter: false) }
    }

    /// Resizes the agent's terminal to Clawd's viewport, the way Orca's phone app does.
    /// Orca remembers the desktop size and puts it back on `restoreSize`.
    func fit(_ agent: OrcaAgent, cols: Int, rows: Int) {
        let client = client
        keyQueue.async { client.resize(agent, mode: "mobile-fit", cols: cols, rows: rows) }
    }

    /// `wait` blocks until done, for quitting; bounded so a stuck Orca can't hold up the exit.
    func restoreSize(_ agent: OrcaAgent, wait: Bool = false) {
        let client = client
        let done = DispatchSemaphore(value: 0)
        keyQueue.async {
            client.resize(agent, mode: "restore")
            done.signal()
        }
        if wait { _ = done.wait(timeout: .now() + 2) }
    }

    enum AnswerResult { case sent, gone, failed }

    /// Presses an option number in the agent's permission prompt. Claude Code acts on the digit
    /// alone, so the screen is re-read first: if that exact prompt is no longer showing, the digit
    /// would land in the agent's input box instead, and nothing is sent.
    func answer(_ number: Int, to agent: OrcaAgent, expecting prompt: PermissionPrompt,
                done: @escaping @MainActor @Sendable (AnswerResult) -> Void) {
        let client = client
        keyQueue.async {
            let result: AnswerResult
            if client.screen(of: agent).flatMap(PermissionPrompt.parse) != prompt {
                result = .gone
            } else {
                result = client.send(String(number), to: agent, enter: false) ? .sent : .failed
            }
            onMain { done(result) }
        }
    }

    /// The rendered terminal screen, one string per row.
    func screen(of agent: OrcaAgent, done: @escaping @MainActor @Sendable ([String]?) -> Void) {
        let client = client
        DispatchQueue.global(qos: .userInitiated).async {
            let lines = client.screen(of: agent)
            onMain { done(lines) }
        }
    }

    /// Tab titles by pane, used to tell apart agents that share a worktree.
    func titles(done: @escaping @MainActor @Sendable ([String: String]) -> Void) {
        let client = client
        DispatchQueue.global(qos: .utility).async {
            var result: [String: String] = [:]
            for t in client.terminals() {
                guard let tab = t["tabId"] as? String, let leaf = t["leafId"] as? String,
                      var title = t["title"] as? String else { continue }
                // Titles start with a spinner glyph such as "✳ " or "◑ ".
                if let first = title.unicodeScalars.first, !CharacterSet.alphanumerics.contains(first),
                   title.dropFirst().hasPrefix(" ") {
                    title = String(title.dropFirst(2))
                }
                result["\(tab):\(leaf)"] = title
            }
            onMain { done(result) }
        }
    }

    /// The runtime handle of the agent's terminal pane, for APIs that take one.
    func terminalHandle(for agent: OrcaAgent, done: @escaping @MainActor @Sendable (String?) -> Void) {
        let client = client
        keyQueue.async {
            let handle = client.handle(for: agent)
            onMain { done(handle) }
        }
    }

    /// Identifies Clawd's terminal size override to Orca, which keeps one per client.
    nonisolated static let clientId = AppIdentity.bundleID
}

// MARK: - Client

/// Orca requests, socket first with the CLI as fallback. Safe to use from any thread.
nonisolated struct OrcaClient: Sendable {
    let cli: String?

    /// Sends a request; falls back to the CLI only when the socket was unreachable, never after
    /// Orca received it, so a keystroke is never typed twice.
    private func request(_ method: String, _ params: [String: Any], cli args: @autoclosure () -> [String]?) -> Data? {
        // Demo data never reaches the real Orca: a made-up pane could match a real terminal
        // (by folder) and receive keystrokes or a resize.
        if TestHooks.fakeOrca != nil { return nil }
        switch OrcaRuntime.request(method, params) {
        case .ok(let data):
            return data
        case .unreachable:
            guard let cli, let args = args() else { return nil }
            return Shell.run(cli, args)
        case .failed(let code, let message):
            Log.debug("\(method) failed: \(code ?? "-") \(message ?? "")")
            return nil
        }
    }

    func terminals() -> [[String: Any]] {
        let data = request("terminal.list", [:], cli: ["terminal", "list", "--json"])
        return OrcaRuntime.result(data)?["terminals"] as? [[String: Any]] ?? []
    }

    func focus(_ agent: OrcaAgent) {
        // A chat session has no terminal: bring up its workspace, then its tab.
        if let tab = agent.chatTabId {
            guard !agent.worktreeId.isEmpty else { return }
            let worktree = "id:" + agent.worktreeId
            _ = request("worktree.activate", ["worktree": worktree, "navigation": "host"], cli: nil)
            _ = request("session.tabs.activate", ["worktree": worktree, "tabId": tab, "navigation": "host"], cli: nil)
            return
        }
        guard let handle = handle(for: agent) else { return }
        _ = request("terminal.focus", ["terminal": handle, "navigation": "host"],
                    cli: ["terminal", "switch", "--terminal", handle])
    }

    /// Types `text`, then Enter when `enter` is set. A multi-line message goes in as a
    /// bracketed paste so its newlines don't submit it halfway.
    func send(_ text: String, to agent: OrcaAgent, enter: Bool) -> Bool {
        guard let handle = handle(for: agent) else { return false }
        let payload = enter && text.contains("\n") ? "\u{1b}[200~\(text)\u{1b}[201~" : text
        let ok = request("terminal.send", ["terminal": handle, "text": payload, "enter": enter],
                         cli: ["terminal", "send", "--terminal", handle, "--text=\(payload)"] + (enter ? ["--enter"] : [])) != nil
        if !ok { Self.forgetHandle(for: agent) }
        return ok
    }

    func resize(_ agent: OrcaAgent, mode: String, cols: Int? = nil, rows: Int? = nil) {
        guard let handle = handle(for: agent) else { return }
        var params: [String: Any] = ["terminal": handle, "mode": mode, "clientId": OrcaWatcher.clientId]
        if let cols, let rows { params["cols"] = cols; params["rows"] = rows }
        _ = request("terminal.resizeForClient", params, cli: nil)
    }

    /// The rendered screen, with whatever is being typed at the agent's prompt put back in.
    func screen(of agent: OrcaAgent) -> [String]? {
        if let demo = TestHooks.fakeScreen(for: agent.paneKey) { return demo }
        guard let handle = handle(for: agent) else { return nil }
        guard let data = request("terminal.read", ["terminal": handle, "screen": true],
                                 cli: ["terminal", "read", "--terminal", handle, "--screen", "--json"]),
              let terminal = OrcaRuntime.result(data)?["terminal"] as? [String: Any],
              let lines = terminal["tail"] as? [String] else {
            Self.forgetHandle(for: agent)
            return nil
        }
        return TerminalDraft.merge(terminal["draft"] as? String ?? "", into: lines)
    }

    // Handles are stable for a pane's lifetime, so look each up once. The terminal view reads the
    // screen several times a second and would otherwise list every terminal each time.
    nonisolated(unsafe) private static var handleCache: [String: String] = [:]   // guarded by handleLock
    private static let handleLock = NSLock()

    func handle(for agent: OrcaAgent) -> String? {
        if let cached = Self.handleLock.withLock({ Self.handleCache[agent.paneKey] }) { return cached }
        guard let handle = Self.match(agent, in: terminals()) else { return nil }
        Self.handleLock.withLock { Self.handleCache[agent.paneKey] = handle }
        return handle
    }

    /// The terminal an agent runs in. Orca names a terminal by the tab and pane it sits in, but
    /// only for workspaces whose tabs are open in its window; terminals of the others are listed
    /// under "pty:…" placeholders with just their folder. Those match by folder (and agent), and
    /// only when that leaves exactly one: keys must never go to the wrong terminal.
    /// A session without a terminal never matches: by folder it would find another agent's.
    static func match(_ agent: OrcaAgent, in terminals: [[String: Any]]) -> String? {
        guard agent.hasTerminal else { return nil }
        let parts = agent.paneKey.split(separator: ":", maxSplits: 1).map(String.init)
        if parts.count == 2, let exact = terminals.first(where: {
            $0["tabId"] as? String == parts[0] && $0["leafId"] as? String == parts[1]
        }) {
            return exact["handle"] as? String
        }
        guard !agent.path.isEmpty else { return nil }
        let candidates = terminals.filter {
            ($0["tabId"] as? String)?.hasPrefix("pty:") == true
                && $0["worktreePath"] as? String == agent.path
                && (agent.agentType.isEmpty || ($0["agentIdentity"] as? String) == agent.agentType)
        }
        return candidates.count == 1 ? candidates[0]["handle"] as? String : nil
    }

    /// Forget a handle that stopped working (terminal closed or restarted, Orca relaunched).
    static func forgetHandle(for agent: OrcaAgent) {
        handleLock.withLock { handleCache[agent.paneKey] = nil }
    }
}

// MARK: - Draft

/// Orca lifts whatever is being typed at the agent's "❯" prompt out of the screen text into
/// `draft`. Putting it back lets the terminal view show what you type, with a cursor mark: the
/// screen text carries no cursor or inverse-video information.
nonisolated enum TerminalDraft {
    static func merge(_ draft: String, into screen: [String]) -> [String] {
        var lines = screen
        // The input line, not a highlighted "❯ 1. Yes" option in a permission dialog.
        let isInput = { (line: String) -> Bool in
            let t = line.trimmingCharacters(in: .whitespaces)
            return t.hasPrefix("❯") && t.range(of: #"^❯\s*\d+\."#, options: .regularExpression) == nil
        }
        guard PermissionPrompt.parse(lines) == nil, let i = lines.lastIndex(where: isInput),
              let mark = lines[i].firstIndex(of: "❯") else { return lines }
        if draft.isEmpty {
            // Empty prompt: the cursor sits on the first character of the placeholder.
            let rest = lines[i][lines[i].index(after: mark)...]
            let gap = rest.prefix { $0 == " " || $0 == "\u{a0}" }
            lines[i] = String(lines[i][...mark]) + String(gap.isEmpty ? " " : gap) + TerminalCursor.mark
                + String(rest.dropFirst(gap.count))
            return lines
        }
        let parts = draft.components(separatedBy: "\n")
        var rows = [String(lines[i][...mark]) + " " + parts[0]] + parts.dropFirst().map { "  " + $0 }
        rows[rows.count - 1] += TerminalCursor.mark
        lines[i] = rows[0]
        lines.insert(contentsOf: rows.dropFirst(), at: i + 1)
        // Keep the screen's height: the draft's extra rows replace blank ones below.
        for _ in 1..<rows.count {
            if let blank = lines.lastIndex(where: { $0.trimmingCharacters(in: .whitespaces).isEmpty }), blank > i + rows.count - 1 {
                lines.remove(at: blank)
            }
        }
        return lines
    }
}
