import AppKit
import Carbon.HIToolbox
import ServiceManagement

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    struct Finished {
        var agent: OrcaAgent
        var at: Date
        var took: TimeInterval?
    }

    let pet = Pet(scale: 5)
    var window: NSWindow!
    var view: PetView!
    var snacks: [Snack] = []
    var lastMouse = NSEvent.mouseLocation
    var timer: Timer?
    var paused = false

    let orca = OrcaWatcher()
    let sign = SignBoard()
    var chat: ChatPanel!
    var permissions: [String: PermissionPrompt] = [:]   // pane -> prompt on screen while waiting
    var approvals: [String: SessionApproval] = [:]      // pane -> chat session's pending request
    var questions: [String: AgentQuestion] = [:]        // pane -> Claude's unanswered questions
    var titles: [String: String] = [:]
    let statusMenu = NSMenu()
    var lastHotKey: TimeInterval = 0
    var screenPollIn: CGFloat = 0
    var lastScreenChange: TimeInterval = 0
    var timelinePollIn: CGFloat = 0
    var rowsRefreshIn: CGFloat = 0
    var readingTranscript = false
    var transcripts: [String: (url: URL, found: TimeInterval)] = [:]   // pane -> transcript
    let transcriptQueue = DispatchQueue(label: "clawd.transcript")
    var fitted: (agent: OrcaAgent, cols: Int, rows: Int)?   // terminal currently sized to Clawd
    let bridge = OrcaBridge()
    let liveTerminal = LiveTerminal()
    var liveSub: (id: String, pane: String)?   // terminal stream feeding liveTerminal
    var liveAgent: OrcaAgent?
    var liveSize: (cols: Int, rows: Int)?
    var liveClosedAt: TimeInterval = -.infinity   // when Orca last ended the live stream
    var liveReconnect: (pane: String, until: TimeInterval)?   // retrying after Orca ended it
    var layoutWork: DispatchWorkItem?
    let settingsWindow = SettingsWindow()
    let welcomeWindow = WelcomeWindow()
    let notifier = AgentNotifier()
    let updates = Updates()
    lazy var menuAnchor: NSWindow = {
        let w = NSWindow(contentRect: .zero, styleMask: .borderless, backing: .buffered, defer: false)
        w.isOpaque = false
        w.backgroundColor = .clear
        w.ignoresMouseEvents = true
        w.level = .statusBar
        w.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary]
        return w
    }()
    var readingScreen = false
    var statusItem: NSStatusItem!
    var hotKey: HotKey!
    var pollIn: CGFloat = 0
    var hoverTime: CGFloat = 0
    var hoverDismissed = false   // the hover card was closed; stays closed until the pointer leaves
    var flash: (sign: Sign, until: TimeInterval, action: (() -> Void)?)?   // short-lived cards
    var acknowledged: [String: String] = [:]   // pane -> state the user already opened
    var waitingSince: [String: Date] = [:]
    var lastNudge: [String: Date] = [:]
    var finished: [String: Finished] = [:] // completions the user hasn't looked at yet
    var lastNotificationResponse: TimeInterval = 0

    static let nudgeEvery: TimeInterval = 180

    // MARK: Settings

    let defaults = UserDefaults.standard
    var soundOn: Bool {
        get { defaults.bool(.sound, default: true) }
        set { defaults.set(newValue, for: .sound) }
    }
    var hidden: Bool {
        get { defaults.bool(.hidden, default: false) }
        set { defaults.set(newValue, for: .hidden) }
    }
    var launchAtLogin: Bool { LoginItem.isEnabled }
    var notificationPolicy: AgentNotifier.Policy {
        get { defaults.string(forKey: PreferenceKey.notifications.rawValue).flatMap(AgentNotifier.Policy.init) ?? .whenHidden }
        set { defaults.set(newValue.rawValue, forKey: PreferenceKey.notifications.rawValue) }
    }
    var shouldNotify: Bool {
        switch notificationPolicy {
        case .always: true
        case .whenHidden: hidden
        case .never: false
        }
    }

    /// The usable area of the screen Clawd is on. With no screen at all (a headless Mac, a
    /// moment during display reconfiguration) Clawd stays where it is.
    var screenBounds: NSRect {
        (window.screen ?? NSScreen.main ?? NSScreen.screens.first)?.visibleFrame ?? window.frame
    }

    // MARK: Lifecycle

    func applicationDidFinishLaunching(_ note: Notification) {
        defaults.migrateLegacyPreferences()
        if let name = TestHooks.appearance { NSApp.appearance = NSAppearance(named: name) }
        let size = CGSize(width: canvasW * pet.scale, height: canvasH * pet.scale)
        view = PetView(app: self, size: size)
        window = NSWindow(contentRect: view.frame, styleMask: .borderless, backing: .buffered, defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        window.level = .floating
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary]
        window.contentView = view

        let screen = screenBounds
        pet.pos = CGPoint(x: screen.midX, y: screen.maxY - size.height)
        pet.thrown(.zero)   // drop in from the top of the screen
        window.setFrameOrigin(pet.pos)
        if !hidden { window.orderFrontRegardless() }

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.image = clawdIcon()
        statusItem.button?.setAccessibilityLabel("Clawd")
        statusItem.button?.imagePosition = .imageLeading
        // Left click opens the chat under the icon; right click (or ⌃-click) shows the menu.
        statusMenu.delegate = self
        statusItem.button?.target = self
        statusItem.button?.action = #selector(statusClicked)
        statusItem.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        updateStatus()

        hotKey = HotKey { [weak self] in self?.hotKeyPressed() }
        if !setShortcut(Shortcut.saved) {
            // Another app owns the shortcut; the menu bar icon and Clawd still open the chat.
            say(String(localized: "Another app is using \(Shortcut.saved.display). You can change it in Settings.", comment: "%@ is a keyboard shortcut such as ⌥Space"), symbol: "keyboard", for: 6)
        }

        orca.enabled = defaults.bool(.orca, default: true)
        orca.onChange = { [weak self] old, new in self?.orcaChanged(from: old, to: new) }
        orca.onRunningChange = { [weak self] _ in
            self?.updateStatus()
            self?.refreshChat()
        }
        handleTermination()
        notifier.start()
        notifier.onResponse = { [weak self] pane, action, text in self?.notificationResponse(pane, action, text) }
        updates.onAvailable = { [weak self] version in self?.announceUpdate(version) }
        updates.start()
        chat = ChatPanel(actions: ChatActions(
            send: { [weak self] a, text in self?.chatSend(text, to: a) },
            answer: { [weak self] a, n in self?.chatAnswer(n, to: a) },
            answerQuestion: { [weak self] a, q, answers in self?.chatAnswer(q, answers, to: a) },
            key: { [weak self] a, keys in self?.chatKey(keys, to: a) },
            open: { [weak self] a in
                self?.open(a)
                self?.chat.close()
            },
            close: { [weak self] in self?.chat.close() },
            launchOrca: { [weak self] in self?.orca.launch() },
            settings: { [weak self] in self?.openSettings() }), terminal: liveTerminal)
        chat.model.live = bridge.isPaired
        liveTerminal.onInput = { [weak self] keys in
            guard let self, let a = self.liveAgent else { return }
            self.orca.sendKeys(keys, to: a)
        }
        liveTerminal.onResize = { [weak self] cols, rows in
            self?.liveSize = (cols, rows)
            self?.updateFit()
        }
        chat.onClose = { [weak self] in
            self?.menuAnchor.orderOut(nil)
            self?.chat.model.notice = nil
            self?.updateFit()
            self?.updateLive()
        }
        // Re-fit when the view switches mode or the terminal area changes size; a short delay lets
        // a window resize settle before the PTY is resized.
        chat.model.onLayoutChange = { [weak self] in
            self?.layoutWork?.cancel()
            let work = DispatchWorkItem { [weak self] in
                self?.updateFit()
                self?.updateLive()
            }
            self?.layoutWork = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.15, execute: work)
        }
        // Deferred: the change arrives mid view-update, and handling it updates the model again.
        chat.model.onSelect = { [weak self] key in
            DispatchQueue.main.async { self?.selectionChanged(key) }
        }
        // Test hooks: open straight into the terminal view, or straight into pairing.
        if TestHooks.terminalView { chat.model.mode = .terminal }
        if TestHooks.showPairing {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.promptPairing() }
        }
        if TestHooks.openChat {
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { self.openChat(fromMenuBar: !TestHooks.openChatFromPet) }
        }
        if TestHooks.welcome || !defaults.bool(.welcomed, default: false) {
            defaults.set(true, for: .welcomed)
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.welcomeWindow.show(app: self) }
        }
        if TestHooks.openSettings {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.openSettings() }
        }

        scheduleFrames()
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.keepOnScreen() }
        }
    }

    /// Opening Clawd again while it runs (Finder, Spotlight, Launchpad): there is no window to
    /// bring forward, so show the chat as the menu bar icon would.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        // Choosing "Open in Orca" on a notification can arrive as a reopen too, in either order
        // with the notification's own action, which must win.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in
            guard let self, !self.chat.isOpen,
                  ProcessInfo.processInfo.systemUptime - self.lastNotificationResponse > 1 else { return }
            self.openChat()
        }
        return false
    }

    /// When the last frame ran; frames advance by the real time elapsed (see PetWindow.swift).
    var lastFrame = ProcessInfo.processInfo.systemUptime

    // MARK: Termination

    func applicationWillTerminate(_ notification: Notification) {
        cleanUpBeforeExit()
    }

    /// Give a resized terminal back to Orca and stop the bridge.
    func cleanUpBeforeExit() {
        bridge.stop()
        if let f = fitted { orca.restoreSize(f.agent, wait: true) }
    }

    /// `kill` or logging out sends SIGTERM, which skips applicationWillTerminate. Catch it so a
    /// terminal never stays shrunk to Clawd's size after Clawd is gone.
    var termSource: DispatchSourceSignal?
    func handleTermination() {
        signal(SIGTERM, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        // Not NSApp.terminate: an open sheet or modal would postpone it indefinitely.
        source.setEventHandler { [weak self] in
            MainActor.assumeIsolated {
                self?.cleanUpBeforeExit()
                exit(0)
            }
        }
        source.resume()
        termSource = source
    }

    // MARK: Preferences

    /// Saves and registers the global shortcut; false when the combination is taken.
    @discardableResult
    func setShortcut(_ shortcut: Shortcut) -> Bool {
        Shortcut.saved = shortcut
        return hotKey.register(shortcut)
    }

    @objc func toggleHidden() {
        hidden.toggle()
        if hidden { window.orderOut(nil); sign.set(nil) } else { window.orderFrontRegardless() }
    }

    @objc func toggleSound() { soundOn.toggle() }

    @objc func toggleOrca() {
        if orca.enabled {
            orca.stop()
            waitingSince = [:]; lastNudge = [:]; finished = [:]; permissions = [:]; approvals = [:]; questions = [:]; acknowledged = [:]
            pet.workActivities = []
            say(String(localized: "Orca integration turned off"), symbol: "pause.circle.fill")
        } else {
            orca.enabled = true
            pollIn = 0
            say(String(localized: "Orca integration turned on"), symbol: "play.circle.fill")
        }
        defaults.set(orca.enabled, for: .orca)
        updateStatus()
        refreshChat()
    }

    @objc func toggleLogin() {
        switch LoginItem.set(!LoginItem.isEnabled) {
        case .enabled: say(String(localized: "Clawd will open when you log in"), symbol: "checkmark.circle.fill")
        case .disabled: say(String(localized: "Open at login turned off"), symbol: "xmark.circle.fill")
        case .needsApproval:
            say(String(localized: "Allow Clawd in System Settings > Login Items"), symbol: "exclamationmark.triangle.fill", for: 5)
            SMAppService.openSystemSettingsLoginItems()
        case .failed(let message): say(String(localized: "Couldn’t change the login item: \(message)", comment: "%@ is the system error message"), symbol: "exclamationmark.triangle.fill", for: 4)
        }
    }
}
