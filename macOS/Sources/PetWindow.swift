import AppKit

// MARK: - Window

final class PetView: NSView, NSMenuDelegate {
    unowned let app: AppDelegate
    var pet: Pet { app.pet }
    var grab: NSPoint?
    var dragged = false
    var tracker = DragTracker()

    init(app: AppDelegate, size: CGSize) {
        self.app = app
        super.init(frame: NSRect(origin: .zero, size: size))
        registerForDraggedTypes([.fileURL])
        setAccessibilityElement(true)
        setAccessibilityRole(.button)
        setAccessibilityLabel("Clawd")
        setAccessibilityHelp(String(localized: "Click to chat with your agents. Double-click to open Orca."))
    }

    override func accessibilityPerformPress() -> Bool {
        app.toggleChat()
        return true
    }
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        ctx.clear(bounds)
        ctx.interpolationQuality = .none
        ctx.scaleBy(x: pet.scale, y: pet.scale)
        render(ctx, pose: pet.pose, effects: pet.effects)
    }

    override func mouseDown(with event: NSEvent) {
        dragged = false
        grab = event.locationInWindow
        tracker = DragTracker()
    }

    /// A click that wobbles a little is still a click; only a real move picks Clawd up.
    static let dragThreshold: CGFloat = 4

    override func mouseDragged(with event: NSEvent) {
        guard let grab else { return }
        if !dragged {
            let moved = hypot(event.locationInWindow.x - grab.x, event.locationInWindow.y - grab.y)
            guard moved >= Self.dragThreshold else { return }
            dragged = true
            pet.grabbed()
        }
        let m = NSEvent.mouseLocation
        pet.pos = CGPoint(x: m.x - grab.x, y: m.y - grab.y)
        tracker.add(pet.pos)
        window?.setFrameOrigin(pet.pos)
    }

    override func mouseUp(with event: NSEvent) {
        grab = nil
        if dragged {
            pet.thrown(tracker.velocity())
        } else if event.clickCount == 2 {
            // The first click of the pair already toggled the chat; the second goes to Orca.
            app.openOrca()
        } else {
            // While an agent is waiting, a click takes you straight to it.
            app.toggleChat()
            pet.poked()
        }
    }

    // MARK: File drops

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        guard fileURLs(sender) != nil else { return [] }
        pet.poked()   // a little hop: Clawd noticed
        return .copy
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard let urls = fileURLs(sender) else { return false }
        app.dropFiles(urls)
        return true
    }

    private func fileURLs(_ sender: NSDraggingInfo) -> [URL]? {
        let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self],
                                                          options: [.urlReadingFileURLsOnly: true]) as? [URL]
        return urls?.isEmpty == false ? urls : nil
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let menu = app.petMenu()
        menu.delegate = self
        return menu
    }

    // Clawd holds still while the menu is open so it doesn't wander off from under it.
    func menuWillOpen(_ menu: NSMenu) { app.paused = true }
    func menuDidClose(_ menu: NSMenu) { app.paused = false }
}

// MARK: - Frame clock

extension AppDelegate {

    /// Frames per second: smooth while Clawd moves, a trickle while it sleeps, and just enough
    /// to keep polling Orca while it is hidden, so an idle Clawd costs next to nothing.
    var frameRate: Double {
        if chat.isOpen { return 30 }   // the popover follows the agent live
        if hidden { return snacks.allSatisfy(\.grounded) ? 4 : 30 }
        if pet.state == .sleep && pet.effects.isEmpty { return 8 }
        // Standing about only breathes and blinks; pixel art moves in whole pixels anyway.
        if (pet.state == .idle || pet.state == .hold) && pet.effects.isEmpty && snacks.isEmpty { return 15 }
        return 30
    }

    func scheduleFrames() {
        timer?.invalidate()
        let rate = frameRate
        let timer = Timer(timeInterval: 1 / rate, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick(rate: rate) }   // added to the main run loop below
        }
        timer.tolerance = 0.2 / rate   // lets macOS batch wakeups
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func tick(rate: Double) {
        let now = ProcessInfo.processInfo.systemUptime
        // Real elapsed time, capped so a stall (sleep, a busy main thread) doesn't teleport Clawd.
        let dt = CGFloat(min(now - lastFrame, 0.3))
        lastFrame = now
        frame(dt: dt)
        if frameRate != rate { scheduleFrames() }
    }

