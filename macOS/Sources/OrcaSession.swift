import CryptoKit
import Foundation

// MARK: - Orca chat sessions
// A session started in Orca's chat runs without a terminal: Orca keeps its conversation as a
// history of items and takes messages and answers through `agentSession.*` calls, the same ones
// its own chat view and phone app use. Each call that changes the session carries an envelope:
// a fresh operation id, the session's fence (bumped whenever its host restarts) and a SHA-256
// over what the call does, which Orca recomputes and must match.

/// What Clawd shows of a chat session: the latest turn, and whatever it is waiting on.
nonisolated struct SessionSnapshot: Equatable, Sendable {
    var fence: Int
    var timeline: [TimelineItem]
    var question: AgentQuestion?
    var approval: SessionApproval?
}

/// A pending permission request in a chat session, drawn like a terminal's dialog.
nonisolated struct SessionApproval: Equatable, Sendable {
    let itemId: String
    let revision: Int
    let prompt: PermissionPrompt
    let optionIds: [String]   // by the prompt's option number, from 1

    static func parse(_ item: [String: Any]) -> SessionApproval? {
        guard let body = item["body"] as? [String: Any], body["kind"] as? String == "approval",
              (body["resolution"] as? [String: Any])?["state"] as? String == "pending",
              let itemId = item["itemId"] as? String, let revision = item["revision"] as? Int else { return nil }
        let raw = (body["options"] as? [[String: Any]] ?? []).compactMap { o -> (String, String)? in
            guard let id = o["id"] as? String, let label = o["label"] as? String else { return nil }
            return (id, label)
        }
        guard !raw.isEmpty else { return nil }
        let title = body["displayName"] as? String ?? body["title"] as? String ?? ""
        let lines = [body["detail"] as? String, body["description"] as? String].compactMap { $0 }
            .flatMap { $0.split(whereSeparator: \.isNewline).map(String.init) }
            .filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        let prompt = PermissionPrompt(question: body["title"] as? String ?? title, detail: [title] + lines,
                                      options: raw.enumerated().map { .init(number: $0 + 1, label: $1.1) })
        return SessionApproval(itemId: itemId, revision: revision, prompt: prompt, optionIds: raw.map(\.0))
    }
}

nonisolated enum OrcaSession {
    /// Items read per look; a turn longer than this shows its latest part.
    static let historyLimit = 120

    /// The snapshot from an `agentSession.history` page.
    static func snapshot(_ page: [String: Any]) -> SessionSnapshot? {
        guard let fence = page["fence"] as? Int else { return nil }
        let items = page["items"] as? [[String: Any]] ?? []
        return SessionSnapshot(fence: fence, timeline: timeline(items),
                               question: items.lazy.reversed().compactMap(AgentQuestion.parse(sessionItem:)).first,
                               approval: items.lazy.reversed().compactMap(SessionApproval.parse).first)
    }

    /// The turn since the latest user message, like a transcript's.
    static func timeline(_ items: [[String: Any]]) -> [TimelineItem] {
        var result: [TimelineItem] = []
        for item in items {
            guard let id = item["itemId"] as? String, let body = item["body"] as? [String: Any] else { continue }
            switch body["kind"] as? String {
            case "message":
                let text = (body["blocks"] as? [[String: Any]] ?? []).compactMap { $0["text"] as? String }
                    .joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
                if body["role"] as? String == "user" {
                    result = []
                    if !text.isEmpty { result.append(TimelineItem(id: id, kind: .user, text: text)) }
                } else if !text.isEmpty {
                    result.append(TimelineItem(id: id, kind: .text, text: text))
                }
            case "tool-call":
                let name = body["name"] as? String ?? String(localized: "Tool", comment: "Name for a tool call without one")
                let state = body["state"] as? String ?? ""
                let head = (body["output"] as? [String: Any])?["head"] as? String
                let done = state != "running" && state != "pending"
                result.append(TimelineItem(id: id, kind: .tool(name: name, result: done ? (snippet(head, 160) ?? "") : nil,
                                                               failed: ["failed", "error", "errored"].contains(state)),
                                           text: Transcripts.summary(name, body["input"] as? [String: Any] ?? [:])))
            default:
                break
            }
        }
        return result
    }

    // MARK: Envelope

    static func operationId() -> String {
        let ms = Int64(Date().timeIntervalSince1970 * 1000)
        return "\(ms)-" + UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }

    /// Orca's payload fingerprint: SHA-256 over {method, sessionId, fields} as JSON with keys
    /// sorted at every depth and absent fields left out.
    static func fingerprint(method: String, sessionId: String, fields: [String: Any]) -> String {
        SHA256.hash(data: Data(canonical(["method": method, "sessionId": sessionId, "fields": fields]).utf8))
            .map { String(format: "%02x", $0) }.joined()
    }

    static func envelope(method: String, sessionId: String, fence: Int, fields: [String: Any]) -> [String: Any] {
        ["sessionId": sessionId, "clientOperationId": operationId(), "expectedRuntimeFence": fence,
         "payloadFingerprint": fingerprint(method: method, sessionId: sessionId, fields: fields)]
    }

    /// The value as JavaScript's JSON.stringify writes it, keys sorted.
    static func canonical(_ value: Any?) -> String {
        switch value {
        case nil, is NSNull: return "null"
        case let s as String: return quote(s)
        case let b as Bool: return b ? "true" : "false"
        case let n as Int: return String(n)
        case let a as [Any]: return "[" + a.map(canonical).joined(separator: ",") + "]"
        case let d as [String: Any]:
            return "{" + d.keys.sorted { Array($0.utf16).lexicographicallyPrecedes(Array($1.utf16)) }
                .map { quote($0) + ":" + canonical(d[$0]) }.joined(separator: ",") + "}"
        default: return "null"
        }
    }

    /// A JSON string literal: only quotes, backslashes and control characters are escaped.
    private static func quote(_ s: String) -> String {
        var out = "\""
        for u in s.unicodeScalars {
            switch u {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case "\u{08}": out += "\\b"
            case "\u{0C}": out += "\\f"
            case _ where u.value < 0x20: out += String(format: "\\u%04x", u.value)
            default: out.unicodeScalars.append(u)
            }
        }
        return out + "\""
    }
}

