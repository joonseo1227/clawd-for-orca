import AppKit
import Carbon.HIToolbox
import SwiftUI

// MARK: - Shortcut
// The system-wide "talk to Clawd" shortcut: stored as a virtual key code and modifiers (so it
// follows the key's position, not the input source), registered through Carbon (which needs no
// accessibility permission), and changeable in Settings.

struct Shortcut: Codable, Equatable {
    var keyCode: UInt32
    var modifiers: UInt   // NSEvent.ModifierFlags raw value, device-independent part only

    static let standard = Shortcut(keyCode: UInt32(kVK_ANSI_J), modifiers: NSEvent.ModifierFlags([.control, .option]).rawValue)

    var flags: NSEvent.ModifierFlags { NSEvent.ModifierFlags(rawValue: modifiers) }

    var carbonModifiers: UInt32 {
        var m = 0
        if flags.contains(.command) { m |= cmdKey }
        if flags.contains(.option) { m |= optionKey }
        if flags.contains(.control) { m |= controlKey }
        if flags.contains(.shift) { m |= shiftKey }
        return UInt32(m)
    }

    /// "⌃⌥J", in the order macOS menus use.
    var display: String {
        var s = ""
        if flags.contains(.control) { s += "⌃" }
        if flags.contains(.option) { s += "⌥" }
        if flags.contains(.shift) { s += "⇧" }
        if flags.contains(.command) { s += "⌘" }
        return s + keyName
    }

    /// The key's label on an ASCII keyboard layout, whatever the current input source (a Korean
    /// layout would otherwise name J "ㅓ").
    var keyName: String {
        if let named = Self.named[Int(keyCode)] { return named }
        return character?.uppercased() ?? "#\(keyCode)"
    }

    /// The character for menu key equivalents, when the key has one.
    var character: String? {
        guard Self.named[Int(keyCode)] == nil,
              let source = TISCopyCurrentASCIICapableKeyboardLayoutInputSource()?.takeRetainedValue(),
              let data = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else { return nil }
        let layout = unsafeBitCast(data, to: CFData.self)
        var deadKeys: UInt32 = 0
        var chars = [UniChar](repeating: 0, count: 4)
        var length = 0
        let status = CFDataGetBytePtr(layout).withMemoryRebound(to: UCKeyboardLayout.self, capacity: 1) {
            UCKeyTranslate($0, UInt16(keyCode), UInt16(kUCKeyActionDisplay), 0, UInt32(LMGetKbdType()),
                           OptionBits(kUCKeyTranslateNoDeadKeysBit), &deadKeys, chars.count, &length, &chars)
        }
        guard status == noErr, length > 0 else { return nil }
        return String(utf16CodeUnits: chars, count: length)
    }

    private static let named: [Int: String] = [
        kVK_Space: "Space", kVK_Return: "↩", kVK_Tab: "⇥", kVK_Escape: "⎋", kVK_Delete: "⌫",
        kVK_LeftArrow: "←", kVK_RightArrow: "→", kVK_UpArrow: "↑", kVK_DownArrow: "↓",
        kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6",
        kVK_F7: "F7", kVK_F8: "F8", kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12",
    ]

    /// A shortcut from a key press, or nil when it can't be one: a global shortcut needs ⌘, ⌃
    /// or ⌥ (Shift alone would swallow typing), except for function keys.
    init?(event: NSEvent) {
        let flags = event.modifierFlags.intersection([.command, .option, .control, .shift])
        // Function key codes aren't contiguous (F1 is 122, F12 is 111), so list them.
        let isFunctionKey = Self.functionKeys.contains(Int(event.keyCode))
        guard isFunctionKey || !flags.intersection([.command, .option, .control]).isEmpty else { return nil }
        self.init(keyCode: UInt32(event.keyCode), modifiers: flags.rawValue)
    }

    init(keyCode: UInt32, modifiers: UInt) {
        self.keyCode = keyCode
        self.modifiers = modifiers
    }

    private static let functionKeys = [kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6, kVK_F7, kVK_F8,
                                       kVK_F9, kVK_F10, kVK_F11, kVK_F12, kVK_F13, kVK_F14, kVK_F15]

    // MARK: Stored

    static var saved: Shortcut {
        get {
            guard let data = UserDefaults.standard.data(forKey: PreferenceKey.shortcut.rawValue),
                  let shortcut = try? JSONDecoder().decode(Shortcut.self, from: data) else { return .standard }
            return shortcut
        }
        set { UserDefaults.standard.set(try? JSONEncoder().encode(newValue), forKey: PreferenceKey.shortcut.rawValue) }
    }
}

// MARK: - Global hotkey

/// One system-wide shortcut through Carbon. Registering again replaces the previous key.
final class HotKey {
    private var ref: EventHotKeyRef?
    private var handler: EventHandlerRef?
    // Carbon's C callback can't capture context; the one hotkey's action lives here.
    nonisolated(unsafe) private static var action: (@MainActor () -> Void)?

    init(action: @escaping @MainActor () -> Void) {
        HotKey.action = action
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
            onMain { HotKey.action?() }
            return noErr
        }, 1, &spec, nil, &handler)
    }

    /// False when the key combination is taken by another app or the system.
    @discardableResult
    func register(_ shortcut: Shortcut) -> Bool {
        unregister()
        let id = EventHotKeyID(signature: OSType(0x434C_5744), id: 1)   // 'CLWD'
        return RegisterEventHotKey(shortcut.keyCode, shortcut.carbonModifiers, id, GetApplicationEventTarget(), 0, &ref) == noErr
    }

    func unregister() {
        if let ref { UnregisterEventHotKey(ref) }
        ref = nil
    }

    /// Pauses the shortcut, e.g. while a new one is being recorded.
    var isPaused = false {
        didSet { if isPaused { unregister() } }
    }
}

// MARK: - Recorder

/// A field that shows the shortcut and records a new one when clicked, like the ones in
/// System Settings › Keyboard › Keyboard Shortcuts.
struct ShortcutRecorder: View {
    @Binding var shortcut: Shortcut
    /// Called around recording, so the current shortcut doesn't fire while keys are pressed.
    var onRecording: (Bool) -> Void = { _ in }
    @State private var recording = false
    @State private var monitor: Any?

    var body: some View {
        HStack(spacing: 6) {
            Button {
                recording ? stop() : start()
            } label: {
                Text(recording ? String(localized: "Type a shortcut", comment: "Shown while recording a keyboard shortcut") : shortcut.display)
                    .monospacedDigit()
                    .frame(minWidth: 110)
            }
            .help(recording ? Text("Press Esc to cancel") : Text("Click to change"))
            if shortcut != .standard && !recording {
                Button {
                    shortcut = .standard
                } label: {
                    Label("Reset to default", systemImage: "arrow.counterclockwise").labelStyle(.iconOnly)
                }
                .buttonStyle(.borderless)
                .help("Reset to default (\(Shortcut.standard.display))")
            }
        }
        .onDisappear { stop() }
    }

    private func start() {
        recording = true
        onRecording(true)
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            if event.keyCode == UInt16(kVK_Escape) && event.modifierFlags.intersection([.command, .option, .control]).isEmpty {
                stop()
            } else if let new = Shortcut(event: event) {
                shortcut = new
                stop()
            } else {
                NSSound.beep()   // needs a modifier
            }
            return nil
        }
    }

    private func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        if recording { onRecording(false) }
        recording = false
    }
}
