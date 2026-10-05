import AppKit

// MARK: - Chat
// Feeds the chat popover: agent rows, the timeline, the terminal view and its live stream,
// and what the popover asks the app to do.

extension AppDelegate {

    /// Double-click on Clawd: Orca, on whichever agent matters most right now.
    func openOrca() {
        chat.close()
        if let a = pending.first ?? orca.waiting.first {
            open(a)
        } else if let key = chat.model.selected, let a = agent(key) {
            open(a)
        } else {
            orca.launch()
        }
    }

    /// Follows the selected agent's Claude Code transcript. The file is only re-read when it
    /// grew, and the session lookup is cached for a while since panes rarely change session.
    func pollTimeline() {
        guard !readingTranscript else { return }   // the last read is still running
        if let a = chat.model.current?.agent, a.sessionId != nil {
            pollSession(a)
            return
        }
        guard let a = chat.model.current?.agent, a.agentType == "claude" || a.agentType.isEmpty else {
            // Only Claude Code writes a transcript; other agents get the summary view.
            if !chat.model.timeline.isEmpty { chat.model.timeline = [] }
            chat.model.timelineReady = true
            return
        }
        readingTranscript = true
        let key = a.paneKey
        let now = ProcessInfo.processInfo.systemUptime
        let cached = transcripts[key].flatMap { now - $0.found < 10 ? $0.url : nil }
        // Just switched to this agent: its reader may be unchanged since it was last shown.
        let showingNothing = chat.model.timeline.isEmpty
        transcriptQueue.async { [weak self] in
            let url = cached ?? TestHooks.fakeTranscript(for: a.paneKey)
                ?? Transcripts.locate(paneKey: a.paneKey, path: a.path, prompt: a.prompt)
            // Only what was appended since the last read is parsed; nil when nothing changed.
            let reader = url.map(Transcripts.reader(for:))
            let items = reader.flatMap { r -> [TimelineItem]? in
                let read = r.timeline()
                return read.changed || showingNothing ? read.items : nil
            }
            let question = reader?.question
            DispatchQueue.main.async {
                guard let self else { return }
                self.readingTranscript = false
                if let url, cached == nil { self.transcripts[key] = (url, now) }
                if url != nil { self.setQuestion(a.asksQuestion ? question : nil, for: key) }
                guard self.chat.model.current?.id == key else { return }
                self.chat.model.timelineReady = true
                // A lookup that misses once (session file being rewritten) must not blank the
                // view: that re-creates it and throws the reader back to the bottom.
                if url == nil && cached == nil && self.transcripts[key] == nil { self.chat.model.timeline = [] }
                if let items, items != self.chat.model.timeline { self.chat.model.timeline = items }
            }
        }
    }

    /// Follows the selected chat session's history: its latest turn and what it waits on.
    func pollSession(_ a: OrcaAgent) {
        readingTranscript = true
        orca.session(of: a) { [weak self] snapshot in
            guard let self else { return }
            self.readingTranscript = false
            if let snapshot { self.apply(snapshot, to: a.paneKey) }
            guard self.chat.model.current?.id == a.paneKey else { return }
            self.chat.model.timelineReady = true
            if let items = snapshot?.timeline, items != self.chat.model.timeline { self.chat.model.timeline = items }
        }
    }

    /// Keeps what a chat session waits on, so its row, card and answers match it.
    func apply(_ snapshot: SessionSnapshot, to pane: String) {
        approvals[pane] = snapshot.approval
        permissions[pane] = snapshot.approval?.prompt
        setQuestion(snapshot.question, for: pane)
        refreshChat()
    }

    func setQuestion(_ question: AgentQuestion?, for pane: String) {
        guard questions[pane] != question else { return }
        questions[pane] = question
        refreshChat()
    }

    /// Refreshes the terminal view; one read at a time so a slow CLI never piles up.
    func pollScreen() {
        guard !readingScreen, let a = chat.model.current?.agent else { return }
        readingScreen = true
        orca.screen(of: a) { [weak self] lines in
            guard let self else { return }
            self.readingScreen = false
            guard self.chat.model.current?.id == a.paneKey else { return }   // selection moved on
            let next: ChatModel.Screen = lines.map { .lines($0) } ?? .failed
            if next != self.chat.model.screen {
                self.chat.model.screen = next
                self.lastScreenChange = ProcessInfo.processInfo.systemUptime
            }
        }
    }

    func chatKey(_ keys: String, to a: OrcaAgent) {
        orca.sendKeys(keys, to: a)
        screenPollIn = min(screenPollIn, 0.05)   // echo the keystroke right away
        lastScreenChange = ProcessInfo.processInfo.systemUptime
    }

