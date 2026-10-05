import AppKit
import SwiftUI

// MARK: - Chat panel
// Talk to Orca agents from Clawd without switching to Orca: pick an agent,
// read what it said, answer its permission prompt or type a reply.

/// Messages-style layout: agents in a sidebar, the selected agent's exchange on the right.
struct ChatView: View {
    static let size = CGSize(width: 920, height: 640)
    @Bindable var model: ChatModel
    let actions: ChatActions
    let liveTerminal: LiveTerminal
    @FocusState private var typing: Bool
    @State private var dropTargeted = false
    @State private var composerSelection: TextSelection?

    var body: some View {
        // The sidebar sits on the popover's own glass; the conversation on the opaque window
        // background, the way a native split view divides the two.
        HStack(spacing: 0) {
            sidebar
                .padding(.top, 6)
                .frame(width: 240)
            Divider()
            VStack(spacing: 0) {
                if model.connection != .connected {
                    connectionPlaceholder
                } else if let row = model.current {
                    header(row)
                    conversation(row)
                } else {
                    placeholder("No agents running", "Run Claude or Codex in Orca and it shows up here.")
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(.background)
        }
        .frame(width: Self.size.width, height: Self.size.height)
        .onAppear { typing = true }
        .background {
            Button(String()) { actions.close() }.keyboardShortcut("w", modifiers: .command).hidden()
            Button(String()) {
                guard model.current?.agent.hasTerminal == true else { return }
                model.mode = model.mode == .chat ? .terminal : .chat
            }
            .keyboardShortcut("t", modifiers: .command).hidden()
        }
    }

    /// Title and subtitle as a window toolbar would show them, with the view switch and the Orca
    /// button as glass toolbar-style controls (a popover has no toolbar of its own).
    func header(_ row: ChatRow) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 1) {
                Text(row.agent.name).font(.headline).lineLimit(1)
                Text([row.title, row.status].compactMap { $0 }.joined(separator: " · "))
                    .font(.subheadline)
                    .foregroundStyle(row.kind <= .question ? AnyShapeStyle(.orange) : AnyShapeStyle(.secondary))
                    .lineLimit(1)
            }
            Spacer()
            ToolbarControls {
                if row.agent.hasTerminal {
                    Picker("View", selection: $model.mode) {
                        Label("Chat", systemImage: "bubble.left.and.bubble.right").tag(ChatModel.Mode.chat)
                        Label("Terminal", systemImage: "terminal").tag(ChatModel.Mode.terminal)
                    }
                    .pickerStyle(.segmented)
                    .labelStyle(.iconOnly)
                    .labelsHidden()
                    .fixedSize()
                    .help("Switch between chat and terminal (⌘T)")
                }
                Button { actions.open(row.agent) } label: {
                    Label("Open in Orca", systemImage: "arrow.up.forward.app").labelStyle(.iconOnly)
                }
                .help("Open in Orca")
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    // MARK: Sidebar

    var sidebar: some View {
        let groups: [(String, [ChatRow])] = [
            (String(localized: "Needs attention", comment: "Group of agents waiting for the user"), model.rows.filter { $0.kind <= .question }),
            (String(localized: "Working"), model.rows.filter { $0.kind == .working }),
            (String(localized: "Done"), model.rows.filter { $0.kind == .finished }),
        ]
        let resting = model.rows.filter { $0.kind == .resting }
        return List(selection: $model.selected) {
            ForEach(groups, id: \.0) { title, rows in
                if !rows.isEmpty {
                    Section(title) { ForEach(rows) { sidebarRow($0) } }
                }
            }
            if !resting.isEmpty {
                Section(isExpanded: $model.showResting) {
                    ForEach(resting) { sidebarRow($0) }
                } header: {
                    // The whole header toggles, not just the chevron that appears on hover.
                    Text("Resting \(resting.count)", comment: "Sidebar section of idle agents, with their count")
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(.rect)
                        .onTapGesture { withAnimation { model.showResting.toggle() } }
                        .accessibilityAddTraits(.isButton)
                }
            }
        }
        .listStyle(.sidebar)
        .onChange(of: model.selected) { _, _ in typing = true }
    }

    /// Dropped files: pasted into the terminal in the terminal view, otherwise added to the
    /// agent's message as paths.
    @discardableResult
    func drop(_ urls: [URL], on row: ChatRow) -> Bool {
        let text = FileDrop.text(for: urls)
        guard !text.isEmpty, row.agent.hasTerminal else { return false }
        if model.showsTerminal && model.current?.id == row.id {
            actions.key(row.agent, FileDrop.paste(text))
        } else {
            model.drafts[row.id] = FileDrop.append(text, to: model.drafts[row.id] ?? "")
            model.selected = row.id
            model.mode = .chat
            typing = true
        }
        return true
    }

    /// Puts the composer's caret after its text. AppKit selects a field's whole text when it
    /// takes focus; with a saved draft the next keystroke would replace it.
    func caretToEnd() {
        DispatchQueue.main.async {
            composerSelection = TextSelection(insertionPoint: model.draft.endIndex)
        }
    }

    func sidebarRow(_ row: ChatRow) -> some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text(row.agent.name).lineLimit(1)
                Text(row.title ?? row.status).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 4)
            indicator(row.kind)
        }
        .padding(.vertical, 3)
        .tag(row.id)
        // Drop files on an agent to start a message to it with their paths.
        .dropDestination(for: URL.self) { urls, _ in drop(urls, on: row) }
    }

    /// Only states worth a glance get a mark; resting agents stay quiet.
    @ViewBuilder
    func indicator(_ kind: RowKind) -> some View {
        switch kind {
        case .permission, .question:
            Image(systemName: "circle.fill").font(.caption2).foregroundStyle(.orange)
                .accessibilityLabel(kind == .permission ? Text("Permission needed") : Text("Reply needed"))
        case .working: ProgressView().controlSize(.mini).accessibilityLabel("Working")
        case .finished:
            Image(systemName: "checkmark").font(.caption.bold()).foregroundStyle(.green)
                .accessibilityLabel("Done")
        case .resting: EmptyView()
        }
    }

    // MARK: Conversation

    func conversation(_ row: ChatRow) -> some View {
        VStack(spacing: 0) {
            if model.showsTerminal {
                terminal(row)
            } else {
                messages(row)
                composer(row)
            }
        }
        // Esc closes the chat; in the terminal view it belongs to the terminal.
        .onExitCommand { if !model.showsTerminal { actions.close() } }
        .dropDestination(for: URL.self) { urls, _ in
            drop(urls, on: row)
        } isTargeted: { dropTargeted = $0 }
        .overlay { if dropTargeted { DropHighlight(terminal: model.showsTerminal) } }
    }

    @ViewBuilder
    func messages(_ row: ChatRow) -> some View {
        if model.timeline.isEmpty && !model.timelineReady {
            // A fraction of a second while the transcript is read; showing the summary first
            // would flash a different layout.
            Color.clear.frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if model.timeline.isEmpty {
            summaryMessages(row)
        } else {
            timeline(row)
        }
    }

    /// Step-by-step view of the current turn, with the permission dialog or a working
    /// indicator at the end.
    func timeline(_ row: ChatRow) -> some View {
        let runningStep = model.timeline.last.map { item -> Bool in
            if case .tool(_, nil, _) = item.kind { return true }
            return item.kind == .thinking
        } ?? false
        return AgentTimeline(items: model.timeline, agent: row.id, live: row.kind == .working) {
            if row.kind == .permission {
                permissionRequest(row)
            } else if row.kind == .working && !runningStep {
                // Between steps: Claude is writing or about to call the next tool.
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Working").foregroundStyle(.secondary)
                }
            }
        }
    }

    /// Without a transcript (e.g. a Codex agent): the last prompt and final reply Orca reports.
    func summaryMessages(_ row: ChatRow) -> some View {
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    if let ask = row.agent.prompt, !ask.isEmpty {
                        UserBubble(text: ask)
                    }
                    if row.kind == .permission {
                        permissionRequest(row)
                    } else if let reply = row.agent.lastMessage, !reply.isEmpty, row.kind != .working {
                        MarkdownView(reply)
                    } else if row.kind == .working || model.busy {
                        HStack(spacing: 8) {
                            ProgressView().controlSize(.small)
                            Text(row.agent.tool.map { String(localized: "Running \($0)", comment: "%@ is a tool name such as Bash") } ?? String(localized: "Working")).foregroundStyle(.secondary)
                        }
                        .padding(.leading, 4)
                    }
                }
                .padding(20)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .defaultScrollAnchor(.bottom)
            .modifier(SoftScrollEdge())
    }

    /// The agent's terminal, resized to this area and typed into directly.
    @ViewBuilder
    func terminal(_ row: ChatRow) -> some View {
        if model.live {
            VStack(spacing: 0) {
                LiveTerminalView(terminal: liveTerminal)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .overlay {
                        if let status = model.liveStatus {
                            GroupBox { Text(status).foregroundStyle(.secondary) }.fixedSize()
                        }
                    }
            }
        } else {
            approximateTerminal(row)
        }
    }

    /// Before pairing: the plain-text screen, recoloured by pattern.
    func approximateTerminal(_ row: ChatRow) -> some View {
        VStack(spacing: 0) {
            Group {
                switch model.screen {
                case .lines(let lines):
                    TerminalScreen(screen: lines, rows: model.fittedRows)
                case .loading:
                    ProgressView().controlSize(.small)
                case .failed:
                    Text("Can’t read the terminal screen. If the terminal was closed, check in Orca.")
                        .font(.callout).foregroundStyle(.secondary).padding(20)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            // Text still being composed with an input method floats over the screen, so the
            // terminal's size (and the agent's PTY) doesn't change while typing.
            .overlay(alignment: .bottomLeading) {
                if !model.composing.isEmpty {
                    Text(model.composing)
                        .font(.body.monospaced())
                        .padding(.horizontal, 10)
                        .padding(.vertical, 6)
                        .background(.regularMaterial, in: .capsule)
                        .padding(12)
                        .accessibilityLabel(Text("Typing  \(model.composing)", comment: "Text being composed with an input method, before it goes to the terminal"))
                }
            }
            .overlay {
                TerminalKeys(target: row.id,
                             onKeys: { actions.key(row.agent, $0) },
                             onComposing: { model.composing = $0 })
            }
            .background {
                GeometryReader { geo in
                    Color.clear
                        .onAppear { model.viewport = geo.size }
                        .onChange(of: geo.size) { _, size in model.viewport = size }
                }
            }
        }
    }

    /// The permission dialog with its answers when it could be read off the screen; otherwise
    /// what Orca says the agent wants to run, answered in Orca.
    @ViewBuilder
    func permissionRequest(_ row: ChatRow) -> some View {
        if let prompt = model.prompt {
            permission(prompt, row.agent)
        } else {
            GroupBox {
                VStack(alignment: .leading, spacing: 10) {
                    if let tool = row.agent.tool {
                        Text(tool).foregroundStyle(.secondary)
                    }
                    if let input = row.agent.toolInput, !input.isEmpty {
                        Text(input).font(.body.monospaced()).textSelection(.enabled).lineLimit(8)
                    }
                    HStack {
                        Button("Reply in Orca") { actions.open(row.agent) }.glassButton(prominent: true)
                        if model.busy { ProgressView().controlSize(.small) }
                    }
                    .controlSize(.large)
                    .buttonBorderShape(.capsule)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(4)
            } label: {
                Label("Permission request", systemImage: "hand.raised.fill").foregroundStyle(.orange)
            }
        }
    }

    func permission(_ p: PermissionPrompt, _ agent: OrcaAgent) -> some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 10) {
                // The command (or file) reads as code, with what it is for under it.
                if let command = p.command {
                    Text(command)
                        .font(.body.monospaced())
                        .textSelection(.enabled)
                        .lineLimit(6)
                }
                ForEach(Array(p.explanation.enumerated()), id: \.offset) { _, line in
                    Text(line).foregroundStyle(.secondary).textSelection(.enabled)
                }
                HStack {
                    ForEach(p.options) { o in
                        Button(o.title) { actions.answer(agent, o.number) }
                            .keyboardShortcut(o.shortcut.map { KeyboardShortcut(KeyEquivalent($0), modifiers: .command) })
                            .help(o.shortcut == nil ? o.label : "\(o.label)  ⌘\(o.number)")
                            .buttonStyle(AnswerButtonStyle(prominent: o.number == 1))
                    }
                }
                .controlSize(.large)
                .disabled(model.busy)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(4)
        } label: {
            Label(p.detail.first ?? String(localized: "Permission request"), systemImage: "hand.raised.fill").foregroundStyle(.orange)
        }
    }

    func composer(_ row: ChatRow) -> some View {
        // A permission dialog takes only its own answers: typed text would land in it. A session
        // without a terminal takes nothing from Clawd.
        let canType = row.kind != .permission && row.agent.hasTerminal
        let placeholder: String
        if canType { placeholder = row.kind == .question ? String(localized: "Reply") : String(localized: "Message") }
        else if !row.agent.hasTerminal && row.kind != .permission { placeholder = String(localized: "Reply in Orca") }
        else if let prompt = model.prompt { placeholder = String(localized: "Answer with the buttons above or ⌘1–\(prompt.options.count)", comment: "Shortcuts ⌘1 to ⌘n pick a permission answer") }
        else { placeholder = String(localized: "Answer the permission request in Orca") }
        let empty = model.draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        return VStack(alignment: .leading, spacing: 6) {
            if let notice = model.notice {
                Label {
                    Text(notice.text).foregroundStyle(.secondary)
                } icon: {
                    switch notice.tone {
                    case .success: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                    case .info: Image(systemName: "info.circle.fill").foregroundStyle(.secondary)
                    case .failure: Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                    }
                }
                .font(.callout)
                .padding(.leading, 14)
                .transition(.opacity)
                .task(id: notice.id) {
                    guard !notice.lingers else { return }
                    try? await Task.sleep(for: .seconds(4))
                    withAnimation { if model.notice?.id == notice.id { model.notice = nil } }
                }
            }
            GlassComposer {
                TextField(placeholder, text: $model.draft, selection: $composerSelection, axis: .vertical)
                    .lineLimit(1...5)
                    .focused($typing)
                    .onChange(of: typing) { _, focused in if focused { caretToEnd() } }
                    .onChange(of: model.selected) { _, _ in caretToEnd() }
                    .disabled(!canType)
                    // With nothing typed, ↑ and ↓ walk the agents; otherwise they move the cursor.
                    .onKeyPress(.upArrow) { guard model.draft.isEmpty else { return .ignored }; model.move(-1); return .handled }
                    .onKeyPress(.downArrow) { guard model.draft.isEmpty else { return .ignored }; model.move(1); return .handled }
                    .onKeyPress(.return, phases: .down) { press in
                        if press.modifiers.contains(.shift) { return .ignored }   // Shift-Enter: new line
                        // While Korean (or any IME) text is still being composed, Enter only
                        // commits the syllable; sending now would drop the last character.
                        if let editor = NSApp.keyWindow?.firstResponder as? NSTextView, editor.hasMarkedText() {
                            return .ignored
                        }
                        submit(row)
                        return .handled
                    }
            } send: {
                // Like Messages: no greyed-out button on an empty field, it appears once there is
                // something to send.
                if !empty && canType {
                    Button { submit(row) } label: {
                        Label("Send", systemImage: "arrow.up.circle.fill").labelStyle(.iconOnly)
                            .font(.system(size: 26))
                            .symbolRenderingMode(.palette)
                            .foregroundStyle(.white, Color.accentColor)
                    }
                    .disabled(model.busy)
                    .help("Send (Return)")
                    .transition(.scale.combined(with: .opacity))
                }
            }
            .animation(.snappy(duration: 0.2), value: empty)
        }
        .padding(16)
    }

    func placeholder(_ title: LocalizedStringKey, _ body: LocalizedStringKey) -> some View {
        ContentUnavailableView(title, systemImage: "bubble.left.and.bubble.right", description: Text(body))
    }

    /// Why there is nothing to show, and the one thing that fixes it.
    @ViewBuilder
    var connectionPlaceholder: some View {
        switch model.connection {
        case .notRunning:
            ContentUnavailableView {
                Label("Orca isn’t running", systemImage: "moon.zzz")
            } description: {
                Text("Open Orca and your agents show up here.")
            } actions: {
                Button("Open Orca") { actions.launchOrca() }.glassButton(prominent: true)
            }
        case .off:
            ContentUnavailableView {
                Label("Orca integration is off", systemImage: "pause.circle")
            } description: {
                Text("You can turn it back on in Settings.")
            } actions: {
                Button("Open Settings") { actions.settings() }.glassButton(prominent: false)
            }
        case .notInstalled:
            ContentUnavailableView {
                Label("Can’t find Orca", systemImage: "questionmark.app.dashed")
            } description: {
                Text("Clawd shows the coding agents running in Orca. Install Orca and it connects right away.")
            } actions: {
                Link("Download Orca", destination: OrcaInstallation.homepage)
            }
        case .connected:
            EmptyView()
        }
    }

    func submit(_ row: ChatRow) {
        let text = model.draft.trimmingCharacters(in: .whitespacesAndNewlines)
        // Return reaches here even while the send button is disabled; the draft only clears once
        // the send is done, so a second press would send it again.
        guard !text.isEmpty, row.kind != .permission, row.agent.hasTerminal, !model.busy else { return }
        actions.send(row.agent, text)
    }
}

/// Shown while files are dragged over the conversation: where they will go.
struct DropHighlight: View {
    let terminal: Bool

    var body: some View {
        RoundedRectangle(cornerRadius: 12, style: .continuous)
            .strokeBorder(.tint, style: StrokeStyle(lineWidth: 2, dash: [6, 4]))
            .background(.tint.opacity(0.06), in: .rect(cornerRadius: 12, style: .continuous))
            .overlay {
                Label(terminal ? "Drop to paste the paths into the terminal" as LocalizedStringKey : "Drop to add the paths to your message",
                      systemImage: "doc.on.doc")
                    .font(.headline)
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    .background(.regularMaterial, in: .capsule)
            }
            .padding(10)
            .allowsHitTesting(false)
    }
}
