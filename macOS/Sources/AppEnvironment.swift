import AppKit
import os

// MARK: - Identity
// Names derived from the bundle, so renaming the app or its bundle ID is a build setting
// (build.sh), not a hunt through the sources.

nonisolated enum AppIdentity {
    static let bundleID = Bundle.main.bundleIdentifier ?? "com.joonseo1227.clawd-for-orca"
    static let name = Bundle.main.object(forInfoDictionaryKey: "CFBundleName") as? String ?? "Clawd"
    static let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
    static let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String
}

// MARK: - Preferences

/// Every UserDefaults key the app uses, in one place.
nonisolated enum PreferenceKey: String {
    case sound, hidden, orca
    case welcomed   // the first-launch welcome was shown
    case notifications
    case shortcut   // the global "talk to Clawd" shortcut, JSON
}

extension UserDefaults {
    /// Carries settings over from builds that used the bundle ID local.clawd.pet.
    func migrateLegacyPreferences() {
        guard AppIdentity.bundleID != "local.clawd.pet", let old = UserDefaults(suiteName: "local.clawd.pet") else { return }
        for key in [PreferenceKey.sound, .hidden, .orca] where object(forKey: key.rawValue) == nil {
            if let value = old.object(forKey: key.rawValue) { set(value, forKey: key.rawValue) }
        }
    }

    func bool(_ key: PreferenceKey, default value: Bool) -> Bool {
        object(forKey: key.rawValue) as? Bool ?? value
    }
    func set(_ value: Bool, for key: PreferenceKey) { set(value, forKey: key.rawValue) }
}

// MARK: - Orca installation

/// Where Orca is installed and the parts of it Clawd uses. Found through Launch Services by
/// bundle ID, so Orca can live anywhere (/Applications, ~/Applications, a custom folder); the
/// CLI is the one Orca ships inside its bundle, with a PATH lookup as fallback.
nonisolated struct OrcaInstallation: Sendable {
    static let bundleID = "com.stablyai.orca"
    static let homepage = URL(string: "https://www.onorca.dev")!

    let app: URL?
    let cli: String

    /// Orca's Electron binary, which runs Node scripts with ELECTRON_RUN_AS_NODE=1.
    var executable: String? {
        app.flatMap { Bundle(url: $0)?.executablePath }
    }

    /// Orca's own client modules, shared by its CLI and mobile app; the bridge loads them.
    var sharedModules: String? {
        guard let app else { return nil }
        let dir = app.appendingPathComponent("Contents/Resources/app.asar.unpacked/out/shared").path
        return FileManager.default.fileExists(atPath: dir) ? dir : nil
    }

    /// The runtime file the running Orca writes: socket path and auth token.
    static let runtimeMetadata = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/Orca/orca-runtime.json")

    static func locate() -> OrcaInstallation? {
        let fm = FileManager.default
        let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID)
            ?? ["/Applications/Orca.app", "\(NSHomeDirectory())/Applications/Orca.app"]
                .map(URL.init(fileURLWithPath:)).first { fm.fileExists(atPath: $0.path) }
        if let app {
            let bundled = app.appendingPathComponent("Contents/Resources/bin/orca").path
            if fm.isExecutableFile(atPath: bundled) { return OrcaInstallation(app: app, cli: bundled) }
        }
        // A CLI on the PATH (Homebrew or a manual install) still works for agent status.
        let path = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
        for dir in path + Self.extraSearchPath {
            let candidate = (dir as NSString).appendingPathComponent("orca")
            if fm.isExecutableFile(atPath: candidate) { return OrcaInstallation(app: app, cli: candidate) }
        }
        return nil
    }

    /// Apps launched from Finder get a bare PATH; these are where command-line tools live.
    static let extraSearchPath = ["/opt/homebrew/bin", "/usr/local/bin", "\(NSHomeDirectory())/.local/bin"]
}

// MARK: - Diagnostics

