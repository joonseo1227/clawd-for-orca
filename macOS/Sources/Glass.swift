import SwiftUI

// MARK: - Liquid Glass
// Apple's glass APIs on macOS 26, with the standard look on earlier systems.

/// Content fades under the header instead of being cut off at a line (macOS 26+).
struct SoftScrollEdge: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 26, *) {
            content.scrollEdgeEffectStyle(.soft, for: .top)
        } else {
            content
        }
    }
}


extension View {
    @ViewBuilder
    func glassButton(prominent: Bool) -> some View {
        if #available(macOS 26, *) {
            if prominent { buttonStyle(.glassProminent) } else { buttonStyle(.glass) }
        } else {
            if prominent { buttonStyle(.borderedProminent) } else { buttonStyle(.bordered) }
        }
    }
}

/// A Messages-style composer: the field and its send button share one interactive glass
/// capsule, the button at the trailing end with equal space above, below and beside it.
struct GlassComposer<Field: View, Send: View>: View {
    @ViewBuilder let field: Field
    @ViewBuilder let send: Send

    var body: some View {
        HStack(alignment: .bottom, spacing: 8) {
            field
                .textFieldStyle(.plain)
                .padding(.vertical, 9)
            // Messages' send button: an accent-filled arrow symbol. A prominent bordered button
            // here renders grey whenever the popover's window isn't key-appearing.
            send
                .buttonStyle(.plain)
                .frame(height: 36)   // centred on the last line, however tall the field grows
        }
        .padding(.leading, 14)
        .padding(.trailing, 5)
        .frame(minHeight: 36)
        .modifier(ComposerGlass())
    }
}

/// Interactive glass on macOS 26+, a bordered field look before that.
struct ComposerGlass: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 26, *) {
            content.glassEffect(.regular.interactive(), in: .capsule)
        } else {
            content.background(.background, in: Capsule()).overlay(Capsule().strokeBorder(.separator))
        }
    }
}

/// Controls grouped like toolbar items on macOS 26: one glass container, glass buttons, the
/// regular toolbar control size. Plain bordered controls before that.
struct ToolbarControls<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        if #available(macOS 26, *) {
            GlassEffectContainer {
                HStack(spacing: 8) { content }
                    .buttonStyle(.glass)
            }
        } else {
            HStack(spacing: 8) { content }
        }
    }
}

/// Answers to a permission dialog, laid out like an alert's buttons. Bordered rather than glass:
/// inside the popover, which is glass itself, prominent glass loses its capsule and stands
/// apart from the plain glass beside it.
struct AnswerButtonStyle: PrimitiveButtonStyle {
    let prominent: Bool

    func makeBody(configuration: Configuration) -> some View {
        if prominent {
            Button(role: configuration.role, action: configuration.trigger) { configuration.label }
                .buttonStyle(.borderedProminent)
        } else {
            Button(role: configuration.role, action: configuration.trigger) { configuration.label }
                .buttonStyle(.bordered)
        }
    }
}