    /// Displays changed (one unplugged, resolution switched): bring Clawd back into view.
    func keepOnScreen() {
        let b = screenBounds
        let size = window.frame.size
        pet.pos.x = min(max(pet.pos.x, b.minX), b.maxX - size.width)
        pet.pos.y = min(max(pet.pos.y, pet.ground(Env(bounds: b, size: size, mouse: .zero, mouseSpeed: 0, snacks: []))), b.maxY - size.height)
        window.setFrameOrigin(pet.pos)
    }

    func frame(dt: CGFloat) {
        pollIn -= dt
        if pollIn <= 0 { pollIn = 2; orca.poll() }
        // "1 min ago" in the open chat moves on even while no agent changes; rows that read the
        // same as before change nothing.
        rowsRefreshIn -= dt
        if chat.isOpen && rowsRefreshIn <= 0 { rowsRefreshIn = 15; refreshChat() }
        if chat.isOpen && !chat.model.showsTerminal {
            timelinePollIn -= dt
            if timelinePollIn <= 0 { timelinePollIn = 0.5; pollTimeline() }
        }
        if chat.isOpen && chat.model.showsTerminal && !chat.model.live {
            screenPollIn -= dt
            // Reads go straight to Orca's socket (about 1 ms), so the view can follow the agent live.
            // Fast while the screen is changing, slower once it has been still for a second.
            let quiet = ProcessInfo.processInfo.systemUptime - lastScreenChange > 1
            if screenPollIn <= 0 { screenPollIn = quiet ? 0.5 : 0.2; pollScreen() }
        }

        if chat.isOpen { sign.set(nil) }   // the chat already shows everything the card would

        // Snacks have windows of their own: one in the air keeps falling while Clawd is hidden.
        let bounds = screenBounds
        snacks.removeAll { $0.gone }
        for s in snacks { s.step(dt: dt, bounds: bounds) }
        if hidden { lastMouse = NSEvent.mouseLocation; return }

        let mouse = NSEvent.mouseLocation
        let speed = hypot(mouse.x - lastMouse.x, mouse.y - lastMouse.y) / dt
        lastMouse = mouse

        // While a menu or the chat is open, or the pointer rests on Clawd, it keeps animating but
        // stays where it is, so nothing slides away from under the pointer. Being dragged or
        // thrown is the user moving it, so that still goes.
        let hovering = window.frame.contains(mouse) && pet.state != .drag
        let hold = (paused || chat.isOpen || hovering) && pet.state != .drag && pet.state != .fly
        pet.frozen = hold
        let anchor = pet.pos

        pet.workActivities = orca.working.map(\.activity)
        pet.holding = !pending.isEmpty
        // The card above Clawd's head stands in for the "!", which would show through its glass.
        pet.attention = pet.holding && sign.current == nil
        pet.step(dt: dt, env: Env(bounds: bounds, size: window.frame.size, mouse: mouse, mouseSpeed: speed, snacks: snacks))
        if hold {
            pet.pos = anchor
            pet.vel = .zero
            if !bounds.insetBy(dx: -window.frame.width, dy: -window.frame.height).contains(anchor) { keepOnScreen() }
        }
        if pet.state != .drag { window.setFrameOrigin(pet.pos) }
        view.needsDisplay = true

        // Hovering over Clawd shows what the Orca agents are up to.
        hoverTime = hovering ? hoverTime + dt : 0
        if !hovering { hoverDismissed = false }
        if !chat.isOpen { refreshSign(hovering: hoverTime > 0.6 && !hoverDismissed) }
        sign.follow(window.frame, scale: pet.scale, screen: bounds)
    }

    func dropSnack() {
        let b = screenBounds
        let x = CGFloat.random(in: b.minX + 40...(b.maxX - 80))
        snacks.append(Snack(at: CGPoint(x: x, y: b.maxY - Snack.size.height)))
    }
}
