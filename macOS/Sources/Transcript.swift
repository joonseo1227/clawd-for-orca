import Foundation

// MARK: - Live transcript
// Claude Code appends every step of a session to ~/.claude/projects/<dir>/<sessionId>.jsonl as
// it happens: what it says between tool calls, each tool call and its result. Reading that
// file lets the chat view follow an agent step by step instead of waiting for its final reply.
// Thinking blocks usually come without their text (only a signature); sometimes the API sends
// a short summary instead, which the view shows. An empty block can only say that Claude thought.

nonisolated struct TimelineItem: Identifiable, Equatable, Sendable {
    enum Kind: Equatable, Sendable {
        case user
        case text
        case thinking
        case tool(name: String, result: String?, failed: Bool)
    }
    let id: String
    var kind: Kind
    var text: String       // message text, or the tool call's one-line summary
}

nonisolated enum Transcripts {
    /// ~/.claude, or CLAUDE_CONFIG_DIR when Claude Code was told to use another folder. Apps
    /// opened from Finder see it only when it is set for them too (`launchctl setenv`).
    static let claudeHome = claudeHome(ProcessInfo.processInfo.environment)

    static func claudeHome(_ env: [String: String]) -> URL {
        if let custom = env["CLAUDE_CONFIG_DIR"], !custom.isEmpty {
            return URL(fileURLWithPath: (custom as NSString).expandingTildeInPath, isDirectory: true)
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude", isDirectory: true)
    }
    // sessionId -> transcript. Only touched from the app's serial transcript queue.
    nonisolated(unsafe) private static var urlCache: [String: URL] = [:]

    struct Session: Sendable {
        let pid: Int32, id: String, cwd: String, updatedAt: Double
        let paneKey: String?   // the Orca pane the process runs in, from its ORCA_PANE_KEY
    }

    /// Live Claude Code processes, from the per-process files Claude Code keeps.
    static func sessions() -> [Session] {
        let dir = claudeHome.appendingPathComponent("sessions")
        let files = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
        return files.filter { $0.pathExtension == "json" }.compactMap { url in
            guard let data = try? Data(contentsOf: url),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let id = json["sessionId"] as? String, let cwd = json["cwd"] as? String else { return nil }
            let pid = Int32((json["pid"] as? Int) ?? Int(json["pid"] as? String ?? "") ?? 0)
            guard pid > 0, kill(pid, 0) == 0 else { return nil }   // skip files left by exited processes
            let updated = (json["updatedAt"] as? Double) ?? Double(json["updatedAt"] as? String ?? "") ?? 0
            return Session(pid: pid, id: id, cwd: cwd, updatedAt: updated,
                           paneKey: environment(of: pid)["ORCA_PANE_KEY"])
        }
    }

    static func url(for sessionId: String) -> URL? {
        if let cached = urlCache[sessionId] { return cached }
        let projects = claudeHome.appendingPathComponent("projects")
        let dirs = (try? FileManager.default.contentsOfDirectory(at: projects, includingPropertiesForKeys: nil)) ?? []
        for dir in dirs {
            let file = dir.appendingPathComponent("\(sessionId).jsonl")
            if FileManager.default.fileExists(atPath: file.path) {
                urlCache[sessionId] = file
                return file
            }
        }
        return nil
    }

    /// The transcript of the Claude Code session running in an Orca pane. Orca starts every pane's
    /// processes with ORCA_PANE_KEY set, so the pane identifies its session exactly, even when
    /// several tabs run Claude in the same folder. Without it, fall back to the folder and the
    /// last prompt.
    static func locate(paneKey: String, path: String, prompt: String?) -> URL? {
        let all = sessions()
        if let exact = all.first(where: { $0.paneKey == paneKey }) { return url(for: exact.id) }
        let candidates = all.filter { $0.cwd == path || $0.cwd.hasPrefix(path + "/") }
        if candidates.count <= 1 { return candidates.first.flatMap { url(for: $0.id) } }
        if let prompt, let wanted = snippet(prompt, 60) {
            for s in candidates {
                guard let u = url(for: s.id), let last = lastPrompt(u) else { continue }
                if snippet(last, 60) == wanted { return u }
            }
        }
        return candidates.max { $0.updatedAt < $1.updatedAt }.flatMap { url(for: $0.id) }
    }

    private static func lastPrompt(_ url: URL) -> String? {
        for line in tail(url, bytes: 400_000).reversed() {
            guard line.contains("\"last-prompt\""),
                  let json = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any] else { continue }
            return json["lastPrompt"] as? String
        }
        return nil
    }

    /// Another process's environment, read with sysctl(KERN_PROCARGS2): argc, the executable
    /// path, padding, argv, then environment strings, all NUL-separated.
    static func environment(of pid: Int32) -> [String: String] {
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, size > MemoryLayout<Int32>.size else { return [:] }
        var buf = [UInt8](repeating: 0, count: size)
        guard sysctl(&mib, 3, &buf, &size, nil, 0) == 0 else { return [:] }
        let argc = buf.withUnsafeBytes { $0.loadUnaligned(as: Int32.self) }
        var i = MemoryLayout<Int32>.size
        func skipString() { while i < size, buf[i] != 0 { i += 1 } }
        skipString()                                    // executable path
        while i < size, buf[i] == 0 { i += 1 }          // padding
        for _ in 0..<max(0, Int(argc)) { skipString(); i += 1 }
        var env: [String: String] = [:]
        while i < size, buf[i] != 0 {
            let start = i
            skipString()
            if let entry = String(bytes: buf[start..<i], encoding: .utf8), let eq = entry.firstIndex(of: "=") {
                env[String(entry[..<eq])] = String(entry[entry.index(after: eq)...])
            }
            i += 1
        }
        return env
    }

    /// Complete lines from the last `bytes` of the file.
    private static func tail(_ url: URL, bytes: Int) -> [String] {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return [] }
        defer { try? handle.close() }
        let size = (try? handle.seekToEnd()) ?? 0
        let start = size > UInt64(bytes) ? size - UInt64(bytes) : 0
        try? handle.seek(toOffset: start)
        let data = (try? handle.readToEnd()) ?? Data()
        var lines = String(decoding: data, as: UTF8.self).components(separatedBy: "\n")
        if start > 0, !lines.isEmpty { lines.removeFirst() }   // cut mid-line
        return lines
    }

    // Readers by transcript path, most recently used last. Only touched from the serial queue.
    nonisolated(unsafe) private static var readers: [(path: String, reader: TranscriptReader)] = []

    /// The incremental reader for a transcript, kept for the 16 most recently read files.
    /// Like everything in `Transcripts`, call it only from the app's serial transcript queue.
    static func reader(for url: URL) -> TranscriptReader {
        let path = url.standardizedFileURL.path
        if let i = readers.firstIndex(where: { $0.path == path }) {
            let hit = readers.remove(at: i)
            readers.append(hit)
            return hit.reader
        }
        let reader = TranscriptReader(url: url)
        readers.append((path, reader))
        if readers.count > 16 { readers.removeFirst(readers.count - 16) }
        return reader
    }

    /// Everything since the user's latest prompt, newest last. Goes through the cached reader,
    /// so repeated calls only parse what was appended since the last one.
    static func timeline(_ url: URL) -> [TimelineItem] {
        reader(for: url).timeline().items
    }

    // Tags Claude Code wraps around text it injects into user entries: reminders, slash-command
    // echoes and their output, background task notices, bash-mode input and output.
    private static let injected = try! NSRegularExpression(
        pattern: "<(system-reminder|command-name|command-message|command-args|command-contents|"
            + "local-command-stdout|local-command-stderr|local-command-caveat|task-notification|"
            + "bash-input|bash-stdout|bash-stderr|user-prompt-submit-hook)\\b[^>]*>[\\s\\S]*?</\\1>")

    /// The text of a real prompt the user typed, or nil for tool results, interrupt markers and
    /// anything Claude Code injected (isMeta entries, tagged reminders and command echoes).
    static func promptText(_ entry: [String: Any]) -> String? {
        guard entry["type"] as? String == "user", entry["isMeta"] as? Bool != true,
              let message = entry["message"] as? [String: Any] else { return nil }
        var parts: [String] = []
        if let s = message["content"] as? String { parts = [s] }
        for block in message["content"] as? [[String: Any]] ?? [] {
            if block["type"] as? String == "tool_result" { return nil }
            if block["type"] as? String == "text", let t = block["text"] as? String { parts.append(t) }
        }
        let text = parts.map { part -> String in
            let range = NSRange(part.startIndex..., in: part)
            let kept = injected.stringByReplacingMatches(in: part, range: range, withTemplate: "")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            return kept.hasPrefix("[Request interrupted by user") ? "" : kept
        }.filter { !$0.isEmpty }.joined(separator: "\n")
        return text.isEmpty ? nil : text
    }

    static func resultText(_ content: Any?) -> String? {
        if let s = content as? String { return snippet(s, 160) }
        let texts = (content as? [[String: Any]] ?? []).compactMap { $0["text"] as? String }
        return snippet(texts.joined(separator: " "), 160)
    }

    /// One line saying what the tool call does.
    static func summary(_ name: String, _ input: [String: Any]) -> String {
        func s(_ key: String) -> String? { input[key] as? String }
        let file = s("file_path").map { ($0 as NSString).lastPathComponent }
        let line: String?
        switch name {
        case "Bash": line = s("description") ?? s("command")
        case "Read", "Write", "Edit", "MultiEdit", "NotebookEdit": line = file
        case "Grep", "Glob": line = s("pattern")
        case "WebSearch": line = s("query")
        case "WebFetch": line = s("url")
        case "Agent", "Task": line = s("description")
        case "TodoWrite": line = String(localized: "Update to-do list", comment: "Summary of a TodoWrite tool call")
        default:
            // Dictionary order changes from run to run; pick by key so the line stays put.
            let preferred = ["description", "command", "query", "pattern", "url", "file_path", "path", "prompt", "text", "value"]
            line = (preferred + input.keys.sorted()).lazy.compactMap { s($0) }.first
        }
        return snippet(line, 120) ?? ""
    }
}