    /// Sizes the shown terminal to the popover while the terminal view is open, and gives it
    /// back to Orca (which restores its own size) as soon as it isn't.
    func updateFit() {
        let model = chat.model
        var target: (agent: OrcaAgent, cols: Int, rows: Int)?
        if chat.isOpen, model.showsTerminal, let agent = model.current?.agent, model.live ? liveSize != nil : model.viewport.width > 100 {
            // Live: the emulator's own grid. Otherwise the cell grid TerminalScreen draws with.
            let grid = model.live ? (liveSize ?? TerminalMetrics.grid(for: model.viewport)) : TerminalMetrics.grid(for: model.viewport)
            target = (agent, grid.cols, grid.rows)
        }
        if let old = fitted, old.agent.paneKey != target?.agent.paneKey {
            orca.restoreSize(old.agent)
            fitted = nil
            model.fittedRows = nil
        }
        if let t = target, fitted == nil || fitted!.cols != t.cols || fitted!.rows != t.rows {
            orca.fit(t.agent, cols: t.cols, rows: t.rows)
            fitted = t
            model.fittedRows = t.rows
            screenPollIn = 0.2
        }
    }

    /// Starts or stops the live terminal stream to match what the popover shows.
    func updateLive() {
        let model = chat.model
        let want = chat.isOpen && model.showsTerminal && model.live ? model.current?.agent : nil
        if let sub = liveSub, sub.pane != want?.paneKey {
            bridge.unsubscribe(sub.id)
            liveSub = nil
            liveAgent = nil
        }
        guard let agent = want, liveSub == nil else { return }
        liveAgent = agent
        liveTerminal.clear()
        let reconnecting = liveReconnect.map { $0.pane == agent.paneKey && ProcessInfo.processInfo.systemUptime < $0.until } ?? false
        model.liveStatus = reconnecting ? String(localized: "Reconnecting to Orca…") : String(localized: "Connecting to Orca…")
        // Each attempt has its own token: switching A → B → A quickly leaves two lookups for A in
        // flight, and only the latest may subscribe, or A's output would be written twice.
        let attempt = "pending-" + UUID().uuidString
        liveSub = (id: attempt, pane: agent.paneKey)
        orca.terminalHandle(for: agent) { [weak self] handle in
            guard let self, self.liveSub?.id == attempt else { return }
            guard let handle, let id = self.bridge.subscribe(terminal: handle, onEvent: { [weak self] event in
                self?.liveEvent(event, pane: agent.paneKey)
            }) else {
                self.liveSub = nil
                if self.retryLive(agent.paneKey) { return }
                // No handle: the terminal wasn't found. No id: the bridge didn't start, and says why.
                model.liveStatus = handle.flatMap { _ in self.bridge.lastError } ?? String(localized: "Couldn’t start the live connection to Orca")
                return
            }
            self.liveSub = (id: id, pane: agent.paneKey)
        }
    }

    func liveEvent(_ event: OrcaBridge.Event, pane: String) {
        guard liveSub?.pane == pane else { return }
        let model = chat.model
        switch event {
        case .snapshot(let text, _, _):
            model.liveStatus = nil
            liveReconnect = nil
            liveTerminal.load(text)
            liveTerminal.focus()
        case .data(let text):
            liveTerminal.feed(text)
        case .resized:
            break
        case .failed(let message):
            liveSub = nil
            forgetLiveHandle(pane)
            // A rejected pairing (revoked in Orca, Orca reinstalled) needs a new code.
            let auth = message.localizedCaseInsensitiveContains("token") || message.localizedCaseInsensitiveContains("unauthorized")
            if !auth && retryLive(pane) { return }
            model.liveStatus = auth ? String(localized: "The Orca connection expired. Pair again in Settings")
                : String(localized: "Live connection error: \(message)", comment: "%@ is an error message")
        case .closed:
            // Orca ended the stream (restarted, the terminal was replaced): say so over the frozen
            // screen and keep trying for a minute. A stream that keeps closing gets one such round
            // every 30 seconds.
            liveSub = nil
            forgetLiveHandle(pane)
            let now = ProcessInfo.processInfo.systemUptime
            guard now - liveClosedAt > 30 else {
                model.liveStatus = String(localized: "Orca closed the live connection")
                return
            }
            liveClosedAt = now
            liveReconnect = (pane, now + Self.liveReconnectFor)
            _ = retryLive(pane)
        }
    }

