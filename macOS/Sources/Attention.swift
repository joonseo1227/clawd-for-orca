import AppKit

// MARK: - Attention
// Which agents need the user, what Clawd shows about them, and when it nudges.

extension AppDelegate {

    /// The user is already looking at this agent in Orca, so don't nag.
    func looking(at a: OrcaAgent) -> Bool {
        NSWorkspace.shared.frontmostApplication?.bundleIdentifier == OrcaInstallation.bundleID && a.worktreeActive
    }

    /// Waiting agents the user hasn't seen yet, longest wait first.
    var pending: [OrcaAgent] {
        orca.waiting
            .filter { !looking(at: $0) && acknowledged[$0.paneKey] != $0.state }
            .sorted { since($0) < since($1) }
    }

    /// When the agent entered its current state: as seen by Clawd, or as Orca reports it for
    /// agents that were already waiting when Clawd started.
    func since(_ a: OrcaAgent) -> Date {
        waitingSince[a.paneKey] ?? a.stateStartedAt ?? Date()
    }

    /// Completions worth listing: seen happening, or reported by Orca as recent.
    func finishedAt(_ a: OrcaAgent) -> Date? {
        if let f = finished[a.paneKey] { return f.at }
        guard a.state == "done", let at = a.stateStartedAt, Date().timeIntervalSince(at) < Self.recentFinish else { return nil }
        return acknowledged[a.paneKey] == a.state ? nil : at
    }

    static let recentFinish: TimeInterval = 3600

    func orcaChanged(from old: [OrcaAgent], to new: [OrcaAgent]) {
        let now = Date()
        let before = Dictionary(old.map { ($0.paneKey, $0) }, uniquingKeysWith: { a, _ in a })
        for a in new {
            let prev = before[a.paneKey]
            if looking(at: a) || a.state == "working" { finished[a.paneKey] = nil }
            if prev?.state == a.state { continue }
            acknowledged[a.paneKey] = nil
            permissions[a.paneKey] = nil
            if a.needsYou {
                waitingSince[a.paneKey] = now
                lastNudge[a.paneKey] = now
                // Orca reports a terminal's permission dialog as "waiting", like a question; a
                // question's notification takes a reply, which would be typed into the dialog. So
                // the notification waits for the screen read.
                let notify = !looking(at: a) && shouldNotify
                loadPrompt(a) { [weak self] in
                    guard let self, notify else { return }
                    let permission = self.permissions[a.paneKey] != nil || a.state == "blocked"
                    self.notifier.post(permission ? .permission : .question, agent: a,
                                       title: permission ? String(localized: "Permission needed") : String(localized: "Reply needed"), body: a.ask, sound: self.soundOn)
                }
                if !looking(at: a) { nudge() }
            } else if prev?.state == "working" && a.state == "done" && !looking(at: a) {
                let took = prev?.stateStartedAt.map { now.timeIntervalSince($0) }
                finished[a.paneKey] = Finished(agent: a, at: now, took: took)
                announceDone(a, took: took)
            }
        }

        let live = Set(new.filter(\.needsYou).map(\.paneKey))
        waitingSince = waitingSince.filter { live.contains($0.key) }
        lastNudge = lastNudge.filter { live.contains($0.key) }
        finished = finished.filter { now.timeIntervalSince($0.value.at) < 3600 }

        // The card stays up on its own; every few minutes Clawd also hops and chimes again.
        for a in pending {
            if let last = lastNudge[a.paneKey], now.timeIntervalSince(last) > Self.nudgeEvery {
                lastNudge[a.paneKey] = now
                nudge()
            }
        }
        updateStatus()
        refreshChat()
    }

    func nudge() {
        pet.alert()
        if soundOn { NSSound(named: "Glass")?.play() }
    }

    func announceDone(_ a: OrcaAgent, took: TimeInterval?) {
        // A session in Orca's chat can only be read from Clawd, not talked to.
        let hint = a.hasTerminal
            ? took.map { String(localized: "Took \(duration($0)) · Click to keep talking", comment: "%@ is how long the task took, e.g. 3 min") }
                ?? String(localized: "Click to keep talking")
            : took.map { String(localized: "Took \(duration($0)) · Click to view", comment: "%@ is how long the task took, e.g. 3 min") }
                ?? String(localized: "Click to view")
        let card = Sign(tone: .done, symbol: "checkmark", title: String(localized: "Task complete"), name: a.name,
                        detail: snippet(a.lastMessage, 160), hint: hint)
        flash = (card, ProcessInfo.processInfo.systemUptime + 20, { [weak self] in self?.openChat(select: a.paneKey) })
        pet.celebrate()
        if soundOn { NSSound(named: "Pop")?.play() }
        if shouldNotify {
            notifier.post(.done, agent: a, title: String(localized: "Task complete"), body: snippet(a.lastMessage, 200), sound: false)
        }
    }

    /// A short confirmation card.
    func say(_ text: String, symbol: String = "info.circle.fill", for seconds: TimeInterval = 2.5) {
        flash = (Sign(tone: .info, symbol: symbol, title: text), ProcessInfo.processInfo.systemUptime + seconds, nil)
    }