/// Follows one transcript as it grows. It remembers how many bytes it has consumed and the
/// timeline built from them, so each call parses only the lines appended since the last one.
/// The first read (and any read after the file was truncated, replaced or jumped far ahead)
/// parses just the tail: the last megabyte, or six when the latest prompt isn't in it.
///
/// Not thread-safe: use it only from the app's serial transcript queue.
nonisolated final class TranscriptReader {
    let url: URL
    private static let windows = [1_000_000, 6_000_000]
    private static let shown = 80
    private static let kept = 240      // items held before trimming the front back to `shown`

    private var loaded = false
    private var inode: UInt64 = 0
    private var offset: UInt64 = 0     // bytes consumed: always just past a newline
    private var sawPrompt = false

    // The current turn: everything since the latest prompt (or the window start without one).
    private var items: [TimelineItem] = []
    private var trimmed = 0            // items dropped from the front of the turn
    private var toolIndex: [String: Int] = [:]   // tool_use id -> absolute item index
    private var seen: Set<String> = []           // entry uuids already in this turn
    private var sequence = 0           // entries read since the last reset, for positional ids
    private var last: [TimelineItem] = []
    /// Claude's AskUserQuestion still waiting for an answer, if any.
    private(set) var question: AgentQuestion?

    init(url: URL) { self.url = url }

    /// The timeline since the user's latest prompt, newest last, at most 80 items, and whether
    /// it differs from what the previous call returned.
    func timeline() -> (items: [TimelineItem], changed: Bool) {
        var st = stat()
        guard stat(url.path, &st) == 0 else {
            reset()
            return finish()
        }
        let size = UInt64(st.st_size), node = UInt64(st.st_ino)
        // Rewritten, truncated, or so far ahead that reading the tail is cheaper.
        if loaded, node != inode || size < offset || size - offset > UInt64(Self.windows.last!) { reset() }
        if !loaded {
            load(size: size)
            inode = node
        } else if size > offset, let data = read(from: offset, to: size) {
            offset += consume(data)
        }
        return finish()
    }

    private func finish() -> (items: [TimelineItem], changed: Bool) {
        let current = Array(items.suffix(Self.shown))
        defer { last = current }
        return (current, current != last)
    }

    private func reset() {
        loaded = false
        offset = 0
        sawPrompt = false
        startTurn()
        sequence = 0
    }

    private func startTurn() {
        items = []
        trimmed = 0
        toolIndex = [:]
        seen = []
        question = nil
    }

    /// First read: the tail window, widened when the latest prompt isn't inside it.
    private func load(size: UInt64) {
        for window in Self.windows {
            reset()
            loaded = true
            offset = size      // a window with no line break in it: follow only what comes next
            let start = size > UInt64(window) ? size - UInt64(window) : 0
            guard var data = read(from: start, to: size) else { loaded = false; return }   // retry next call
            var base = start
            if start > 0 {
                // Cut mid-line: skip to the next full one.
                guard let nl = data.firstIndex(of: 0x0A) else { continue }
                let skip = nl - data.startIndex + 1
                data = data.subdata(in: (data.startIndex + skip)..<data.endIndex)
                base += UInt64(skip)
            }
            offset = base + consume(data)
            if sawPrompt || start == 0 { return }
        }
    }

    private func read(from start: UInt64, to end: UInt64) -> Data? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        do {
            try handle.seek(toOffset: start)
            return try handle.read(upToCount: Int(end - start)) ?? Data()
        } catch { return nil }
    }

    /// Parses the complete lines in `data` and returns how many bytes they took. A trailing
    /// line without its newline is still being written; it is left for the next read.
    private func consume(_ data: Data) -> UInt64 {
        var used = 0
        data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            var lineStart = 0
            for i in 0..<raw.count where raw[i] == 0x0A {
                if i > lineStart { ingest(Data(raw[lineStart..<i])) }
                lineStart = i + 1
            }
            used = lineStart
        }
        return UInt64(used)
    }

    private func ingest(_ line: Data) {
        guard let entry = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              entry["isSidechain"] as? Bool != true else { return }
        let type = entry["type"] as? String
        guard type == "user" || type == "assistant" else { return }
        sequence += 1
        let uuid = entry["uuid"] as? String
        let key = uuid ?? "#\(sequence)"
        func id(_ b: Int) -> String { uuid.map { "\($0):\(b)" } ?? "p\(sequence).\(b)" }
        let message = entry["message"] as? [String: Any]

        if type == "user" {
            if let text = Transcripts.promptText(entry) {
                sawPrompt = true
                startTurn()
                seen.insert(key)
                append(TimelineItem(id: uuid.map { "\($0):u" } ?? "p\(sequence).u", kind: .user, text: text))
                return
            }
            guard seen.insert(key).inserted else { return }
            for block in message?["content"] as? [[String: Any]] ?? [] where block["type"] as? String == "tool_result" {
                guard let toolId = block["tool_use_id"] as? String else { continue }
                if case .terminal(toolId) = question?.source { question = nil }
                guard let abs = toolIndex[toolId] else { continue }
                let i = abs - trimmed
                guard i >= 0, i < items.count, case .tool(let name, _, _) = items[i].kind else { continue }
                items[i].kind = .tool(name: name, result: Transcripts.resultText(block["content"]) ?? "",
                                      failed: block["is_error"] as? Bool == true)
            }
            return
        }

        guard seen.insert(key).inserted else { return }
        for (b, block) in (message?["content"] as? [[String: Any]] ?? []).enumerated() {
            switch block["type"] as? String {
            case "text":
                let text = (block["text"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                if !text.isEmpty { append(TimelineItem(id: id(b), kind: .text, text: text)) }
            case "thinking", "redacted_thinking":
                let summary = (block["thinking"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                if items.last?.kind == .thinking {
                    // Consecutive blocks are one stretch of thinking.
                    if !summary.isEmpty {
                        items[items.count - 1].text += (items[items.count - 1].text.isEmpty ? "" : "\n\n") + summary
                    }
                } else {
                    append(TimelineItem(id: id(b), kind: .thinking, text: summary))
                }
            case "tool_use":
                let name = block["name"] as? String ?? String(localized: "Tool", comment: "Name for a tool call without one")
                let toolId = block["id"] as? String ?? "x" + id(b)
                toolIndex[toolId] = trimmed + items.count
                if name == "AskUserQuestion" {
                    question = AgentQuestion.parse(toolInput: block["input"] as? [String: Any] ?? [:], toolUseId: toolId)
                }
                append(TimelineItem(id: toolId, kind: .tool(name: name, result: nil, failed: false),
                                    text: Transcripts.summary(name, block["input"] as? [String: Any] ?? [:])))
            default:
                break
            }
        }
    }

    /// Adds an item, dropping old ones from the front once a long turn has piled up enough
    /// that they can no longer be shown.
    private func append(_ item: TimelineItem) {
        items.append(item)
        guard items.count > Self.kept else { return }
        let drop = items.count - Self.shown
        items.removeFirst(drop)
        trimmed += drop
        toolIndex = toolIndex.filter { $0.value >= trimmed }
    }
}
