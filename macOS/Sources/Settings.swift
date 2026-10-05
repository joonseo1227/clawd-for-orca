import AppKit
import SwiftUI

// MARK: - Settings window
// A grouped form like System Settings. Clawd runs on the AppKit app lifecycle (it is a menu bar
// accessory with its own windows), where SwiftUI's Settings scene can't be opened from AppKit
// menus, so the same form lives in a regular window opened with ⌘,.

struct SettingsView: View {
    let app: AppDelegate
    @State var pairing = false       // pairing sheet shown
    @State private var refresh = 0   // bumps after actions that change state outside SwiftUI
    @State private var shortcutTaken = false

    var body: some View {
        let _ = refresh
        Form {
            Section("General") {
                Toggle("Open Clawd at login", isOn: Binding(
                    get: { LoginItem.isEnabled },
                    set: { _ in app.toggleLogin(); refresh += 1 }))
                Toggle("Show Clawd on screen", isOn: Binding(
                    get: { !app.hidden },
                    set: { _ in app.toggleHidden(); refresh += 1 }))
                Picker("System notifications", selection: Binding(
                    get: { app.notificationPolicy },
                    set: { app.notificationPolicy = $0; refresh += 1 })) {
                    ForEach(AgentNotifier.Policy.allCases) { Text($0.title).tag($0) }
                }
                Toggle("Notification sound", isOn: Binding(
                    get: { app.soundOn },
                    set: { app.soundOn = $0; refresh += 1 }))
            }

            Section {
                LabeledContent("Status") { OrcaStatusView(orca: app.orca) }
                Toggle("Orca agent integration", isOn: Binding(
                    get: { app.orca.enabled },
                    set: { _ in app.toggleOrca(); refresh += 1 }))
                    .disabled(!app.orca.available)
                LabeledContent {
                    if app.bridge.isPaired {
                        HStack {
                            Label("Paired", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                            Button("Unpair") { app.unpair(); refresh += 1 }
                        }
                    } else {
                        Button("Pair…") { pairing = true }
                    }
                } label: {
                    Text("Live terminal")
                    Text("Paired with a code from Orca Mobile")
                }
            } header: {
                Text(verbatim: "Orca")
            }

            Section("Shortcuts") {
                LabeledContent("Talk to Clawd") {
                    ShortcutRecorder(shortcut: Binding(
                        get: { Shortcut.saved },
                        set: { shortcutTaken = !app.setShortcut($0); refresh += 1 }),
                        onRecording: { app.hotKey.isPaused = $0; if !$0 { app.setShortcut(Shortcut.saved) } })
                }
                if shortcutTaken {
                    Label("Another app already uses this shortcut. Choose a different one.", systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                        .font(.callout)
                }
                LabeledContent("Switch between chat and terminal", value: "⌘T")
                LabeledContent("Close chat", value: "⌘W")
            }

            Section {
                LabeledContent("Version") {
                    HStack {
                        Text(version)
                        if app.updates.isEnabled {
                            Button("Check for Updates…") { app.updates.check() }
                                .disabled(!app.updates.canCheck)
                        }
                    }
                }
                if app.updates.isEnabled {
                    Toggle("Check for updates automatically", isOn: Binding(
                        get: { app.updates.automaticallyChecks },
                        set: { app.updates.automaticallyChecks = $0; refresh += 1 }))
                }
                LabeledContent("Made by") {
                    Link("joonseo1227" as String, destination: URL(string: "https://joonseo1227.com")!)
                }
            } header: {
                Text("About")
            } footer: {
                // Leading, like System Settings' section notes; footers otherwise sit right-aligned.
                Text("An unofficial app, not affiliated with Anthropic or Stably AI.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .formStyle(.grouped)
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
        // State lives in the app delegate, outside SwiftUI; follow it while the window is open.
        .task {
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

extension SettingsView {
    /// The version, with the build number only when it differs (release builds use the version for both).
    var version: String {
        guard let version = AppIdentity.version else { return String(localized: "Development build") }
        guard let build = AppIdentity.build, build != version else { return version }
        return "\(version) (\(build))"
    }
}

/// Takes the pairing code from Orca Mobile's "Copy pairing code".
struct PairingSheet: View {
    let onPair: (OrcaPairing) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var code = ""
    @State private var invalid = false

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Label("Live connection to Orca", systemImage: "link").font(.title3.weight(.semibold))
            Text("To show the terminal with its colors and cursor, Clawd needs a live connection to Orca.")
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 6) {
                Text("1. In Orca’s left sidebar, open **Orca Mobile**.")
                Text("2. Generate a QR code and click **Copy pairing code**.")
                Text("3. Paste it below.")
            }
            HStack {
                // A secure field: the code carries a token that can drive your terminals.
                SecureField("orca://pair?code=…" as String, text: $code)
                    .textFieldStyle(.roundedBorder)
                    .onChange(of: code) { _, _ in invalid = false }
                Button("Paste") { code = NSPasteboard.general.string(forType: .string) ?? "" }
            }
            if invalid {
                Label("Can’t read the pairing code. Paste the code exactly as copied from Orca.",
                      systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red).font(.callout)
            }
            Text("The code is stored only in this Mac’s keychain.").font(.footnote).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Pair") {
                    guard let pairing = OrcaPairing(code: code) else { invalid = true; return }
                    onPair(pairing)
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(code.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(24)
        .frame(width: 440)
    }
}

final class SettingsWindow {
    private var window: NSWindow?

    func show(app: AppDelegate, pairing: Bool = false) {
        if window == nil {
            let w = NSWindow(contentViewController: NSHostingController(rootView: SettingsView(app: app, pairing: pairing)))
            w.title = String(localized: "Clawd Settings", comment: "Settings window title")
            w.styleMask = [.titled, .closable]
            w.isReleasedWhenClosed = false
            w.center()
            window = w
        } else {
            // Rebuild so the form reflects changes made elsewhere (menus, Clawd itself).
            window?.contentViewController = NSHostingController(rootView: SettingsView(app: app, pairing: pairing))
        }
        NSApp.activate()
        window?.makeKeyAndOrderFront(nil)
    }
}
