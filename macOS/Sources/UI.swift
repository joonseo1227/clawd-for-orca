import AppKit
import SwiftUI

// MARK: - Sign card

/// What Clawd shows above its head. Everything except Clawd follows the system look.
struct Sign: Equatable {
    enum Tone { case urgent, done, info }
    var tone: Tone
    var symbol: String
    var title: String
    var name: String? = nil
    var detail: String? = nil
    var hint: String? = nil
}

/// What a card does when clicked or closed; swapped without rebuilding the card.
@Observable
final class SignActions {
    var onTap: (() -> Void)?
    var onClose: (() -> Void)?
}

struct SignCard: View {
    let sign: Sign
    let actions: SignActions
    // Sized from the text styles, so the card follows the system's type scale.
    @ScaledMetric(relativeTo: .largeTitle) private var badge: CGFloat = 56
    @ScaledMetric(relativeTo: .title) private var smallBadge: CGFloat = 40

    var tint: Color {
        switch sign.tone {
        case .urgent: return .orange
        case .done: return .green
        case .info: return .gray
        }
    }

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            // A coloured badge like a notification's app icon: readable from across the room.
            let size = sign.tone == .info ? smallBadge : badge
            Image(systemName: sign.symbol)
                .font(.system(size: size * 0.5, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: size, height: size)
                .background(tint.gradient, in: .rect(cornerRadius: size * 0.26, style: .continuous))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 3) {
                Text(sign.title)
                    .font(sign.tone == .info ? .title.bold() : .largeTitle.bold())
                if let name = sign.name {
                    Text(name).font(.title2.weight(.semibold))
                }
                if let detail = sign.detail {
                    Text(detail)
                        .font(.title3)
                        .foregroundStyle(.primary.opacity(0.85))
                        .lineLimit(4)
                        .padding(.top, 2)
                }
                if let hint = sign.hint {
                    Text(hint)
                        .font(.callout.weight(.medium))
                        .foregroundStyle(.secondary)
                        .padding(.top, 4)
                }
            }
            .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 16)
        .padding(.trailing, 18)   // room for the close button
        .frame(minWidth: 220, maxWidth: 440, alignment: .leading)
        .modifier(CardBackground())
        .contentShape(.rect)
        .onTapGesture { actions.onTap?() }
        // One element for VoiceOver, activated like a button when the card does something.
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(actions.onTap == nil ? [] : .isButton)
        .accessibilityAction { actions.onTap?() }
        .overlay(alignment: .topTrailing) {
            Button { actions.onClose?() } label: {
                Label("Close", systemImage: "xmark").labelStyle(.iconOnly)
            }
            .buttonStyle(.borderless)
            .controlSize(.small)
            .help("Close")
            .padding(10)
        }
        .padding(12)   // room for the system shadow
    }
}

/// Liquid Glass on macOS 26+, material before that. Colour lives in the badge, not the card.
struct CardBackground: ViewModifier {
    /// Matches the corner of a macOS 26 notification banner.
    static let cornerRadius: CGFloat = 22

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: Self.cornerRadius, style: .continuous)
        if #available(macOS 26, *) {
            content.glassEffect(.regular, in: shape)
        } else {
            content.background(.regularMaterial, in: shape)
        }
    }
}

final class SignBoard {
    private final class HostView: NSHostingView<SignCard> {
        // The panel never becomes key; take the first click anyway.
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    }

    let panel: NSPanel
    private let actions = SignActions()
    private lazy var host = HostView(rootView: SignCard(sign: Sign(tone: .info, symbol: "circle", title: ""), actions: actions))
    private(set) var current: Sign?

    init() {
        panel = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary]
        // The panel takes the card's size from set(). Left to the hosting view, the panel's minimum
        // height was the card's at its narrowest width, with the text wrapped onto more lines: the
        // card sat centred in empty, click-blocking space, further above Clawd the longer its text.
        host.sizingOptions = [.intrinsicContentSize]
        panel.contentView = host
    }

    /// Shows `sign` (or hides with nil). Re-showing the same sign only swaps its actions.
    func set(_ sign: Sign?, action: (() -> Void)? = nil, onClose: (() -> Void)? = nil) {
        actions.onTap = action
        actions.onClose = onClose ?? { [weak self] in self?.set(nil) }
        guard sign != current else { return }
        let wasHidden = current == nil
        current = sign
        guard let sign else { panel.orderOut(nil); return }
        host.rootView = SignCard(sign: sign, actions: actions)
        // fittingSize gives the card's width, but its height as if no text wrapped; a long reply
        // wraps at that width, so measure the height there or the card is cut off.
        let width = host.fittingSize.width
        let height = NSHostingController(rootView: host.rootView).sizeThatFits(in: NSSize(width: width, height: 10_000)).height
        panel.setContentSize(NSSize(width: width, height: ceil(height)))
        panel.invalidateShadow()
        if wasHidden {
            // One short fade so a new card catches the eye; nothing loops.
            panel.alphaValue = 0
            panel.orderFrontRegardless()
            NSAnimationContext.runAnimationGroup { $0.duration = 0.2; panel.animator().alphaValue = 1 }
        }
    }

    /// Floats just above Clawd's head, kept on screen.
    func follow(_ petFrame: NSRect, scale: CGFloat, screen: NSRect) {
        guard current != nil else { return }
        let size = panel.frame.size
        let headTop = petFrame.minY + (canvasH - spriteY) * scale
        let x = max(screen.minX, min(screen.maxX - size.width, petFrame.midX - size.width / 2))
        let y = min(screen.maxY - size.height, headTop - 4)
        panel.setFrameOrigin(NSPoint(x: x, y: y))
    }
}

// MARK: - Menu bar icon

/// Clawd's standing sprite at one point per pixel, for the menu bar.
func clawdIcon() -> NSImage {
    NSImage(size: NSSize(width: 18, height: 10), flipped: true) { _ in
        guard let ctx = NSGraphicsContext.current?.cgContext else { return false }
        ctx.translateBy(x: -spriteX, y: -spriteY)
        render(ctx, pose: Pose(), effects: [])
        return true
    }
}
