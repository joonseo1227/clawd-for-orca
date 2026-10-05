import Foundation
import Security

// MARK: - Pairing
// A live terminal stream needs Orca's paired WebSocket transport. The user creates a pairing
// code once in Orca ("Pair a phone" → Copy pairing code); Clawd keeps it in the Keychain since
// its device token can drive the user's terminals.

nonisolated struct OrcaPairing: Sendable {
    let endpoint: String
    let deviceToken: String
    let publicKeyB64: String

    /// Accepts `orca://pair?code=…` or the bare base64url payload.
    init?(code raw: String) {
        var code = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if let comps = URLComponents(string: code), comps.scheme == "orca",
           let value = comps.queryItems?.first(where: { $0.name == "code" })?.value {
            code = value
        }
        var b64 = code.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while b64.count % 4 != 0 { b64 += "=" }
        guard let data = Data(base64Encoded: b64) else { return nil }
        self.init(json: data)
    }

    init?(json data: Data) {
        guard let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let endpoint = o["endpoint"] as? String, let token = o["deviceToken"] as? String,
              let key = o["publicKeyB64"] as? String else { return nil }
        self.endpoint = endpoint
        self.deviceToken = token
        self.publicKeyB64 = key
    }

    var json: Data {
        (try? JSONSerialization.data(withJSONObject: ["endpoint": endpoint, "deviceToken": deviceToken, "publicKeyB64": publicKeyB64])) ?? Data()
    }

    /// Orca runs on this Mac, so talk to it over loopback: the LAN address in the code changes
    /// whenever the network does.
    var localEndpoint: String {
        guard var comps = URLComponents(string: endpoint), comps.scheme == "ws" || comps.scheme == "wss" else { return endpoint }
        comps.host = "127.0.0.1"
        return comps.string ?? endpoint
    }
}

nonisolated enum PairingStore {
    nonisolated(unsafe) private static let base = query(service: AppIdentity.bundleID + ".orca-pairing")
    /// Where builds before the bundle ID change kept it; moved over on first use.
    nonisolated(unsafe) private static let legacy = query(service: "local.clawd.pet.orca-pairing")

    private static func query(service: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: "orca"]
    }

    /// Whether a pairing is stored, without reading its secret. Reading attributes doesn't ask
    /// for keychain access, so checking at launch never shows a password prompt.
    static func exists() -> Bool {
        exists(base) || exists(legacy)
    }

    private static func exists(_ base: [String: Any]) -> Bool {
        var query = base
        query[kSecReturnAttributes as String] = true
        return SecItemCopyMatching(query as CFDictionary, nil) == errSecSuccess
    }

    static func load() -> OrcaPairing? {
        if let pairing = read(base) { return pairing }
        // An older build's item: reading it may ask once for access, then it moves here.
        guard let pairing = read(legacy) else { return nil }
        if save(pairing) { SecItemDelete(legacy as CFDictionary) }
        return pairing
    }

    private static func read(_ base: [String: Any]) -> OrcaPairing? {
        var query = base
        query[kSecReturnData as String] = true
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
        return OrcaPairing(json: data)
    }

    /// Updates the stored item in place rather than deleting first, so a failed write never
    /// leaves the user with no pairing at all.
    @discardableResult
    static func save(_ pairing: OrcaPairing) -> Bool {
        let attributes: [String: Any] = [
            kSecValueData as String: pairing.json,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
        ]
        if exists(base) {
            let status = SecItemUpdate(base as CFDictionary, attributes as CFDictionary)
            if status != errSecSuccess { Log.error("pairing update failed: \(status)") }
            return status == errSecSuccess
        }
        let item = base.merging(attributes) { $1 }
        let status = SecItemAdd(item as CFDictionary, nil)
        if status != errSecSuccess { Log.error("pairing save failed: \(status)") }
        return status == errSecSuccess
    }

    static func clear() {
        SecItemDelete(base as CFDictionary)
        SecItemDelete(legacy as CFDictionary)
    }
}

// MARK: - Bridge process

/// Runs clawd-bridge.js under Orca's bundled Node and relays terminal subscriptions.
final class OrcaBridge {
    enum Event {
        case snapshot(String, cols: Int?, rows: Int?)
        case data(String)
        case resized(cols: Int, rows: Int)
        case failed(String)
        case closed
    }

    private var process: Process?
    private var input: FileHandle?
    private var outputs: [FileHandle] = []    // stdout and stderr read ends of the current run
    private var buffer = Data()
    private var stderrTail = Data()
    private var pendingEnds = 0               // stdout EOF, stderr EOF and exit still to come
    private var ready = false                 // the bridge loaded Orca's module and took the pairing
    private var streaming: Set<String> = []   // subscriptions that received their first screen
    private var handlers: [String: (Event) -> Void] = [:]
    private var pairing: OrcaPairing?    // read from the keychain only when the bridge starts
    private var paired: Bool
    private(set) var lastError: String?
    private var idleTimer: Timer?

    /// The bridge is a full Node process (~80 MB); quit it once nothing has been streamed for a
    /// while. It starts again in about a second the next time the terminal view opens.
    static let idleShutdown: TimeInterval = 120

