import AppKit
import Sparkle

// MARK: - Updates
// Sparkle checks the appcast named in Info.plist (build.sh) once a day, verifies each download's
// EdDSA signature and replaces the app. Clawd lives in the menu bar, so an update found in the
// background doesn't open a window over your work: Clawd mentions it on a card and in its menus,
// and Sparkle's window opens when you ask for it.

final class Updates: NSObject, @preconcurrency SPUStandardUserDriverDelegate {
    /// The version a scheduled check found, until the user looks at it.
    private(set) var available: String?
    var onAvailable: ((String) -> Void)?

    private var controller: SPUStandardUpdaterController?

    /// False in the bare development binary, which has no Info.plist to name the feed.
    var isEnabled: Bool { controller != nil }

    func start() {
        guard Bundle.main.object(forInfoDictionaryKey: "SUFeedURL") != nil else { return }
        controller = SPUStandardUpdaterController(startingUpdater: true, updaterDelegate: nil, userDriverDelegate: self)
    }

    var automaticallyChecks: Bool {
        get { controller?.updater.automaticallyChecksForUpdates ?? false }
        set { controller?.updater.automaticallyChecksForUpdates = newValue }
    }

    /// False while a check or an install is already under way.
    var canCheck: Bool { controller?.updater.canCheckForUpdates ?? false }

    /// Shows Sparkle's window: the update found earlier, or a fresh check.
    func check() {
        NSApp.activate()
        controller?.checkForUpdates(nil)
    }

    // MARK: SPUStandardUserDriverDelegate

    var supportsGentleScheduledUpdateReminders: Bool { true }

    /// Right after launch Sparkle may show the update itself; any later find is announced by Clawd.
    func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem, andInImmediateFocus immediateFocus: Bool) -> Bool {
        immediateFocus && !TestHooks.deferUpdate
    }

    func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem, state: SPUUserUpdateState) {
        guard !handleShowingUpdate else { return }
        available = update.displayVersionString
        onAvailable?(update.displayVersionString)
    }

    func standardUserDriverDidReceiveUserAttention(forUpdate update: SUAppcastItem) {
        available = nil
    }

    func standardUserDriverWillFinishUpdateSession() {
        available = nil
    }
}

extension AppDelegate {
    /// A card while Clawd is on screen; the menus offer the update until it has been looked at.
    func announceUpdate(_ version: String) {
        let card = Sign(tone: .info, symbol: "arrow.down.circle.fill",
                        title: String(localized: "Clawd \(version) is available", comment: "%@ is a version such as 1.0.1"),
                        hint: String(localized: "Click to update"))
        flash = (card, ProcessInfo.processInfo.systemUptime + 60, { [weak self] in self?.updates.check() })
    }

    /// The update waiting to be installed, else "Check for Updates…" where `orCheck`; nothing in a
    /// development build.
    func updateItem(orCheck: Bool) -> NSMenuItem? {
        guard updates.isEnabled else { return nil }
        if let version = updates.available {
            return item(String(localized: "Update to Clawd \(version)…", comment: "Menu item; %@ is a version such as 1.0.1"), #selector(checkForUpdates))
        }
        return orCheck ? item(String(localized: "Check for Updates…"), #selector(checkForUpdates)) : nil
    }

    @objc func checkForUpdates() { updates.check() }
}
