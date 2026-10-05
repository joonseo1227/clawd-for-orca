import Foundation

// MARK: - Runtime socket
// A direct line to the running Orca app: the unix socket and newline-delimited JSON frames that
// the orca CLI itself uses (Orca.app/.../out/cli/runtime/transport.js), minus the Node process
// the CLI starts for every request. About 1 ms per call instead of 200 ms, which is what lets
// the terminal view refresh several times a second.

nonisolated enum OrcaRuntime {
    enum Outcome {
        case ok(Data)
        /// Orca isn't running or the request never left: safe to retry another way.
        case unreachable
        /// Orca got the request and refused it, or the answer never came. Not safe to repeat:
        /// a keystroke may already have been typed.
        case failed(code: String?, message: String?)

        var data: Data? { if case .ok(let d) = self { d } else { nil } }
    }

    private struct Metadata { let endpoint: String; let token: String }

    /// Re-read on every call: the socket path and token change whenever Orca restarts.
    private static func metadata() -> Metadata? {
        guard let data = try? Data(contentsOf: OrcaInstallation.runtimeMetadata),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let token = json["authToken"] as? String,
              let transports = json["transports"] as? [[String: Any]],
              let endpoint = transports.first(where: { $0["kind"] as? String == "unix" })?["endpoint"] as? String
        else { return nil }
        return Metadata(endpoint: endpoint, token: token)
    }

    /// Whether Orca is running, judged by its runtime file and socket.
    static var isRunning: Bool {
        guard let meta = metadata() else { return false }
        return FileManager.default.fileExists(atPath: meta.endpoint)
    }

    static func call(_ method: String, _ params: [String: Any], timeout: Int = 2) -> Data? {
        request(method, params, timeout: timeout).data
    }

    static func request(_ method: String, _ params: [String: Any], timeout: Int = 2) -> Outcome {
        guard let meta = metadata() else { return .unreachable }
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return .unreachable }
        defer { close(fd) }

        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let capacity = MemoryLayout.size(ofValue: addr.sun_path)
        guard meta.endpoint.utf8.count < capacity else { return .unreachable }
        withUnsafeMutablePointer(to: &addr.sun_path) { ptr in
            ptr.withMemoryRebound(to: CChar.self, capacity: capacity) { _ = strncpy($0, meta.endpoint, capacity - 1) }
        }
        var tv = timeval(tv_sec: timeout, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        // A write after Orca hung up must fail with EPIPE, not kill Clawd with SIGPIPE.
        var on: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        let connected = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        guard connected == 0 else { return .unreachable }

        let id = UUID().uuidString.lowercased()
        let request: [String: Any] = ["id": id, "authToken": meta.token, "method": method, "params": params]
        guard var line = try? JSONSerialization.data(withJSONObject: request) else { return .unreachable }
        line.append(0x0A)
        let sent = line.withUnsafeBytes { write(fd, $0.baseAddress, $0.count) }
        guard sent == line.count else { return .unreachable }

        // Frames are newline-terminated; keepalive frames may precede the answer.
        var buffer = Data()
        var chunk = [UInt8](repeating: 0, count: 65536)
        while true {
            let n = read(fd, &chunk, chunk.count)
            guard n > 0 else { return .failed(code: "no_response", message: nil) }
            buffer.append(contentsOf: chunk[0..<n])
            while let newline = buffer.firstIndex(of: 0x0A) {
                let frame = buffer[buffer.startIndex..<newline]
                buffer.removeSubrange(buffer.startIndex...newline)
                guard let json = try? JSONSerialization.jsonObject(with: frame) as? [String: Any] else { continue }
                if json["_keepalive"] != nil { continue }
                guard json["id"] as? String == id else { continue }
                if json["ok"] as? Bool == true { return .ok(Data(frame)) }
                let error = json["error"] as? [String: Any]
                return .failed(code: error?["code"] as? String, message: error?["message"] as? String)
            }
        }
    }

    /// The `result` object of a successful answer.
    static func result(_ data: Data?) -> [String: Any]? {
        guard let data, let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        return json["result"] as? [String: Any]
    }
}

// MARK: - Command line

nonisolated enum Shell {
    /// Runs a program and returns its standard output, or nil when it fails, can't start or
    /// runs past `timeout` (then it is stopped, so a hung CLI never blocks its queue for good).
    static func run(_ path: String, _ args: [String], timeout: TimeInterval = 15) -> Data? {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        // Apps launched from Finder get a bare PATH; the orca script and its Node need more.
        var env = ProcessInfo.processInfo.environment
        env["PATH"] = ([env["PATH"]].compactMap { $0 } + OrcaInstallation.extraSearchPath + ["/usr/bin", "/bin"])
            .joined(separator: ":")
        p.environment = env
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        p.standardInput = FileHandle.nullDevice

        let output = OutputBuffer()
        let finished = DispatchSemaphore(value: 0)
        out.fileHandleForReading.readabilityHandler = { handle in
            let chunk = handle.availableData
            if chunk.isEmpty {
                handle.readabilityHandler = nil
                finished.signal()   // end of output
            } else {
                output.append(chunk)
            }
        }
        do { try p.run() } catch {
            out.fileHandleForReading.readabilityHandler = nil
            return nil
        }
        if finished.wait(timeout: .now() + timeout) == .timedOut {
            out.fileHandleForReading.readabilityHandler = nil
            p.terminate()
            Log.error("\((path as NSString).lastPathComponent) \(args.first ?? "") timed out")
            return nil
        }
        p.waitUntilExit()
        try? out.fileHandleForReading.close()
        return p.terminationStatus == 0 ? output.data : nil
    }

    private final class OutputBuffer: @unchecked Sendable {   // guarded by lock
        private let lock = NSLock()
        private var buffer = Data()
        func append(_ chunk: Data) { lock.withLock { buffer.append(chunk) } }
        var data: Data { lock.withLock { buffer } }
    }
}