    /// Only the end of stderr is kept: enough for Node's error message, bounded if it chatters.
    private static let stderrLimit = 4096

    /// Asked each time the bridge starts, since Orca may be moved or updated meanwhile.
    var installation: () -> OrcaInstallation? = { OrcaInstallation.locate() }

    var isPaired: Bool { paired }

    init() { paired = !TestHooks.noLive && PairingStore.exists() }

    /// Returns false when the Keychain refused the pairing; nothing changes then.
    @discardableResult
    func pair(_ p: OrcaPairing) -> Bool {
        guard PairingStore.save(p) else {
            lastError = String(localized: "Couldn’t save the pairing to the keychain")
            return false
        }
        stop()
        pairing = p
        paired = true
        lastError = nil
        return true
    }

    func unpair() {
        PairingStore.clear()
        stop()
        pairing = nil
        paired = false
    }

    private var script: String? {
        if let bundled = Bundle.main.path(forResource: "clawd-bridge", ofType: "js") { return bundled }
        // Running the bare binary during development: the script is in the repository's Bridge/
        // folder, a few levels above the binary (macOS/clawd or macOS/.build/<config>/Clawd).
        var dir = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent()
        for _ in 0..<5 {
            let candidate = dir.appendingPathComponent("Bridge/clawd-bridge.js").path
            if FileManager.default.fileExists(atPath: candidate) { return candidate }
            dir.deleteLastPathComponent()
        }
        return nil
    }

    private func ensureRunning() -> Bool {
        if let process {
            if process.isRunning { return true }
            // Exited but its pipes haven't drained yet: settle that run before starting anew.
            endRun(process)
        }
        if pairing == nil, paired { pairing = PairingStore.load() }
        guard let pairing else {
            lastError = String(localized: "Couldn’t read the pairing from the keychain")
            return false
        }
        guard let script else {
            lastError = String(localized: "The live terminal’s bridge script is missing. Reinstall Clawd")
            return false
        }
        guard let orca = installation(), let node = orca.executable, let shared = orca.sharedModules else {
            lastError = String(localized: "Can’t find the Orca app")
            return false
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: node)
        p.arguments = [script]
        var env = ProcessInfo.processInfo.environment
        env["ELECTRON_RUN_AS_NODE"] = "1"
        env["ORCA_SHARED_DIR"] = shared
        // As Orca's own CLI launcher does: a user's NODE_OPTIONS must not change how Orca's Node runs.
        env["NODE_OPTIONS"] = nil
        p.environment = env
        let stdin = Pipe(), stdout = Pipe(), stderr = Pipe()
        // Writing after Node died would raise SIGPIPE and kill the app; fail with EPIPE instead.
        _ = fcntl(stdin.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)
        p.standardInput = stdin
        p.standardOutput = stdout
        p.standardError = stderr
        stdout.fileHandleForReading.readabilityHandler = { [weak self] h in
            let chunk = h.availableData
            // At EOF the handler would otherwise fire forever with empty data.
            if chunk.isEmpty { h.readabilityHandler = nil; try? h.close() }
            onMain { self?.receive(chunk, from: p) }
        }
        stderr.fileHandleForReading.readabilityHandler = { [weak self] h in
            let chunk = h.availableData
            if chunk.isEmpty { h.readabilityHandler = nil; try? h.close() }
            onMain { self?.receiveError(chunk, from: p) }
        }
        p.terminationHandler = { [weak self] _ in
            onMain { self?.streamEnded(p) }
        }
        buffer.removeAll()
        stderrTail.removeAll()
        streaming.removeAll()
        ready = false
        pendingEnds = 3
        do { try p.run() } catch {
            lastError = error.localizedDescription
            for pipe in [stdin, stdout, stderr] {
                pipe.fileHandleForReading.readabilityHandler = nil
                try? pipe.fileHandleForReading.close()
                try? pipe.fileHandleForWriting.close()
            }
            return false
        }
        process = p
        input = stdin.fileHandleForWriting
        outputs = [stdout.fileHandleForReading, stderr.fileHandleForReading]
        // The pairing goes over stdin, never argv or the environment, which other processes can read.
        let creds: [String: Any] = ["endpoint": pairing.localEndpoint, "deviceToken": pairing.deviceToken,
                                    "publicKeyB64": pairing.publicKeyB64]
        write(["op": "pairing", "pairing": creds])
        return true
    }

    private func write(_ message: [String: Any]) {
        guard let input, var data = try? JSONSerialization.data(withJSONObject: message) else { return }
        data.append(0x0A)
        try? input.write(contentsOf: data)
    }

    private func receiveError(_ chunk: Data, from p: Process) {
        guard process === p else { return }
        if chunk.isEmpty { streamEnded(p); return }
        stderrTail.append(chunk)
        if stderrTail.count > Self.stderrLimit { stderrTail = stderrTail.suffix(Self.stderrLimit) }
    }

