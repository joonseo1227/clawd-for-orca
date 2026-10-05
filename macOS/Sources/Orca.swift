import AppKit

// MARK: - Orca

/// One coding agent running in an Orca terminal pane.
nonisolated struct OrcaAgent: Equatable, Sendable {
    let paneKey: String        // "<tabId>:<leafId>"
    let name: String
    let state: String          // working, blocked, waiting, done, idle
    let tool: String?
    let toolInput: String?
    let lastMessage: String?
    let prompt: String?
    let stateStartedAt: Date?
    let worktreeActive: Bool   // the worktree currently selected in Orca
    var path: String = ""      // worktree folder, used to find the Claude Code transcript
    var worktreeId: String = ""
    var agentType: String = ""

    var needsYou: Bool { state == "blocked" || state == "waiting" }

    /// False for a session started in Orca's chat: Orca runs it without a terminal, so it can't be
    /// shown, typed into or answered from Clawd. Orca names its pane after the session
    /// ("structured-agent-session-<id>:<leaf>"); nothing else in `worktree.ps` marks it reliably.
    var hasTerminal: Bool { !paneKey.hasPrefix(Self.chatSessionPrefix) }
    static let chatSessionPrefix = "structured-agent-session-"

    /// The tab Orca shows a chat session in ("agent-session:<session id>"); nil for a terminal.
    var chatTabId: String? {
        guard !hasTerminal, let tab = paneKey.split(separator: ":", maxSplits: 1).first else { return nil }
        return "agent-session:" + tab.dropFirst(Self.chatSessionPrefix.count)
    }

    /// What Clawd does to mirror this agent's current tool.
    var activity: Activity {
        switch tool ?? "" {
        case "Bash": return .build
        case "Edit", "Write", "MultiEdit", "NotebookEdit": return .type
        case "Read", "Grep", "Glob", "WebFetch", "WebSearch", "ToolSearch": return .think
        case "Agent", "Task", "Workflow": return .juggle
        case let t where t.hasPrefix("mcp__"): return .lookAround
        default: return .type
        }
    }

    /// What the agent wants from the user: the command it asks to run, or its question.
    var ask: String? {
        if state == "blocked" {
            let what = [tool, snippet(toolInput, 90)].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: ": ")
            return what.isEmpty ? nil : what
        }
        return snippet(lastMessage, 160)
    }
}

/// Collapses whitespace and trims to `limit` characters.
nonisolated func snippet(_ s: String?, _ limit: Int) -> String? {
    guard let s else { return nil }
    let flat = s.split(whereSeparator: \.isWhitespace).joined(separator: " ")
    if flat.isEmpty { return nil }
    return flat.count > limit ? String(flat.prefix(limit - 1)) + "…" : flat
}

/// "3 min", "1 hr 5 min"
nonisolated func duration(_ seconds: TimeInterval) -> String {
    let m = max(1, Int(seconds / 60))
    return m < 60 ? String(localized: "\(m) min", comment: "Duration in minutes")
        : String(localized: "\(m / 60) hr \(m % 60) min", comment: "Duration in hours and minutes")
}

/// "just now", "3 min ago"
nonisolated func ago(_ date: Date) -> String {
    let s = Date().timeIntervalSince(date)
    return s < 60 ? String(localized: "just now", comment: "Less than a minute ago; used mid-sentence")
        : String(localized: "\(duration(s)) ago", comment: "%@ is a duration, e.g. 3 min")
}

extension OrcaAgent {
    /// Agents from a `worktree.ps` answer (socket or `orca worktree ps --json`).
    nonisolated static func parse(_ data: Data?) -> [OrcaAgent]? {
        guard let worktrees = OrcaRuntime.result(data)?["worktrees"] as? [[String: Any]] else { return nil }
        let now = Date().timeIntervalSince1970 * 1000
        var result: [OrcaAgent] = []
        for w in worktrees {
            let display = w["displayName"] as? String ?? ""
            let name = display.isEmpty || display == "main" ? (w["repo"] as? String ?? "?") : display
            for a in w["agents"] as? [[String: Any]] ?? [] {
                guard let key = a["paneKey"] as? String, var state = a["state"] as? String else { continue }
                // Orca treats live states untouched for 30 minutes as idle.
                if let updated = a["updatedAt"] as? Double, state != "done", now - updated > Self.staleAfter * 1000 { state = "idle" }
                result.append(OrcaAgent(
                    paneKey: key, name: name, state: state,
                    tool: a["toolName"] as? String,
                    toolInput: a["toolInput"] as? String,
                    lastMessage: a["lastAssistantMessage"] as? String,
                    prompt: a["prompt"] as? String,
                    stateStartedAt: (a["stateStartedAt"] as? Double).map { Date(timeIntervalSince1970: $0 / 1000) },
                    worktreeActive: w["isActive"] as? Bool ?? false,
                    path: w["path"] as? String ?? "",
                    worktreeId: w["worktreeId"] as? String ?? "",
                    agentType: a["agentType"] as? String ?? ""))
            }
        }
        return result
    }

    nonisolated static let staleAfter: TimeInterval = 30 * 60
}
