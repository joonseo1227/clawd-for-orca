import AppKit
import SwiftUI

// MARK: - Terminal keyboard
// In terminal mode the keyboard belongs to the agent's terminal. Keys go through the input
// system (so Korean and other IMEs compose properly) and come out as the bytes a terminal
// would send.

final class TerminalKeyView: NSView, NSTextInputClient {
    var onKeys: ((String) -> Void)?
    var onComposing: ((String) -> Void)?
    var target = ""
    private var marked = NSMutableAttributedString()

    func discardComposition() {
        guard hasMarkedText() else { return }
        marked = NSMutableAttributedString()
        inputContext?.discardMarkedText()
        onComposing?("")
    }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) { window?.makeFirstResponder(self) }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        DispatchQueue.main.async { [weak self] in self?.window?.makeFirstResponder(self) }
    }

    override func keyDown(with event: NSEvent) {
        Log.debug("keyDown \(event.keyCode)")
        let mods = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        // Ctrl-letter: send the control character (Ctrl-C interrupts, Ctrl-D ends, ...).
        if mods.contains(.control), !hasMarkedText(), let ch = event.charactersIgnoringModifiers?.lowercased().unicodeScalars.first,
           ("a"..."z").contains(Character(ch)) || "[\\]".unicodeScalars.contains(ch) {
            onKeys?(String(UnicodeScalar(UInt8(ch.value & 0x1f))))
            return
        }
        // Shift/Option-Return is a newline inside Claude Code's prompt rather than a submit.
        if event.keyCode == 36, !mods.isDisjoint(with: [.shift, .option]), !hasMarkedText() {
            onKeys?("\u{1b}\r")
            return
        }
        interpretKeyEvents([event])
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        let mods = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        if mods == .command, event.charactersIgnoringModifiers == "v",
           let text = NSPasteboard.general.string(forType: .string), !text.isEmpty {
            // Bracketed paste: a pasted newline must not submit the prompt halfway.
            onKeys?("\u{1b}[200~" + text + "\u{1b}[201~")
            return true
        }
        return super.performKeyEquivalent(with: event)
    }

    override func doCommand(by selector: Selector) {
        let map: [Selector: String] = [
            #selector(insertNewline(_:)): "\r",
            #selector(insertTab(_:)): "\t",
            #selector(insertBacktab(_:)): "\u{1b}[Z",
            #selector(deleteBackward(_:)): "\u{7f}",
            #selector(deleteForward(_:)): "\u{1b}[3~",
            #selector(moveUp(_:)): "\u{1b}[A",
            #selector(moveDown(_:)): "\u{1b}[B",
            #selector(moveRight(_:)): "\u{1b}[C",
            #selector(moveLeft(_:)): "\u{1b}[D",
            #selector(cancelOperation(_:)): "\u{1b}",
            #selector(moveToBeginningOfLine(_:)): "\u{01}",
            #selector(moveToEndOfLine(_:)): "\u{05}",
            #selector(moveToLeftEndOfLine(_:)): "\u{01}",
            #selector(moveToRightEndOfLine(_:)): "\u{05}",
            #selector(deleteToBeginningOfLine(_:)): "\u{15}",
            #selector(moveWordLeft(_:)): "\u{1b}b",
            #selector(moveWordRight(_:)): "\u{1b}f",
            #selector(deleteWordBackward(_:)): "\u{17}",
            #selector(scrollPageUp(_:)): "\u{1b}[5~",
            #selector(scrollPageDown(_:)): "\u{1b}[6~",
            #selector(pageUp(_:)): "\u{1b}[5~",
            #selector(pageDown(_:)): "\u{1b}[6~",
        ]
        if let keys = map[selector] { onKeys?(keys) }
    }

    // MARK: NSTextInputClient

    func insertText(_ string: Any, replacementRange: NSRange) {
        let text = (string as? NSAttributedString)?.string ?? (string as? String) ?? ""
        marked = NSMutableAttributedString()
        onComposing?("")
        if !text.isEmpty { onKeys?(text) }
    }

    func setMarkedText(_ string: Any, selectedRange: NSRange, replacementRange: NSRange) {
        marked = NSMutableAttributedString(string: (string as? NSAttributedString)?.string ?? (string as? String) ?? "")
        onComposing?(marked.string)
    }

    func unmarkText() {
        let text = marked.string
        marked = NSMutableAttributedString()
        onComposing?("")
        if !text.isEmpty { onKeys?(text) }
    }

    func hasMarkedText() -> Bool { marked.length > 0 }
    func markedRange() -> NSRange { hasMarkedText() ? NSRange(location: 0, length: marked.length) : NSRange(location: NSNotFound, length: 0) }
    func selectedRange() -> NSRange { NSRange(location: marked.length, length: 0) }
    func validAttributesForMarkedText() -> [NSAttributedString.Key] { [] }
    func attributedSubstring(forProposedRange range: NSRange, actualRange: NSRangePointer?) -> NSAttributedString? { nil }
    func characterIndex(for point: NSPoint) -> Int { 0 }

    /// Where the IME candidate window should appear: bottom-left of the terminal.
    func firstRect(forCharacterRange range: NSRange, actualRange: NSRangePointer?) -> NSRect {
        guard let window else { return .zero }
        return window.convertToScreen(convert(NSRect(x: 12, y: 12, width: 1, height: 16), to: nil))
    }
}

struct TerminalKeys: NSViewRepresentable {
    /// The agent the keys go to. When it changes, a half-composed syllable is dropped rather
    /// than committed to the newly selected agent.
    var target: String
    var onKeys: (String) -> Void
    var onComposing: (String) -> Void

    func makeNSView(context: Context) -> TerminalKeyView {
        let v = TerminalKeyView()
        v.target = target
        v.onKeys = onKeys
        v.onComposing = onComposing
        return v
    }

    func updateNSView(_ v: TerminalKeyView, context: Context) {
        if v.target != target {
            v.discardComposition()
            v.target = target
        }
        v.onKeys = onKeys
        v.onComposing = onComposing
    }
}
