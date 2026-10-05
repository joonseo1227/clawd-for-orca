import Foundation
import Testing
@testable import Clawd

@Suite struct OrcaAgentTests {
    let nowMs = Date().timeIntervalSince1970 * 1000

    func agent(_ key: String, state: String, updatedAgo seconds: Double = 5, extra: [String: Any] = [:]) -> [String: Any] {
        ["paneKey": key, "state": state, "updatedAt": nowMs - seconds * 1000, "agentType": "claude"]
            .merging(extra) { $1 }
    }

    func psAnswer(_ worktrees: [[String: Any]]) throws -> Data {
        try JSONSerialization.data(withJSONObject: ["ok": true, "result": ["worktrees": worktrees]])
    }

    @Test func parsesWorktreesAndAgents() throws {
        let started = nowMs - 120_000
        let data = try psAnswer([
            ["displayName": "spookfish", "repo": "clawd", "isActive": true, "path": "/w/spookfish",
             "agents": [agent("tab1:leaf1", state: "blocked", extra: [
                "toolName": "Bash", "toolInput": "npm   test\n--watch=false", "prompt": "run tests",
                "lastAssistantMessage": "Running.", "stateStartedAt": started])]],
            ["displayName": "", "repo": "orca", "path": "/w/orca", "agents": [agent("tab2:leaf1", state: "working")]],
            ["displayName": "main", "repo": "website", "agents": [agent("tab3:leaf1", state: "done")]],
        ])
        let agents = try #require(OrcaAgent.parse(data))
        #expect(agents.map(\.paneKey) == ["tab1:leaf1", "tab2:leaf1", "tab3:leaf1"])
        // Name falls back to the repo when the worktree has no display name or is "main".
        #expect(agents.map(\.name) == ["spookfish", "orca", "website"])
        #expect(agents.map(\.worktreeActive) == [true, false, false])

        let first = agents[0]
        #expect(first.state == "blocked")
        #expect(first.tool == "Bash")
        #expect(first.path == "/w/spookfish")
        #expect(first.agentType == "claude")
        #expect(first.prompt == "run tests")
        let startedAt = try #require(first.stateStartedAt)
        #expect(abs(startedAt.timeIntervalSince1970 * 1000 - started) < 1)
        #expect(agents[1].stateStartedAt == nil)
    }

    @Test func staleLiveStatesBecomeIdle() throws {
        let stale = OrcaAgent.staleAfter + 60
        let data = try psAnswer([["displayName": "w", "agents": [
            agent("a:1", state: "working", updatedAgo: stale),
            agent("a:2", state: "blocked", updatedAgo: stale),
            agent("a:3", state: "done", updatedAgo: stale),
            agent("a:4", state: "working", updatedAgo: OrcaAgent.staleAfter - 60),
        ]]])
        let states = try #require(OrcaAgent.parse(data)).map(\.state)
        #expect(states == ["idle", "idle", "done", "working"])
    }

    @Test func skipsMalformedAgents() throws {
        let data = try psAnswer([["displayName": "w", "agents": [
            ["state": "working"],                 // no paneKey
            ["paneKey": "a:1"],                   // no state
            agent("a:2", state: "waiting"),
        ]]])
        #expect(try #require(OrcaAgent.parse(data)).map(\.paneKey) == ["a:2"])
    }

    @Test func rejectsAnswersWithoutWorktrees() throws {
        #expect(OrcaAgent.parse(nil) == nil)
        #expect(OrcaAgent.parse(Data("not json".utf8)) == nil)
        #expect(OrcaAgent.parse(try JSONSerialization.data(withJSONObject: ["ok": false, "error": ["code": "x"]])) == nil)
        #expect(OrcaAgent.parse(try psAnswer([])) == [])
    }

    @Test func chatSessionsHaveNoTerminal() throws {
        // Orca's chat sessions as `worktree.ps` lists them: the pane is named after the session,
        // and "structuredHostOwned" is only there when this Orca runs it.
        let data = try psAnswer([["displayName": "w", "agents": [
            agent("8c1f0e2a-tab:3b9d-leaf", state: "working"),
            agent("structured-agent-session-a1b2c3:5f0e6c1d-2b7a-4c3e-9d8f-0a1b2c3d4e5f", state: "blocked",
                  extra: ["structuredHostOwned": true]),
            agent("structured-agent-session-a1b2c3-reopened-2:9e8d7c6b-5a4f-4e3d-8c2b-1a0f9e8d7c6b", state: "waiting"),
        ]]])
        #expect(try #require(OrcaAgent.parse(data)).map(\.hasTerminal) == [true, false, false])
    }