// MARK: - Calls

nonisolated extension OrcaClient {
    /// The latest part of a chat session's history.
    func session(_ sessionId: String) -> SessionSnapshot? {
        if TestHooks.fakeOrca != nil { return nil }
        guard let data = OrcaRuntime.call("agentSession.history", ["sessionId": sessionId, "direction": "tail",
                                                                  "limit": OrcaSession.historyLimit]),
              let page = OrcaRuntime.result(data)?["page"] as? [String: Any] else { return nil }
        return OrcaSession.snapshot(page)
    }

    /// Sends a message the way Orca's chat does; Orca queues it while the agent is working.
    func sessionSend(_ text: String, to sessionId: String) -> Bool {
        let fields: [String: Any] = ["body": ["kind": "message", "role": "user", "blocks": [["type": "text", "text": text]]]]
        if case .sent = mutate("agentSession.send", fingerprinting: "agentSession.send", sessionId, fields) { return true }
        return false
    }

    func sessionApprove(_ approval: SessionApproval, option number: Int, in sessionId: String) -> OrcaWatcher.AnswerResult {
        guard approval.optionIds.indices.contains(number - 1) else { return .failed }
        let fields: [String: Any] = ["itemId": approval.itemId, "expectedRevision": approval.revision,
                                     "optionId": approval.optionIds[number - 1]]
        return mutate("agentSession.respondToApproval", fingerprinting: "agentSession.respondTo:approval", sessionId, fields)
    }

    func sessionAnswer(_ question: AgentQuestion, _ answers: [AgentQuestion.Answer], in sessionId: String) -> OrcaWatcher.AnswerResult {
        guard case .session(let itemId, let revision) = question.source else { return .failed }
        let wire: [[String: Any]] = zip(question.items, answers).map { item, a in
            var entry: [String: Any] = ["questionId": item.id,
                                        "optionIds": a.picked.sorted().compactMap { item.options.indices.contains($0) ? item.options[$0].id : nil }]
            if !a.otherText.isEmpty { entry["other"] = a.otherText }
            return entry
        }
        let fields: [String: Any] = ["itemId": itemId, "expectedRevision": revision, "answers": wire]
        return mutate("agentSession.respondToQuestion", fingerprinting: "agentSession.respondTo:question", sessionId, fields)
    }

    /// Runs one changing call at the session's current fence. Orca answers a refused one (stale
    /// revision, already answered, host restarted) with `ok: false` inside a successful reply.
    private func mutate(_ method: String, fingerprinting name: String, _ sessionId: String,
                        _ fields: [String: Any]) -> OrcaWatcher.AnswerResult {
        guard let fence = session(sessionId)?.fence else { return .failed }
        var params = fields
        params["envelope"] = OrcaSession.envelope(method: name, sessionId: sessionId, fence: fence, fields: fields)
        switch OrcaRuntime.request(method, params, timeout: 10) {
        case .ok(let data):
            if OrcaRuntime.result(data)?["ok"] as? Bool == true { return .sent }
            Log.debug("\(method) refused: \(String(decoding: data.prefix(400), as: UTF8.self))")
            return .gone
        case .unreachable:
            return .failed
        case .failed(let code, let message):
            Log.debug("\(method) failed: \(code ?? "-") \(message ?? "")")
            return .failed
        }
    }
}
