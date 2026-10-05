import AppKit
import SwiftTerm
import SwiftUI

// MARK: - Live terminal
// A real xterm emulator (SwiftTerm) fed with Orca's terminal stream: colours, backgrounds,
// dim and inverse text, the cursor, wide characters, IME input and selection all behave as in
// a terminal app. Keys typed into it go back to the agent's terminal through Orca.

final class LiveTerminal: NSObject, TerminalViewDelegate {
    let view = TerminalView(frame: NSRect(x: 0, y: 0, width: 600, height: 400))
    var onInput: ((String) -> Void)?
    var onResize: ((Int, Int) -> Void)?
    private var wheelMonitor: Any?
    private var wheelRemainder: CGFloat = 0

    override init() {
        super.init()
        wheelMonitor = NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { [weak self] event in
            self?.wheel(event) == true ? nil : event
        }
        view.terminalDelegate = self
        view.font = TerminalMetrics.liveFont
        // Default-coloured cells stay transparent so the popover's own material shows through.
        view.nativeBackgroundColor = .clear
        view.nativeForegroundColor = .labelColor
        view.caretColor = .labelColor
        view.optionAsMetaKey = true
        view.wantsLayer = true
        view.layer?.backgroundColor = .clear
    }

    /// SwiftTerm 1.11 always scrolls its own scrollback on a wheel event, and its scrollWheel
    /// can't be overridden from outside the module. Full-screen apps such as Claude Code turn on
    /// mouse reporting and scroll themselves, so hand them the wheel as xterm wheel buttons.
    /// Returns true when the event was consumed.
    private func wheel(_ event: NSEvent) -> Bool {
        guard let window = view.window, event.window === window else { return false }
        let point = view.convert(event.locationInWindow, from: nil)
        let terminal = view.getTerminal()
        Log.debug("wheel dy=\(event.scrollingDeltaY) precise=\(event.hasPreciseScrollingDeltas) inside=\(view.bounds.contains(point)) mode=\(terminal.mouseMode)")
        guard view.bounds.contains(point), terminal.mouseMode != .off else { return false }

        // Trackpads send many small precise deltas; turn them into whole wheel clicks.
        wheelRemainder += event.hasPreciseScrollingDeltas ? event.scrollingDeltaY / 12 : event.scrollingDeltaY
        let steps = Int(wheelRemainder)
        guard steps != 0 else { return true }
        wheelRemainder -= CGFloat(steps)

        let col = min(terminal.cols - 1, max(0, Int(point.x / (view.bounds.width / CGFloat(terminal.cols)))))
        let row = min(terminal.rows - 1, max(0, Int((view.bounds.height - point.y) / (view.bounds.height / CGFloat(terminal.rows)))))
        let mods = event.modifierFlags
        let flags = terminal.encodeButton(button: steps > 0 ? 4 : 5, release: false, shift: mods.contains(.shift),
                                          meta: mods.contains(.option), control: mods.contains(.control))
        for _ in 0..<min(abs(steps), 8) { terminal.sendEvent(buttonFlags: flags, x: col, y: row) }
        return true
    }

    var size: (cols: Int, rows: Int) {
        let t = view.getTerminal()
        return (t.cols, t.rows)
    }

    /// A fresh screen from Orca: clear the emulator and replay the serialized buffer.
    func load(_ snapshot: String) {
        view.getTerminal().resetToInitialState()
        view.feed(text: snapshot)
        if Log.verbose {
            let t = view.getTerminal()
            let modes = ["?1049h", "?47h", "?1047h", "?1000h", "?1002h", "?1003h", "?1006h", "?2004h", "?25l"].filter { snapshot.contains($0) }
            Log.debug("snapshot len=\(snapshot.count) modes=\(modes) alt=\(t.isCurrentBufferAlternate) mouse=\(t.mouseMode) rows=\(t.rows) cols=\(t.cols)")
        }
    }

    func feed(_ data: String) { view.feed(text: data) }

    /// Empties the screen, so a terminal that fails to connect never shows the previous one.
    func clear() {
        view.getTerminal().resetToInitialState()
        view.needsDisplay = true
    }

    func focus() { view.window?.makeFirstResponder(view) }

    // MARK: TerminalViewDelegate

    func send(source: TerminalView, data: ArraySlice<UInt8>) {
        Log.debug("send \(data.count) bytes")   // never the content: it's whatever the user types
        onInput?(String(decoding: data, as: UTF8.self))
    }

    func sizeChanged(source: TerminalView, newCols: Int, newRows: Int) {
        onResize?(newCols, newRows)
    }

    // OSC 52 lets terminal output replace the clipboard unasked; the user copies by selecting.
    func clipboardCopy(source: TerminalView, content: Data) {}

    /// Links come from terminal output, so only web and mail links open; file:, custom app
    /// schemes and the like could launch things the user never meant to.
    func requestOpenLink(source: TerminalView, link: String, params: [String: String]) {
        guard let url = URL(string: link), let scheme = url.scheme?.lowercased(),
              ["http", "https", "mailto"].contains(scheme) else { return }
        NSWorkspace.shared.open(url)
    }

    func setTerminalTitle(source: TerminalView, title: String) {}
    func hostCurrentDirectoryUpdate(source: TerminalView, directory: String?) {}
    func scrolled(source: TerminalView, position: Double) {}
    func bell(source: TerminalView) {}
    func iTermContent(source: TerminalView, content: ArraySlice<UInt8>) {}
    func rangeChanged(source: TerminalView, startY: Int, endY: Int) {}
}

/// Hosts the app-owned terminal view so it survives SwiftUI re-renders.
struct LiveTerminalView: NSViewRepresentable {
    let terminal: LiveTerminal

    func makeNSView(context: Context) -> NSView {
        let container = NSView()
        terminal.view.removeFromSuperview()
        terminal.view.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(terminal.view)
        NSLayoutConstraint.activate([
            terminal.view.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: TerminalMetrics.padding),
            terminal.view.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -TerminalMetrics.padding),
            terminal.view.topAnchor.constraint(equalTo: container.topAnchor, constant: TerminalMetrics.padding),
            terminal.view.bottomAnchor.constraint(equalTo: container.bottomAnchor, constant: -TerminalMetrics.padding),
        ])
        DispatchQueue.main.async { terminal.focus() }
        return container
    }

    func updateNSView(_ view: NSView, context: Context) {}
}
