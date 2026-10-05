import AppKit

// MARK: - Physics helpers

let gravity: CGFloat = 1800   // points / s²

/// Turns the last few drag positions into a throw velocity.
struct DragTracker {
    var samples: [(t: TimeInterval, p: CGPoint)] = []

    mutating func add(_ p: CGPoint) {
        let now = ProcessInfo.processInfo.systemUptime
        samples.append((now, p))
        samples.removeAll { now - $0.t > 0.1 }
    }

    func velocity() -> CGPoint {
        guard let a = samples.first, let b = samples.last, b.t - a.t > 0.01,
              ProcessInfo.processInfo.systemUptime - b.t < 0.08 else { return .zero }
        let dt = CGFloat(b.t - a.t)
        return CGPoint(x: (b.p.x - a.p.x) / dt, y: (b.p.y - a.p.y) / dt)
    }
}

// MARK: - Snack

final class Snack {
    static let scale: CGFloat = 5
    static let size = CGSize(width: 8 * scale, height: 7 * scale)
    static let art = [".####.", "##o###", "###o##", "#o####", ".####."]

    let window: NSWindow
    var pos: CGPoint
    var vel = CGPoint.zero
    var held = false
    var grounded = false
    var bites = 0
    var gone = false

    var centerX: CGFloat { pos.x + Snack.size.width / 2 }

    init(at pos: CGPoint) {
        self.pos = pos
        window = NSWindow(contentRect: NSRect(origin: pos, size: Snack.size), styleMask: .borderless, backing: .buffered, defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        window.level = .floating
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary]
        window.contentView = SnackView(snack: self)
        window.orderFrontRegardless()
    }

    func step(dt: CGFloat, bounds: NSRect) {
        if held { grounded = false; return }
        let ground = bounds.minY - Snack.scale
        if pos.y > ground || vel.y > 0 {
            vel.y -= gravity * dt
            pos.x = max(bounds.minX, min(bounds.maxX - Snack.size.width, pos.x + vel.x * dt))
            pos.y += vel.y * dt
        }
        grounded = pos.y <= ground
        if grounded { pos.y = ground; vel = .zero }
        window.setFrameOrigin(pos)
        window.contentView?.needsDisplay = true
    }

    func consume() {
        gone = true
        window.orderOut(nil)
    }
}

final class SnackView: NSView {
    unowned let snack: Snack
    var grab: NSPoint?
    var tracker = DragTracker()

    init(snack: Snack) {
        self.snack = snack
        super.init(frame: NSRect(origin: .zero, size: Snack.size))
    }
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        ctx.clear(bounds)
        ctx.scaleBy(x: Snack.scale, y: Snack.scale)
        let visibleCols = 6 - snack.bites * 2   // bites come off the right side
        for (ry, row) in Snack.art.enumerated() {
            for (rx, ch) in row.enumerated() where ch != "." && rx < visibleCols {
                ctx.setFillColor(ch == "o" ? chipColor : cookieColor)
                ctx.fill(CGRect(x: 1 + CGFloat(rx), y: 1 + CGFloat(ry), width: 1, height: 1))
            }
        }
    }

    override func mouseDown(with event: NSEvent) {
        grab = event.locationInWindow
        snack.held = true
        tracker = DragTracker()
    }

    override func mouseDragged(with event: NSEvent) {
        guard let grab else { return }
        let m = NSEvent.mouseLocation
        snack.pos = CGPoint(x: m.x - grab.x, y: m.y - grab.y)
        tracker.add(snack.pos)
        window?.setFrameOrigin(snack.pos)
    }

    override func mouseUp(with event: NSEvent) {
        grab = nil
        snack.held = false
        snack.vel = tracker.velocity()
    }
}