/// Unified logging (Console.app, `log stream --predicate 'subsystem == "<bundle id>"'`).
/// With CLAWD_DEBUG set, messages are echoed to stderr as well, for scripted testing.
nonisolated enum Log {
    private static let logger = Logger(subsystem: AppIdentity.bundleID, category: "app")
    /// True with CLAWD_DEBUG set; guards diagnostics that cost something to gather.
    static let verbose = ProcessInfo.processInfo.environment["CLAWD_DEBUG"] != nil

    static func debug(_ message: @autoclosure () -> String) {
        guard verbose else { return }
        let text = message()
        logger.debug("\(text, privacy: .public)")
        FileHandle.standardError.write(Data((text + "\n").utf8))
    }

    static func error(_ message: String) {
        logger.error("\(message, privacy: .public)")
        if verbose { FileHandle.standardError.write(Data(("error: " + message + "\n").utf8)) }
    }
}

/// Environment switches used by scripted UI tests. None of them are read in normal use.
nonisolated enum TestHooks {
    private static let env = ProcessInfo.processInfo.environment
    /// Open the chat in the terminal view.
    static let terminalView = env["CLAWD_TERMINAL_VIEW"] != nil
    /// Show the pairing sheet a second after launch.
    static let showPairing = env["CLAWD_SHOW_PAIRING"] != nil
    /// Select this pane when the chat opens.
    static let select = env["CLAWD_SELECT"]
    /// Pre-fill the composer.
    static let draft = env["CLAWD_DRAFT"]
    /// Replay a saved `orca worktree ps --json` instead of asking Orca.
    static let fakeOrca = env["CLAWD_FAKE_ORCA"]
    /// A folder of transcripts named after panes ("tab:leaf" with ":" as "_"), used instead
    /// of looking up Claude Code sessions. Together with fakeOrca, a complete demo.
    static let fakeTranscripts = env["CLAWD_FAKE_TRANSCRIPTS"].map(URL.init(fileURLWithPath:))

    static func fakeTranscript(for pane: String) -> URL? {
        guard let dir = fakeTranscripts else { return nil }
        let url = dir.appendingPathComponent(pane.replacingOccurrences(of: ":", with: "_") + ".jsonl")
        return FileManager.default.fileExists(atPath: url.path) ? url : nil
    }
    /// A terminal screen for a pane, "<pane>.screen.txt" in the same folder.
    static func fakeScreen(for pane: String) -> [String]? {
        guard let dir = fakeTranscripts else { return nil }
        let url = dir.appendingPathComponent(pane.replacingOccurrences(of: ":", with: "_") + ".screen.txt")
        return (try? String(contentsOf: url, encoding: .utf8))?.components(separatedBy: "\n")
    }
    /// Behave as if not paired (no keychain access), for tests that must not prompt.
    static let noLive = env["CLAWD_NO_LIVE"] != nil
    /// Force light or dark appearance ("light" / "dark"), for screenshots.
    static let appearance = env["CLAWD_APPEARANCE"].flatMap { value -> NSAppearance.Name? in
        value == "light" ? .aqua : value == "dark" ? .darkAqua : nil
    }
    /// Open the chat right after launch: from the menu bar icon, or from Clawd with "pet".
    static let openChat = env["CLAWD_OPEN_CHAT"] != nil
    static let openChatFromPet = env["CLAWD_OPEN_CHAT"] == "pet"
    /// Show the first-launch welcome even if it was seen before.
    static let welcome = env["CLAWD_WELCOME"] != nil
    /// Open the settings window right after launch.
    static let openSettings = env["CLAWD_OPEN_SETTINGS"] != nil
    /// Announce an update found at launch the way later ones are: a card and a menu item.
    static let deferUpdate = env["CLAWD_DEFER_UPDATE"] != nil
}

/// Hops back to the main thread from background work and runs `work` on the main actor.
nonisolated func onMain(_ work: @escaping @MainActor @Sendable () -> Void) {
    DispatchQueue.main.async { MainActor.assumeIsolated(work) }
}
