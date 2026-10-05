import AppKit

// MARK: - Menus
// The menu bar icon's menu and Clawd's right-click menu.

extension AppDelegate {

    func menuNeedsUpdate(_ menu: NSMenu) {
        guard menu === statusMenu else { return }
        menu.removeAllItems()
        addAgentItems(to: menu)
        menu.addItem(.separator())
        menu.addItem(item(hidden ? String(localized: "Show Clawd") : String(localized: "Hide Clawd"), #selector(toggleHidden)))
        if !hidden { menu.addItem(item(String(localized: "Give a snack"), #selector(menuSnack))) }
        menu.addItem(settingsItem())
        if let update = updateItem(orCheck: true) { menu.addItem(update) }
        menu.addItem(.separator())
        menu.addItem(quitItem())
    }

    func petMenu() -> NSMenu {
        let menu = NSMenu()
        addAgentItems(to: menu)
        menu.addItem(.separator())
        menu.addItem(item(String(localized: "Give a snack"), #selector(menuSnack)))
        let tricks = NSMenu()
        for (i, a) in Activity.allCases.enumerated() {
            let it = item(a.title, #selector(menuActivity(_:)))
            it.tag = i
            tricks.addItem(it)
        }
        let tricksItem = NSMenuItem(title: String(localized: "Tricks", comment: "Submenu of animations Clawd can perform"), action: nil, keyEquivalent: "")
        tricksItem.submenu = tricks
        menu.addItem(tricksItem)
        menu.addItem(item(pet.following ? String(localized: "Stop following") : String(localized: "Follow the pointer"), #selector(menuFollow)))
        menu.addItem(item(pet.state == .sleep ? String(localized: "Wake up") : String(localized: "Put to sleep"), #selector(menuSleep)))
        menu.addItem(.separator())
        menu.addItem(item(String(localized: "Hide Clawd"), #selector(toggleHidden)))
        menu.addItem(settingsItem())
        if let update = updateItem(orCheck: false) { menu.addItem(update) }
        menu.addItem(quitItem())
        return menu
    }

    func quitItem() -> NSMenuItem {
        NSMenuItem(title: String(localized: "Quit Clawd"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
    }

    func item(_ title: String, _ action: Selector?) -> NSMenuItem {
        let it = NSMenuItem(title: title, action: action, keyEquivalent: "")
        it.target = self
        return it
    }

    func header(_ title: String) -> NSMenuItem {
        let it = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        it.isEnabled = false
        return it
    }

    /// Agent rows, each with a submenu to open the terminal or send a message.
    func addAgentItems(to menu: NSMenu) {
        guard orca.available else { menu.addItem(header(String(localized: "Can’t find Orca"))); return }
        guard orca.enabled else { menu.addItem(header(String(localized: "Orca integration off"))); return }

        func row(_ title: String, _ a: OrcaAgent, detail: String?, canSend: Bool) {
            let sub = NSMenu()
            if let detail { sub.addItem(header(detail)); sub.addItem(.separator()) }
            let openItem = item(String(localized: "Open in Orca"), #selector(menuOpen(_:)))
            openItem.representedObject = a.paneKey
            sub.addItem(openItem)
            if canSend {
                let title = !a.canMessage ? String(localized: "Show in Clawd")
                    : a.needsYou ? String(localized: "Reply with Clawd") : String(localized: "Message with Clawd")
                let sendItem = item(title, #selector(menuCompose(_:)))
                sendItem.representedObject = a.paneKey
                sub.addItem(sendItem)
            }
            let it = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            it.submenu = sub
            menu.addItem(it)
        }

        let waiting = orca.waiting
        let working = orca.working
        let done = finished.values.sorted { $0.at > $1.at }
        if waiting.isEmpty && working.isEmpty && done.isEmpty {
            menu.addItem(header(String(localized: "All agents are resting")))
        }
        if !waiting.isEmpty {
            menu.addItem(header(String(localized: "Needs attention", comment: "Group of agents waiting for the user")))
            for a in waiting {
                let waited = Date().timeIntervalSince(since(a))
                let title = waited < 60 ? String(localized: "\(a.name) · just now", comment: "Agent that started waiting less than a minute ago")
                    : String(localized: "\(a.name) · for \(duration(waited))", comment: "Agent name, then how long it has been waiting, e.g. 3 min")
                row(title, a, detail: snippet(a.ask, 70), canSend: true)
            }
        }
        if !working.isEmpty {
            menu.addItem(header(String(localized: "Working")))
            for a in working {
                let since = a.stateStartedAt.map { " · \(duration(Date().timeIntervalSince($0)))" } ?? ""
                row(a.name + (a.tool.map { " · \($0)" } ?? "") + since, a, detail: snippet(a.prompt, 70), canSend: true)
            }
        }
        if !done.isEmpty {
            menu.addItem(header(String(localized: "Recently finished")))
            for f in done.prefix(5) {
                row("\(f.agent.name) · \(ago(f.at))", f.agent,
                    detail: snippet(f.agent.lastMessage, 70), canSend: true)
            }
        }
        let jumpItem = item(String(localized: "Talk to Clawd"), #selector(menuJump))
        // Shows the global shortcut beside the item, as menus do for their own shortcuts.
        let shortcut = Shortcut.saved
        if let key = shortcut.character {
            jumpItem.keyEquivalent = key.lowercased()
            jumpItem.keyEquivalentModifierMask = shortcut.flags
        }
        menu.addItem(jumpItem)
    }

    func settingsItem() -> NSMenuItem {
        let it = item(String(localized: "Settings…"), #selector(openSettings))
        it.keyEquivalent = ","
        it.keyEquivalentModifierMask = .command
        return it
    }

    @objc func openSettings() {
        chat.close()
        settingsWindow.show(app: self)
    }

    func agent(_ key: Any?) -> OrcaAgent? {
        guard let key = key as? String else { return nil }
        return orca.agents.first { $0.paneKey == key } ?? finished[key]?.agent
    }

    @objc func menuOpen(_ sender: NSMenuItem) { if let a = agent(sender.representedObject) { open(a) } }
    @objc func menuCompose(_ sender: NSMenuItem) { if let a = agent(sender.representedObject) { openChat(select: a.paneKey) } }
    @objc func menuJump() { DispatchQueue.main.async { self.openChat(fromMenuBar: true) } }
    @objc func menuSnack() { dropSnack() }
    @objc func menuActivity(_ sender: NSMenuItem) { pet.start(Activity.allCases[sender.tag]) }
    @objc func menuFollow() { pet.toggleFollow() }
    @objc func menuSleep() { if pet.state == .sleep { pet.poked() } else { pet.enter(.sleep) } }
}
