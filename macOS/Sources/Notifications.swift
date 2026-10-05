import AppKit
import UserNotifications

// MARK: - System notifications
// When Clawd is hidden its cards can't be seen, so agents that need you (and finished work)
// arrive as macOS notifications instead, answerable in place: type a reply in the banner and
// it goes to the agent's terminal. Focus modes and notification settings apply as usual.

final class AgentNotifier: NSObject, UNUserNotificationCenterDelegate {
    enum Action: String {
        case reply, open
        case show   // the banner itself was clicked
    }
    enum Category: String {
        case question = "agent.question"
        case permission = "agent.permission"
        case done = "agent.done"
    }
    /// For a session in Orca's chat, which can't take a reply from Clawd.
    private static let openOnly = "agent.open-only"

    /// What the user did with a notification: the pane it was about, the action, typed text.
    var onResponse: ((String, Action, String?) -> Void)?

    private var center: UNUserNotificationCenter { .current() }
    private var authorized: Bool?

    func start() {
        center.delegate = self
        let reply = UNTextInputNotificationAction(identifier: Action.reply.rawValue, title: String(localized: "Reply"),
                                                  options: [], textInputButtonTitle: String(localized: "Send"),
                                                  textInputPlaceholder: String(localized: "Message"))
        let open = UNNotificationAction(identifier: Action.open.rawValue, title: String(localized: "Open in Orca"), options: [.foreground])
        center.setNotificationCategories([
            UNNotificationCategory(identifier: Category.question.rawValue, actions: [reply, open], intentIdentifiers: []),
            // A permission dialog is answered by choosing an option, which needs the chat.
            UNNotificationCategory(identifier: Category.permission.rawValue, actions: [open], intentIdentifiers: []),
            UNNotificationCategory(identifier: Category.done.rawValue, actions: [reply, open], intentIdentifiers: []),
            UNNotificationCategory(identifier: Self.openOnly, actions: [open], intentIdentifiers: []),
        ])
    }

    /// Asks for permission the first time it is needed; macOS remembers the answer.
    private func withAuthorization(_ work: @escaping @MainActor () -> Void) {
        if authorized == true { work(); return }
        center.requestAuthorization(options: [.alert, .sound]) { granted, error in
            Log.debug("notifications authorized=\(granted) error=\(error.map { "\($0)" } ?? "-")")
            onMain {
                self.authorized = granted
                if granted { work() }
            }
        }
    }

    /// When to post: the default only covers a hidden Clawd, whose cards can't be seen.
    enum Policy: String, CaseIterable, Identifiable {
        case whenHidden, always, never
        var id: String { rawValue }
        var title: String {
            switch self {
            case .whenHidden: String(localized: "When Clawd is hidden", comment: "When to send system notifications")
            case .always: String(localized: "Always", comment: "When to send system notifications")
            case .never: String(localized: "Never", comment: "When to send system notifications")
            }
        }
    }

    func post(_ category: Category, agent: OrcaAgent, title: String, body: String?, sound: Bool) {
        withAuthorization { [center] in
            let content = UNMutableNotificationContent()
            content.title = title
            content.subtitle = agent.name
            content.body = body ?? ""
            content.categoryIdentifier = agent.hasTerminal ? category.rawValue : Self.openOnly
            content.userInfo = ["pane": agent.paneKey]
            content.threadIdentifier = agent.paneKey   // one stack per agent
            content.interruptionLevel = category == .done ? .active : .timeSensitive
            if sound { content.sound = .default }
            // One notification per agent and state: a newer one replaces the older.
            let request = UNNotificationRequest(identifier: "\(agent.paneKey)#\(category.rawValue)", content: content, trigger: nil)
            let id = request.identifier
            center.add(request) { error in
                if let error { Log.error("notification failed: \(error)") } else { Log.debug("notified \(id)") }
            }
        }
    }

    /// The agent was dealt with (answered, opened): its notifications are stale.
    func clear(_ paneKey: String) {
        let ids = [Category.question, .permission, .done].map { "\(paneKey)#\($0.rawValue)" }
        center.removeDeliveredNotifications(withIdentifiers: ids)
    }

    // MARK: UNUserNotificationCenterDelegate

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        let pane = response.notification.request.content.userInfo["pane"] as? String
        let text = (response as? UNTextInputNotificationResponse)?.userText
        let action: Action = response.actionIdentifier == UNNotificationDefaultActionIdentifier ? .show
            : Action(rawValue: response.actionIdentifier) ?? .show
        onMain {
            if let pane { self.onResponse?(pane, action, text) }
        }
        completionHandler()
    }

    /// Clawd counts as the frontmost app while its chat is open; show the banner anyway.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                            withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list, .sound])
    }
}