    @Test func chatSessionsOpenTheirTab() throws {
        // A chat session's tab is "agent-session:<session id>", the pane name without its prefix.
        let data = try psAnswer([["displayName": "w", "worktreeId": "repo::/w", "agents": [
            agent("8c1f0e2a-tab:3b9d-leaf", state: "working"),
            agent("structured-agent-session-claude_70af9a41_998d:1f31e0a8", state: "done"),
        ]]])
        let agents = try #require(OrcaAgent.parse(data))
        #expect(agents.map(\.chatTabId) == [nil, "agent-session:claude_70af9a41_998d"])
        #expect(agents.map(\.worktreeId) == ["repo::/w", "repo::/w"])
    }

    func make(state: String, tool: String? = nil, input: String? = nil, message: String? = nil) -> OrcaAgent {
        OrcaAgent(paneKey: "t:l", name: "w", state: state, tool: tool, toolInput: input, lastMessage: message,
                  prompt: nil, stateStartedAt: nil, worktreeActive: false)
    }

    @Test func needsYou() {
        #expect(make(state: "blocked").needsYou)
        #expect(make(state: "waiting").needsYou)
        #expect(!make(state: "working").needsYou)
        #expect(!make(state: "done").needsYou)
        #expect(!make(state: "idle").needsYou)
    }

    @Test func askWhenBlockedIsToolAndInput() {
        #expect(make(state: "blocked", tool: "Bash", input: "rm -rf\n  build", message: "ignored").ask == "Bash: rm -rf build")
        #expect(make(state: "blocked", tool: "Bash").ask == "Bash")
        #expect(make(state: "blocked", input: "   ").ask == nil)
        #expect(make(state: "blocked").ask == nil)
    }

    @Test func askOtherwiseIsLastMessageTrimmed() throws {
        #expect(make(state: "waiting", tool: "Bash", message: "Which  one?\n1. A").ask == "Which one? 1. A")
        let long = make(state: "waiting", message: String(repeating: "word ", count: 100)).ask
        let ask = try #require(long)
        #expect(ask.count == 160)
        #expect(ask.hasSuffix("…"))
    }
}

@Suite struct FormattingTests {
    @Test func snippetCollapsesWhitespaceAndTruncates() {
        #expect(snippet(nil, 10) == nil)
        #expect(snippet(" \n\t ", 10) == nil)
        #expect(snippet("a  b\n\nc", 10) == "a b c")
        #expect(snippet("0123456789", 10) == "0123456789")
        #expect(snippet("0123456789A", 10) == "012345678…")
    }

    // The test runner's main bundle has no string tables, so these read the English source;
    // LocalizationTests checks the Korean.
    @Test func durationInMinutesAndHours() {
        #expect(duration(0) == "1 min")
        #expect(duration(59) == "1 min")
        #expect(duration(180) == "3 min")
        #expect(duration(59 * 60 + 59) == "59 min")
        #expect(duration(3600) == "1 hr 0 min")
        #expect(duration(3900) == "1 hr 5 min")
        #expect(duration(26 * 3600 + 60) == "26 hr 1 min")
    }

    @Test func agoIsRelativeToNow() {
        #expect(ago(Date()) == "just now")
        #expect(ago(Date().addingTimeInterval(-30)) == "just now")
        #expect(ago(Date().addingTimeInterval(-185)) == "3 min ago")
        #expect(ago(Date().addingTimeInterval(-3900)) == "1 hr 5 min ago")
    }
}

@Suite struct OrcaPairingTests {
    let payload: [String: String] = [
        "endpoint": "ws://192.168.0.23:6768/mobile?v=2",
        "deviceToken": "tok_abc123",
        "publicKeyB64": "MCowBQYDK2VuAyEA+/xyz==",
    ]