    /// Picks the one card worth showing right now: waiting agents beat news beat the hover summary.
    func refreshSign(hovering: Bool) {
        if hidden { sign.set(nil); return }
        let waiting = pending
        if let first = waiting.first {
            // Orca reports permission dialogs as "waiting" too; the screen tells them apart.
            let prompt = permissions[first.paneKey]
            let isPermission = prompt != nil || first.state == "blocked"
            let waited = Date().timeIntervalSince(since(first))
            var hint: String
            if first.hasTerminal {
                hint = waited < 60 ? String(localized: "Just started waiting · Click to reply")
                    : String(localized: "Waiting for \(duration(waited)) · Click to reply", comment: "%@ is a duration, e.g. 3 min")
            } else {
                // Answered in Orca; the click shows what it asks.
                hint = waited < 60 ? String(localized: "Just started waiting · Click to view")
                    : String(localized: "Waiting for \(duration(waited)) · Click to view", comment: "%@ is a duration, e.g. 3 min")
            }
            if waiting.count > 1 {
                hint = String(localized: "+\(waiting.count - 1) more · \(hint)", comment: "More waiting agents, then the hint for the first one")
            }
            let card = Sign(tone: .urgent, symbol: isPermission ? "hand.raised.fill" : "bubble.left.fill",
                            title: isPermission ? String(localized: "Permission needed") : String(localized: "Reply needed"), name: first.name,
                            detail: prompt?.detail.last ?? first.ask, hint: hint)
            sign.set(card, action: { [weak self] in self?.openChat(select: first.paneKey) }, onClose: { [weak self] in
                // Seen it: no card or nagging for this request until the agent's state changes.
                for a in waiting { self?.acknowledged[a.paneKey] = a.state }
                self?.updateStatus()
            })
            return
        }
        if let f = flash, ProcessInfo.processInfo.systemUptime < f.until {
            sign.set(f.sign, action: f.action, onClose: { [weak self] in self?.flash = nil })
            return
        }
        flash = nil
        sign.set(hovering ? summaryCard() : nil, action: hovering ? { [weak self] in self?.openChat() } : nil,
                 onClose: { [weak self] in self?.hoverDismissed = true })
    }

    func summaryCard() -> Sign {
        guard orca.available else { return Sign(tone: .info, symbol: "exclamationmark.triangle", title: String(localized: "Can’t find Orca")) }
        guard orca.enabled else { return Sign(tone: .info, symbol: "pause.circle", title: String(localized: "Orca integration off")) }
        var lines = orca.working.prefix(4).map { $0.name + ($0.tool.map { "  \($0)" } ?? "") }
        lines += finished.values.sorted { $0.at > $1.at }.prefix(3).map { String(localized: "\($0.agent.name)  finished \(ago($0.at))", comment: "Agent name, then when it finished, e.g. just now or 3 min ago") }
        let working = orca.working.count
        if lines.isEmpty { return Sign(tone: .info, symbol: "moon.zzz", title: String(localized: "Everyone’s resting")) }
        return Sign(tone: .info, symbol: working > 0 ? "hammer" : "checkmark.circle",
                    title: working > 0 ? String(localized: "\(working) working", comment: "Number of agents working") : String(localized: "Recently finished"),
                    detail: lines.joined(separator: "\n"))
    }

    /// Menu bar shows only the most pressing state.
    func updateStatus() {
        var title = ""
        if orca.enabled {
            if !orca.waiting.isEmpty { title = String(localized: "\(orca.waiting.count) waiting", comment: "Menu bar: agents waiting for the user") }
            else if !orca.working.isEmpty { title = String(localized: "\(orca.working.count) running", comment: "Menu bar: agents working") }
            else if !finished.isEmpty { title = String(localized: "\(finished.count) done", comment: "Menu bar: agents that finished") }
        }
        statusItem.button?.title = title.isEmpty ? "" : " " + title
    }

    // MARK: Actions

    func open(_ a: OrcaAgent) {
        orca.focus(a)
        notifier.clear(a.paneKey)
        acknowledged[a.paneKey] = a.state
        finished[a.paneKey] = nil
        updateStatus()
    }

    /// A notification was answered: reply in place, or open the agent in Clawd or Orca.
    func notificationResponse(_ pane: String, _ action: AgentNotifier.Action, _ text: String?) {
        lastNotificationResponse = ProcessInfo.processInfo.systemUptime
        guard let a = agent(pane) else { return }
        switch action {
        case .reply:
            let message = text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard !message.isEmpty else { return }
            orca.send(message, to: a) { [weak self] ok in
                guard let self else { return }
                if ok {
                    self.handled(a)
                } else {
                    self.notifier.post(.question, agent: a, title: String(localized: "Couldn’t send"),
                                       body: String(localized: "Open it in Orca to check"), sound: false)
                }
            }
        case .open:
            open(a)
        case .show:
            openChat(select: a.paneKey, fromMenuBar: hidden)
        }
    }
}
