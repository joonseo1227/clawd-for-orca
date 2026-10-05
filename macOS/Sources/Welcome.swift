import AppKit
import SwiftUI

// MARK: - Welcome
// Shown on first launch: what Clawd does, and the few things to set up, each with its state
// and the one action that completes it. Nothing here is required to start.

struct WelcomeView: View {
    let app: AppDelegate
    let onDone: () -> Void
    @State private var pairing = false
    @State private var refresh = 0

    var body: some View {
        let _ = refresh
        VStack(spacing: 0) {
            ClawdPortrait()
                .frame(width: 160, height: 120)
                .padding(.top, 28)
                .accessibilityHidden(true)
            VStack(spacing: 8) {
                Text("Meet Clawd")
                    .font(.largeTitle.bold())
                Text("Clawd watches the coding agents working in Orca and lets you know when they need an answer or finish. Click Clawd to start chatting.")
                    .font(.title3)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 40)

            Form {
                Section {
                    LabeledContent {
                        OrcaStatusView(orca: app.orca)
                    } label: {
                        Label("Orca" as String, systemImage: "app.connected.to.app.below.fill")
                    }
                    LabeledContent {
                        Text(Shortcut.saved.display).foregroundStyle(.secondary)
                    } label: {
                        Label("Summon Clawd from anywhere", systemImage: "keyboard")
                    }
                    Toggle(isOn: Binding(get: { LoginItem.isEnabled },
                                         set: { _ in app.toggleLogin(); refresh += 1 })) {
                        Label("Open at login", systemImage: "power")
                    }
                    LabeledContent {
                        if app.bridge.isPaired {
                            Label("Paired", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                        } else {
                            Button("Pair…") { pairing = true }
                        }
                    } label: {
                        Label {
                            Text("Live terminal")
                            Text("Optional · Shows the terminal with its colors and cursor")
                        } icon: {
                            Image(systemName: "terminal")
                        }
                    }
                }
            }
            .formStyle(.grouped)
            .scrollDisabled(true)
            .scrollContentBackground(.hidden)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, 12)

            Button {
                onDone()
            } label: {
                Text("Get started").frame(maxWidth: 240)
            }
            .glassButton(prominent: true)
            .controlSize(.extraLarge)
            .keyboardShortcut(.defaultAction)
            .padding(.top, 8)
            .padding(.bottom, 28)
        }
        .frame(width: 520)
        .task {
            // Orca may be opened or installed while this is up.
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(2))
                refresh += 1
            }
        }
        .sheet(isPresented: $pairing, onDismiss: { refresh += 1 }) {
            PairingSheet { app.completePairing($0) }
        }
    }
}

/// Whether Clawd can see Orca, and the one action that helps when it can't.
struct OrcaStatusView: View {
    let orca: OrcaWatcher

    var body: some View {
        if !orca.available {
            HStack {
                Label("Not installed", systemImage: "xmark.circle.fill").foregroundStyle(.secondary)
                Link("Download", destination: OrcaInstallation.homepage)
            }
        } else if !orca.running {
            HStack {
                Label("Not running", systemImage: "moon.zzz.fill").foregroundStyle(.secondary)
                Button("Open") { orca.launch() }
            }
        } else {
            Label {
                Text("Running · \(orca.agents.count) agents", comment: "Orca is running, with this many agents").foregroundStyle(.secondary)
            } icon: {
                Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
            }
        }
    }
}

/// Clawd standing, breathing, blinking and now and then waving, drawn with the pet's own sprite.
struct ClawdPortrait: View {
    var body: some View {
        TimelineView(.animation(minimumInterval: 1 / 15)) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            Canvas { canvas, size in
                // Whole-number scale keeps the pixels crisp.
                let scale = max(1, floor(min(size.width / canvasW, size.height / (canvasH - 6))))
                canvas.withCGContext { ctx in
                    ctx.interpolationQuality = .none
                    ctx.translateBy(x: (size.width - canvasW * scale) / 2, y: size.height - canvasH * scale)
                    ctx.scaleBy(x: scale, y: scale)
                    render(ctx, pose: Self.pose(at: t), effects: [])
                }
            }
        }
    }

    static func pose(at t: TimeInterval) -> Pose {
        var pose = Pose()
        pose.phase = CGFloat(t)
        pose.bob = Int(t / 0.6) % 2 == 0 ? 0 : -0.5
        if t.truncatingRemainder(dividingBy: 4) < 0.15 { pose.eyes = .closed }
        // A short wave every eight seconds.
        let cycle = t.truncatingRemainder(dividingBy: 8)
        if cycle < 2 {
            pose.armR = Int(cycle / 0.25) % 2 == 0 ? 1 : 0.5
            pose.eyes = .happy
        }
        return pose
    }
}

final class WelcomeWindow {
    private var window: NSWindow?

    func show(app: AppDelegate) {
        if window == nil {
            let host = NSHostingController(rootView: WelcomeView(app: app) { [weak self] in self?.close() })
            let w = NSWindow(contentViewController: host)
            w.styleMask = [.titled, .closable, .fullSizeContentView]
            w.titlebarAppearsTransparent = true
            w.titleVisibility = .hidden
            w.title = String(localized: "Welcome to Clawd", comment: "Welcome window title")
            w.isMovableByWindowBackground = true
            w.isReleasedWhenClosed = false
            w.center()
            window = w
        }
        NSApp.activate()
        window?.makeKeyAndOrderFront(nil)
    }

    func close() {
        window?.close()
    }
}