    /// Why the bridge didn't come up, for subscriptions that never got a screen.
    private var startFailure: String {
        let detail = String(decoding: stderrTail, as: UTF8.self).split(whereSeparator: \.isNewline)
            .lazy.map { $0.trimmingCharacters(in: .whitespaces) }.first { !$0.isEmpty }
        if ready { return String(localized: "Orca closed the connection without sending the terminal screen") }
        return detail.map { String(localized: "Couldn’t start the live connection to Orca: \($0)", comment: "%@ is an error message") }
            ?? String(localized: "Couldn’t start the live connection to Orca")
    }

    /// Counts down stdout EOF, stderr EOF and the exit, so every line and the error output are
    /// in before subscriptions are settled.
    private func streamEnded(_ p: Process) {
        guard process === p else { return }
        pendingEnds -= 1
        if pendingEnds <= 0 { endRun(p) }
    }

    /// The bridge is gone: every open subscription ends, as a failure if it never got a screen.
    private func endRun(_ p: Process) {
        guard process === p else { return }
        let failure = startFailure
        if !ready { lastError = failure; Log.error("bridge exited: \(failure)") }
        let open = handlers, streamed = streaming
        teardown()
        for (id, handler) in open { handler(streamed.contains(id) ? .closed : .failed(failure)) }
    }

    private func receive(_ chunk: Data, from p: Process) {
        guard process === p else { return }
        if chunk.isEmpty { streamEnded(p); return }
        buffer.append(chunk)
        while process === p, let newline = buffer.firstIndex(of: 0x0A) {
            let line = buffer[buffer.startIndex..<newline]
            buffer.removeSubrange(buffer.startIndex...newline)
            guard let o = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else { continue }
            switch o["type"] as? String {
            case "ready":
                ready = true
                continue
            case "fatal":
                // The bridge couldn't load Orca's client code (Orca moved, updated or damaged).
                let reason = o["message"] as? String ?? String(localized: "Unknown error")
                let message = String(localized: "Couldn’t start the live connection to Orca: \(reason)", comment: "%@ is an error message")
                lastError = message
                Log.error(message)
                let open = handlers
                teardown()
                for handler in open.values { handler(.failed(message)) }
                return
            default:
                break
            }
            guard let id = o["id"] as? String, let handler = handlers[id] else { continue }
            switch o["type"] as? String {
            case "frame":
                guard let r = o["result"] as? [String: Any] else { continue }
                switch r["type"] as? String {
                case "scrollback":
                    streaming.insert(id)
                    let text = r["serialized"] as? String ?? (r["lines"] as? [String] ?? []).joined(separator: "\r\n")
                    handler(.snapshot(text, cols: r["cols"] as? Int, rows: r["rows"] as? Int))
                case "data":
                    if let chunk = r["chunk"] as? String { handler(.data(chunk)) }
                case "fit-override-changed":
                    if let c = r["cols"] as? Int, let rr = r["rows"] as? Int { handler(.resized(cols: c, rows: rr)) }
                case "end":
                    finish(id, with: .closed)
                default:
                    break
                }
            case "error":
                lastError = o["message"] as? String
                finish(id, with: .failed(lastError ?? String(localized: "Connection error")))
            case "closed":
                finish(id, with: streaming.contains(id) ? .closed : .failed(startFailure))
            default:
                break
            }
        }
    }

    /// A subscription's last event: forget its handler and close it in the bridge too.
    private func finish(_ id: String, with event: Event) {
        guard let handler = handlers[id] else { return }
        remove(id)
        write(["op": "unsubscribe", "id": id])
        handler(event)
    }

    /// Drops a handler; once none are left the idle countdown starts.
    private func remove(_ id: String) {
        handlers[id] = nil
        streaming.remove(id)
        guard handlers.isEmpty else { return }
        idleTimer?.invalidate()
        idleTimer = Timer.scheduledTimer(withTimeInterval: Self.idleShutdown, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {   // scheduled on the main run loop
                if self?.handlers.isEmpty == true { self?.stop() }
            }
        }
    }

    /// Streams a terminal's screen; returns an id for `unsubscribe`, or nil when the bridge
    /// can't start (`lastError` says why).
    func subscribe(terminal handle: String, onEvent: @escaping (Event) -> Void) -> String? {
        idleTimer?.invalidate()
        guard ensureRunning() else { return nil }
        let id = UUID().uuidString
        handlers[id] = onEvent
        write(["op": "subscribe", "id": id, "terminal": handle])
        return id
    }

    func unsubscribe(_ id: String) {
        guard handlers[id] != nil else { return }
        remove(id)
        write(["op": "unsubscribe", "id": id])
    }

    func stop() {
        idleTimer?.invalidate()
        idleTimer = nil
        process?.terminate()
        teardown()
    }

    /// Forgets the current run without notifying anyone; late events from it are ignored.
    private func teardown() {
        handlers.removeAll()
        streaming.removeAll()
        for h in outputs { h.readabilityHandler = nil; try? h.close() }
        outputs = []
        try? input?.close()   // the bridge exits when its stdin closes
        input = nil
        process = nil
        buffer.removeAll()
    }
}
