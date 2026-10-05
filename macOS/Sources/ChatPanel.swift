import AppKit
import SwiftUI

// MARK: - Popover

/// The chat lives in a popover that springs from Clawd (or the menu bar icon), closes when
/// you click elsewhere or press Esc, and keeps keyboard focus while open.
final class ChatPanel: NSObject, NSPopoverDelegate {
    let model = ChatModel()
    private let popover = NSPopover()
    private var closedAt: TimeInterval = 0
    var onClose: (() -> Void)?

    init(actions: ChatActions, terminal: LiveTerminal) {
        super.init()
        popover.behavior = .transient
        popover.animates = true
        popover.delegate = self
        popover.contentViewController = NSHostingController(rootView: ChatView(model: model, actions: actions, liveTerminal: terminal))
        // Tell the popover its real size up front. Left at the 320×320 default, it positions itself
        // for that, then grows to fit the SwiftUI content and runs off the top of the screen when
        // opened from the menu bar.
        popover.contentSize = NSSize(width: ChatView.size.width, height: ChatView.size.height)
    }

    var isOpen: Bool { popover.isShown }

    /// True right after the popover closed. A click on Clawd first dismisses the popover and then
    /// arrives as a click; without this it would reopen at once instead of toggling closed.
    var justClosed: Bool { ProcessInfo.processInfo.systemUptime - closedAt < 0.35 }

    /// `below` opens the popover under the anchor (menu bar icon); otherwise above it (Clawd).
    func show(relativeTo rect: NSRect, of view: NSView, below: Bool = false) {
        if popover.isShown {
            popover.positioningRect = rect
        } else {
            // NSRectEdge is in the anchor view's coordinates: Clawd's view is flipped, the menu bar
            // button is not, so the same "minY" would point opposite ways.
            let bottom: NSRectEdge = view.isFlipped ? .maxY : .minY
            let top: NSRectEdge = view.isFlipped ? .minY : .maxY
            popover.show(relativeTo: rect, of: view, preferredEdge: below ? bottom : top)
        }
        // Activate once the popover exists, so there is a window to make key. Opened from the
        // menu bar, the popover only becomes key after the status item's click has finished, so
        // ask again on the next turn of the run loop.
        NSApp.activate()
        popover.contentViewController?.view.window?.makeKey()
        DispatchQueue.main.async { [popover] in
            // Cooperative activation is refused after a click on the menu bar icon, which would
            // leave the popover unable to take typing. Only then fall back to the older call.
            if !NSApp.isActive { NSApp.activate(ignoringOtherApps: true) }
            popover.contentViewController?.view.window?.makeKey()
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [popover] in
            let w = popover.contentViewController?.view.window
            Log.debug("chat shown=\(popover.isShown) active=\(NSApp.isActive) front=\(NSWorkspace.shared.frontmostApplication?.localizedName ?? "-") key=\(w?.isKeyWindow ?? false) responder=\(w?.firstResponder.map { String(describing: type(of: $0)) } ?? "nil")")
        }
    }

    func close() { popover.performClose(nil) }

    func popoverDidClose(_ notification: Notification) {
        closedAt = ProcessInfo.processInfo.systemUptime
        onClose?()
    }
}