    static let liveReconnectFor: TimeInterval = 60

    /// The terminal may have been replaced, or Orca restarted: look its handle up afresh.
    func forgetLiveHandle(_ pane: String) {
        if let a = liveAgent, a.paneKey == pane { OrcaClient.forgetHandle(for: a) }
    }

    /// While reconnecting to `pane`, tries again in a moment and returns true; false once the
    /// time for it is up (or there was no reconnect), for the caller to say what went wrong.
    func retryLive(_ pane: String) -> Bool {
        guard let r = liveReconnect, r.pane == pane, ProcessInfo.processInfo.systemUptime < r.until else {
            liveReconnect = nil
            return false
        }
        chat.model.liveStatus = String(localized: "Reconnecting to Orca…")
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
            guard let self, self.liveSub == nil, self.chat.model.current?.id == pane else { return }
            // Orca isn't back yet: without its socket no terminal would be found.
            guard self.orca.running else {
                if !self.retryLive(pane) { self.chat.model.liveStatus = String(localized: "Orca closed the live connection") }
                return
            }
            self.updateLive()
        }
        return true
    }

    /// Asks for an Orca pairing code once; the live terminal needs it.
    @objc func promptPairing() {
        chat.close()
        settingsWindow.show(app: self, pairing: true)
    }

    func completePairing(_ pairing: OrcaPairing) {
        guard bridge.pair(pairing) else {
            say(bridge.lastError ?? String(localized: "Couldn’t connect to Orca"), symbol: "exclamationmark.triangle.fill", for: 5)
            return
        }
        chat.model.live = true
        say(String(localized: "Connected to Orca"), symbol: "link")
    }

    @objc func unpair() {
        if let sub = liveSub { bridge.unsubscribe(sub.id); liveSub = nil }
        bridge.unpair()
        chat.model.live = false
        say(String(localized: "Disconnected the live connection to Orca"), symbol: "link.badge.plus")
    }

    /// Clicking Clawd toggles the chat. A click that just dismissed it must not reopen it.
    func toggleChat() {
        if chat.isOpen { chat.close() } else if !chat.justClosed { openChat() }
    }

    func hotKeyPressed() {
        // Carbon can deliver one press twice; a toggle would then open and close in a blink.
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastHotKey > 0.3 else { return }
        lastHotKey = now
        if chat.isOpen { chat.close() } else { openChat() }
    }

    @objc func statusClicked() {
        let event = NSApp.currentEvent
        if event?.type == .rightMouseUp || event?.modifierFlags.contains(.control) == true {
            // Attach the menu just for this click so left clicks keep opening the chat.
            statusItem.menu = statusMenu
            statusItem.button?.performClick(nil)
            statusItem.menu = nil
        } else if chat.isOpen {
            chat.close()
        } else if !chat.justClosed {
            openChat(fromMenuBar: true)
        }
    }


    /// ⌃⌥J or a click on Clawd: open the chat on `key`, or on whoever needs the user most.
    func openChat(select key: String? = nil, fromMenuBar: Bool = false) {
        let model = chat.model
        model.notice = nil
        refreshChat()
        if let key { model.selected = key }
        else if let debugKey = TestHooks.select { model.selected = debugKey }
        else if let first = pending.first ?? orca.waiting.first { model.selected = first.paneKey }
        else if model.selected == nil || model.current == nil { model.selected = model.rows.first?.id }
        // Open the resting section when there is nothing else to show or a resting agent is picked.
        model.showResting = model.rows.allSatisfy { $0.kind == .resting } || model.current?.kind == .resting
        selectionChanged(model.selected)
        // Agents already waiting when Clawd started were never read: a chat session's questions
        // look like a permission request until they are.
        for a in orca.waiting where a.paneKey != model.selected && (a.sessionId != nil || a.asksQuestion)
            && questions[a.paneKey] == nil && approvals[a.paneKey] == nil {
            loadPrompt(a)
        }
        if let draft = TestHooks.draft { model.draft = draft }
        orca.titles { [weak self] t in
            self?.titles = t
            self?.refreshChat()
        }
        if hidden || fromMenuBar, let button = statusItem.button {
            // AppKit opens popovers anchored to the status bar window upward, off the top of the
            // screen. Anchor to an invisible strip just under the icon instead: with no room
            // above it, the popover has to open downward, arrow pointing at the icon.
            guard let w = button.window else { return }
            let icon = w.convertToScreen(button.convert(button.bounds, to: nil))
            menuAnchor.setFrame(NSRect(x: icon.minX, y: icon.minY - 1, width: icon.width, height: 1), display: false)
            menuAnchor.orderFrontRegardless()
            chat.show(relativeTo: menuAnchor.contentView!.bounds, of: menuAnchor.contentView!, below: true)
        } else {
            // Point at the top of Clawd's head.
            let head = NSRect(x: 5 * pet.scale, y: spriteY * pet.scale, width: 10 * pet.scale, height: 1)
            chat.show(relativeTo: head, of: view)
        }
    }

    func chatRows() -> [ChatRow] {
        func kind(_ a: OrcaAgent) -> RowKind {
            if a.needsYou { return asksPermission(a) ? .permission : .question }
            if a.state == "working" { return .working }
            return finishedAt(a) != nil ? .finished : .resting
        }
        func status(_ a: OrcaAgent, _ k: RowKind) -> String {
            switch k {
            case .permission: return String(localized: "Permission needed · \(ago(since(a)))", comment: "%@ is when it started, e.g. 3 min ago")
            case .question: return String(localized: "Reply needed · \(ago(since(a)))", comment: "%@ is when it started, e.g. 3 min ago")
            case .working: return a.tool.map { String(localized: "Working · \($0)", comment: "%@ is a tool name such as Bash") } ?? String(localized: "Working")
            case .finished: return String(localized: "Done · \(ago(finishedAt(a) ?? Date()))", comment: "%@ is when it finished, e.g. 3 min ago")
            case .resting: return String(localized: "Resting")
            }
        }
        return orca.agents
            .map { a in let k = kind(a); return ChatRow(agent: a, kind: k, title: titles[a.paneKey], status: status(a, k)) }
            .sorted {
                if $0.kind != $1.kind { return $0.kind < $1.kind }
                if $0.kind <= .question, since($0.agent) != since($1.agent) { return since($0.agent) < since($1.agent) }
                if $0.kind == .finished, let f0 = finishedAt($0.agent), let f1 = finishedAt($1.agent), f0 != f1 { return f0 > f1 }
                return $0.agent.name.localizedStandardCompare($1.agent.name) == .orderedAscending
            }
    }

    func refreshChat() {
        let model = chat.model
        model.connection = !orca.available ? .notInstalled : !orca.enabled ? .off : !orca.running ? .notRunning : .connected
        let rows = chatRows()
        if rows != model.rows { model.rows = rows }
        // The selected agent went away (tab closed): move on to the first one, which also
        // moves the live stream and the terminal fit off the dead pane.
        if let selected = model.selected, !rows.contains(where: { $0.id == selected }) {
            model.selected = rows.first?.id
        }
        let prompt = model.current.flatMap { permissions[$0.id] }
        if prompt != model.prompt { model.prompt = prompt }
        let question = model.current.flatMap { $0.agent.needsYou ? questions[$0.id] : nil }
        if question != model.question { model.question = question }
    }

    func selectionChanged(_ key: String?) {
        chat.model.screen = .loading
        chat.model.timeline = []
        chat.model.timelineReady = false
        timelinePollIn = 0
        chat.model.composing = ""
        DispatchQueue.main.async {
            self.updateFit()
            self.updateLive()
        }
        screenPollIn = 0
        refreshChat()
        if let row = chat.model.current, row.agent.needsYou { loadPrompt(row.agent) }
        // Reading a finished agent's reply counts as seeing it: drop it from the menu bar count.
        if let key, finished.removeValue(forKey: key) != nil { updateStatus() }
    }

    /// Reads what the agent is asking: a chat session's pending request or questions, or the
    /// permission dialog on a terminal's screen and Claude's questions in its transcript. `then`
    /// runs once that is known.
    func loadPrompt(_ a: OrcaAgent, then: (() -> Void)? = nil) {
        if a.sessionId != nil {
            chat.model.busyPanes.insert(a.paneKey)
            orca.session(of: a) { [weak self] snapshot in
                guard let self else { return }
                self.chat.model.busyPanes.remove(a.paneKey)
                if let snapshot { self.apply(snapshot, to: a.paneKey) }
                then?()
            }
            return
        }
        guard a.hasTerminal else { then?(); return }
        if a.asksQuestion { loadQuestion(a) }
        chat.model.busyPanes.insert(a.paneKey)
        orca.screen(of: a) { [weak self] lines in
            guard let self else { return }
            self.permissions[a.paneKey] = lines.flatMap(PermissionPrompt.parse)
            self.chat.model.busyPanes.remove(a.paneKey)
            self.refreshChat()
            then?()
        }
    }

    /// Claude's questions from the agent's transcript, which has them in full.
    func loadQuestion(_ a: OrcaAgent) {
        transcriptQueue.async { [weak self] in
            let url = TestHooks.fakeTranscript(for: a.paneKey) ?? Transcripts.locate(paneKey: a.paneKey, path: a.path, prompt: a.prompt)
            let question = url.flatMap { u -> AgentQuestion? in
                let reader = Transcripts.reader(for: u)
                _ = reader.timeline()
                return reader.question
            }
            DispatchQueue.main.async { self?.setQuestion(question, for: a.paneKey) }
        }
    }

    func chatSend(_ text: String, to a: OrcaAgent) {
        let model = chat.model
        model.busyPanes.insert(a.paneKey)
        orca.send(text, to: a) { [weak self] ok in
            guard let self else { return }
            model.busyPanes.remove(a.paneKey)
            if ok {
                model.drafts[a.paneKey] = nil
                self.orca.pollSoon()
                model.notice = .success(String(localized: "Sent to \(a.name)", comment: "%@ is the agent (worktree) name"))
                self.screenPollIn = 0.3
                self.handled(a)
            } else {
                model.notice = .failure(String(localized: "Couldn’t send. Open it in Orca to check"))
            }
        }
    }

    func chatAnswer(_ number: Int, to a: OrcaAgent) {
        let model = chat.model
        guard let prompt = permissions[a.paneKey] else { return }
        let title = prompt.options.first { $0.number == number }?.title ?? String(localized: "Option \(number)", comment: "A permission dialog answer by its number")
        model.busyPanes.insert(a.paneKey)
        let done: @MainActor @Sendable (OrcaWatcher.AnswerResult) -> Void = { [weak self] result in
            guard let self else { return }
            model.busyPanes.remove(a.paneKey)
            self.orca.pollSoon()
            switch result {
            case .sent:
                model.notice = .success("\(a.name): \(title)")
                self.permissions[a.paneKey] = nil
                self.approvals[a.paneKey] = nil
                self.handled(a)
            case .gone:
                // Someone answered it elsewhere (Orca, phone) or it timed out; nothing was sent.
                model.notice = .info(String(localized: "Already answered, so nothing was sent"))
                self.permissions[a.paneKey] = nil
                self.refreshChat()
                self.loadPrompt(a)
            case .failed:
                model.notice = .failure(String(localized: "Couldn’t send the answer. Open it in Orca to check"))
            }
        }
        if let approval = approvals[a.paneKey] {
            orca.approve(number, approval, in: a, done: done)
        } else {
            orca.answer(number, to: a, expecting: prompt, done: done)
        }
    }

    func chatAnswer(_ question: AgentQuestion, _ answers: [AgentQuestion.Answer], to a: OrcaAgent) {
        let model = chat.model
        guard question.complete(answers) else { return }
        model.busyPanes.insert(a.paneKey)
        orca.answer(question, answers, to: a) { [weak self] result in
            guard let self else { return }
            model.busyPanes.remove(a.paneKey)
            self.orca.pollSoon()
            switch result {
            case .sent:
                model.notice = .success(String(localized: "Answered \(a.name)", comment: "%@ is the agent (worktree) name"))
                self.questions[a.paneKey] = nil
                self.handled(a)
            case .gone:
                model.notice = .info(String(localized: "Already answered, so nothing was sent"))
                self.questions[a.paneKey] = nil
                self.refreshChat()
                self.loadPrompt(a)
            case .failed:
                // Part of the answer may have gone in: the dialog is the place to check.
                model.notice = .failure(String(localized: "Couldn’t send the answer. Open it in Orca to check"))
                self.loadPrompt(a)
            }
        }
    }

    /// After answering one agent, move straight on to the next one that is waiting.
    func handled(_ a: OrcaAgent) {
        acknowledged[a.paneKey] = a.state
        notifier.clear(a.paneKey)
        finished[a.paneKey] = nil
        pet.poked()
        updateStatus()
        refreshChat()
        if let next = pending.first(where: { $0.paneKey != a.paneKey }) {
            chat.model.selected = next.paneKey
        }
    }

    /// Files dropped on Clawd: open the chat on whoever needs the user most, with the paths
    /// in the message.
    func dropFiles(_ urls: [URL]) {
        let text = FileDrop.text(for: urls)
        guard !text.isEmpty else { return }
        openChat()
        let model = chat.model
        guard let row = model.current, row.agent.canMessage else { return }
        let id = row.id
        model.mode = .chat
        model.drafts[id] = FileDrop.append(text, to: model.drafts[id] ?? "")
    }
}
