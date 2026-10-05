import Foundation
import ServiceManagement

/// Launch at login through SMAppService, so Clawd shows up under System Settings › General ›
/// Login Items like any other app and macOS manages it.
enum LoginItem {
    enum Result { case enabled, disabled, needsApproval, failed(String) }

    static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }

    @discardableResult
    static func set(_ on: Bool) -> Result {
        removeLegacyAgent()
        do {
            if on {
                try SMAppService.mainApp.register()
                return SMAppService.mainApp.status == .requiresApproval ? .needsApproval : .enabled
            }
            try SMAppService.mainApp.unregister()
            return .disabled
        } catch {
            if SMAppService.mainApp.status == .requiresApproval { return .needsApproval }
            return .failed(error.localizedDescription)
        }
    }

    /// Earlier builds wrote a LaunchAgent plist by hand; drop it so Clawd doesn't start twice.
    static func removeLegacyAgent() {
        let legacy = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/local.clawd.pet.plist")
        try? FileManager.default.removeItem(at: legacy)
    }
}