    func base64url(_ object: Any) throws -> String {
        try JSONSerialization.data(withJSONObject: object).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    @Test func acceptsBarePayload() throws {
        let pairing = try #require(OrcaPairing(code: "  " + base64url(payload) + "\n"))
        #expect(pairing.endpoint == payload["endpoint"])
        #expect(pairing.deviceToken == "tok_abc123")
        #expect(pairing.publicKeyB64 == payload["publicKeyB64"])
    }

    @Test func acceptsPairingURL() throws {
        let pairing = try #require(OrcaPairing(code: "orca://pair?code=" + base64url(payload)))
        #expect(pairing.deviceToken == "tok_abc123")
    }

    @Test func handlesEveryPaddingLength() throws {
        // Payload lengths 0, 1 and 2 mod 3 produce no, two and one padding characters.
        for pad in ["", "x", "xy"] {
            let p = payload.merging(["deviceToken": "t" + pad]) { $1 }
            #expect(OrcaPairing(code: try base64url(p))?.deviceToken == "t" + pad)
        }
    }

    @Test func rejectsGarbage() throws {
        #expect(OrcaPairing(code: "") == nil)
        #expect(OrcaPairing(code: "hello world!!") == nil)
        #expect(OrcaPairing(code: "orca://pair?code=%%%") == nil)
        #expect(OrcaPairing(code: Data("plain text".utf8).base64EncodedString()) == nil)
        // JSON without a device token.
        #expect(OrcaPairing(code: try base64url(["endpoint": "ws://h:1", "publicKeyB64": "k"])) == nil)
    }

    @Test func jsonRoundTrips() throws {
        let pairing = try #require(OrcaPairing(code: try base64url(payload)))
        let again = try #require(OrcaPairing(json: pairing.json))
        #expect(again.endpoint == pairing.endpoint)
        #expect(again.deviceToken == pairing.deviceToken)
        #expect(again.publicKeyB64 == pairing.publicKeyB64)
    }

    @Test func localEndpointUsesLoopbackKeepingPortAndPath() throws {
        func local(_ endpoint: String) throws -> String {
            try #require(OrcaPairing(json: JSONSerialization.data(withJSONObject:
                ["endpoint": endpoint, "deviceToken": "t", "publicKeyB64": "k"]))).localEndpoint
        }
        #expect(try local("ws://192.168.0.23:6768/mobile?v=2") == "ws://127.0.0.1:6768/mobile?v=2")
        #expect(try local("wss://mac.local:443/ws") == "wss://127.0.0.1:443/ws")
        #expect(try local("ws://10.0.0.2") == "ws://127.0.0.1")
        // Not a WebSocket URL: left alone.
        #expect(try local("http://192.168.0.23:6768") == "http://192.168.0.23:6768")
    }
}

@Suite struct TerminalMatchTests {
    func agent(pane: String, path: String, type: String = "claude") -> OrcaAgent {
        OrcaAgent(paneKey: pane, name: "x", state: "done", tool: nil, toolInput: nil, lastMessage: nil,
                  prompt: nil, stateStartedAt: nil, worktreeActive: false, path: path, agentType: type)
    }

    @Test func exactPaneWins() {
        let list: [[String: Any]] = [
            ["tabId": "tab", "leafId": "leaf", "handle": "term_a", "worktreePath": "/w"],
            ["tabId": "pty:1", "leafId": "pty:1", "handle": "term_b", "worktreePath": "/w", "agentIdentity": "claude"],
        ]
        #expect(OrcaClient.match(agent(pane: "tab:leaf", path: "/w"), in: list) == "term_a")
    }

    @Test func unmountedWorkspaceMatchesByFolder() {
        let list: [[String: Any]] = [
            ["tabId": "pty:9", "leafId": "pty:9", "handle": "term_osprey", "worktreePath": "/ws/osprey", "agentIdentity": "claude"],
            ["tabId": "pty:9", "leafId": "pty:9", "handle": "term_pangyo", "worktreePath": "/ws/pangyo", "agentIdentity": "claude"],
        ]
        #expect(OrcaClient.match(agent(pane: "t:l", path: "/ws/pangyo"), in: list) == "term_pangyo")
    }

    @Test func ambiguousFolderMatchesNothing() {
        let list: [[String: Any]] = [
            ["tabId": "pty:1", "handle": "term_a", "worktreePath": "/w", "agentIdentity": "claude"],
            ["tabId": "pty:1", "handle": "term_b", "worktreePath": "/w", "agentIdentity": "claude"],
        ]
        #expect(OrcaClient.match(agent(pane: "t:l", path: "/w"), in: list) == nil)
    }

    @Test func chatSessionMatchesNothing() {
        // The only terminal in its folder belongs to another agent.
        let list: [[String: Any]] = [
            ["tabId": "pty:1", "leafId": "pty:1", "handle": "term_a", "worktreePath": "/w", "agentIdentity": "claude"],
        ]
        #expect(OrcaClient.match(agent(pane: "structured-agent-session-a1:b2", path: "/w"), in: list) == nil)
    }
}

@MainActor @Suite struct ChatModeTests {
    func row(_ pane: String) -> ChatRow {
        ChatRow(agent: OrcaAgent(paneKey: pane, name: "w", state: "working", tool: nil, toolInput: nil, lastMessage: nil,
                                 prompt: nil, stateStartedAt: nil, worktreeActive: false),
                kind: .working, status: "")
    }

    @Test func terminalViewOnlyForAgentsWithATerminal() {
        let model = ChatModel()
        model.rows = [row("tab:leaf"), row("structured-agent-session-a1:b2")]
        model.mode = .terminal
        model.selected = "tab:leaf"
        #expect(model.showsTerminal)
        model.selected = "structured-agent-session-a1:b2"
        #expect(!model.showsTerminal)
        // The choice is kept for the next agent that has one.
        #expect(model.mode == .terminal)
        model.selected = "tab:leaf"
        #expect(model.showsTerminal)
    }
}
