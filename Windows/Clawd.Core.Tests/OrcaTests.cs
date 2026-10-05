using System.Text.Json.Nodes;
using Clawd.Core.Orca;
using Clawd.Core.Pet;

namespace Clawd.Core.Tests;

public class OrcaAgentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static double NowMs => Now.ToUnixTimeMilliseconds();

    private static JsonObject Agent(string key, string state, double updatedAgo = 5, JsonObject? extra = null)
    {
        var o = new JsonObject { ["paneKey"] = key, ["state"] = state, ["updatedAt"] = NowMs - updatedAgo * 1000, ["agentType"] = "claude" };
        foreach (var (k, v) in extra ?? []) o[k] = v?.DeepClone();
        return o;
    }

    private static JsonObject PsAnswer(params JsonObject[] worktrees) =>
        new() { ["ok"] = true, ["result"] = new JsonObject { ["worktrees"] = new JsonArray(worktrees) } };

    [Fact]
    public void ParsesWorktreesAndAgents()
    {
        var started = NowMs - 120_000;
        var data = PsAnswer(
            new JsonObject
            {
                ["displayName"] = "spookfish", ["repo"] = "clawd", ["isActive"] = true, ["path"] = @"C:\w\spookfish",
                ["agents"] = new JsonArray(Agent("tab1:leaf1", "blocked", extra: new JsonObject
                {
                    ["toolName"] = "Bash", ["toolInput"] = "npm   test\n--watch=false", ["prompt"] = "run tests",
                    ["lastAssistantMessage"] = "Running.", ["stateStartedAt"] = started,
                })),
            },
            new JsonObject { ["displayName"] = "", ["repo"] = "orca", ["path"] = @"C:\w\orca", ["agents"] = new JsonArray(Agent("tab2:leaf1", "working")) },
            new JsonObject { ["displayName"] = "main", ["repo"] = "website", ["agents"] = new JsonArray(Agent("tab3:leaf1", "done")) });
        var agents = OrcaAgent.Parse(data, Now);
        Assert.NotNull(agents);
        Assert.Equal(["tab1:leaf1", "tab2:leaf1", "tab3:leaf1"], agents.Select(a => a.PaneKey));
        // Name falls back to the repo when the worktree has no display name or is "main".
        Assert.Equal(["spookfish", "orca", "website"], agents.Select(a => a.Name));
        Assert.Equal([true, false, false], agents.Select(a => a.WorktreeActive));

        var first = agents[0];
        Assert.Equal("blocked", first.State);
        Assert.Equal("Bash", first.Tool);
        Assert.Equal(@"C:\w\spookfish", first.Path);
        Assert.Equal("claude", first.AgentType);
        Assert.Equal("run tests", first.Prompt);
        Assert.NotNull(first.StateStartedAt);
        Assert.True(Math.Abs(first.StateStartedAt.Value.ToUnixTimeMilliseconds() - started) < 1);
        Assert.Null(agents[1].StateStartedAt);
    }

    [Fact]
    public void StaleLiveStatesBecomeIdle()
    {
        var stale = OrcaAgent.StaleAfter.TotalSeconds + 60;
        var data = PsAnswer(new JsonObject
        {
            ["displayName"] = "w",
            ["agents"] = new JsonArray(
                Agent("a:1", "working", stale), Agent("a:2", "blocked", stale),
                Agent("a:3", "done", stale), Agent("a:4", "working", OrcaAgent.StaleAfter.TotalSeconds - 60)),
        });
        Assert.Equal(["idle", "idle", "done", "working"], OrcaAgent.Parse(data, Now)!.Select(a => a.State));
    }

    [Fact]
    public void SkipsMalformedAgents()
    {
        var data = PsAnswer(new JsonObject
        {
            ["displayName"] = "w",
            ["agents"] = new JsonArray(new JsonObject { ["state"] = "working" }, new JsonObject { ["paneKey"] = "a:1" }, Agent("a:2", "waiting")),
        });
        Assert.Equal(["a:2"], OrcaAgent.Parse(data, Now)!.Select(a => a.PaneKey));
    }

    [Fact]
    public void ChatSessionsHaveNoTerminal()
    {
        // Orca's chat sessions as `worktree.ps` lists them: the pane is named after the session,
        // and "structuredHostOwned" is only there when this Orca runs it.
        var data = PsAnswer(new JsonObject
        {
            ["displayName"] = "w",
            ["agents"] = new JsonArray(
                Agent("8c1f0e2a-tab:3b9d-leaf", "working"),
                Agent("structured-agent-session-a1b2c3:5f0e6c1d-2b7a-4c3e-9d8f-0a1b2c3d4e5f", "blocked",
                    extra: new JsonObject { ["structuredHostOwned"] = true }),
                Agent("structured-agent-session-a1b2c3-reopened-2:9e8d7c6b-5a4f-4e3d-8c2b-1a0f9e8d7c6b", "waiting")),
        });
        Assert.Equal([true, false, false], OrcaAgent.Parse(data, Now)!.Select(a => a.HasTerminal));
    }

    [Fact]
    public void ChatSessionsOpenTheirTab()
    {
        // A chat session's tab is "agent-session:<session id>", the pane name without its prefix.
        var data = PsAnswer(new JsonObject
        {
            ["displayName"] = "w",
            ["worktreeId"] = "repo::C:\\w",
            ["agents"] = new JsonArray(
                Agent("8c1f0e2a-tab:3b9d-leaf", "working"),
                Agent("structured-agent-session-claude_70af9a41_998d:1f31e0a8", "done")),
        });
        var agents = OrcaAgent.Parse(data, Now)!;
        Assert.Equal([null, "agent-session:claude_70af9a41_998d"], agents.Select(a => a.ChatTabId));
        Assert.Equal(["repo::C:\\w", "repo::C:\\w"], agents.Select(a => a.WorktreeId));
    }

    [Fact]
    public void RejectsAnswersWithoutWorktrees()
    {
        Assert.Null(OrcaAgent.Parse((byte[]?)null));
        Assert.Null(OrcaAgent.Parse("not json"u8.ToArray()));
        Assert.Null(OrcaAgent.Parse(new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "x" } }));
        Assert.Empty(OrcaAgent.Parse(PsAnswer())!);
    }

    private static OrcaAgent Make(string state, string? tool = null, string? input = null, string? message = null) =>
        new("t:l", "w", state, tool, input, message, null, null, false);

    [Fact]
    public void NeedsYou()
    {
        Assert.True(Make("blocked").NeedsYou);
        Assert.True(Make("waiting").NeedsYou);
        Assert.False(Make("working").NeedsYou);
        Assert.False(Make("done").NeedsYou);
        Assert.False(Make("idle").NeedsYou);
    }

    [Fact]
    public void AskWhenBlockedIsToolAndInput()
    {
        Assert.Equal("Bash: rm -rf build", Make("blocked", "Bash", "rm -rf\n  build", "ignored").Ask);
        Assert.Equal("Bash", Make("blocked", "Bash").Ask);
        Assert.Null(Make("blocked", input: "   ").Ask);
        Assert.Null(Make("blocked").Ask);
    }

    [Fact]
    public void AskOtherwiseIsLastMessageTrimmed()
    {
        Assert.Equal("Which one? 1. A", Make("waiting", "Bash", message: "Which  one?\n1. A").Ask);
        var ask = Make("waiting", message: string.Concat(Enumerable.Repeat("word ", 100))).Ask;
        Assert.NotNull(ask);
        Assert.Equal(160, ask.Length);
        Assert.EndsWith("…", ask);
    }

    [Theory]
    [InlineData("Bash", Activity.Build)]
    [InlineData("Edit", Activity.Type)]
    [InlineData("Grep", Activity.Think)]
    [InlineData("Task", Activity.Juggle)]
    [InlineData("mcp__github__search", Activity.LookAround)]
    [InlineData(null, Activity.Type)]
    public void ActivityMirrorsTool(string? tool, Activity expected) => Assert.Equal(expected, Make("working", tool).Activity);
}

[UseCulture("ko-KR")]
public class FormattingTests
{
    [Fact]
    public void SnippetCollapsesWhitespaceAndTruncates()
    {
        Assert.Null(Text.Snippet(null, 10));
        Assert.Null(Text.Snippet(" \n\t ", 10));
        Assert.Equal("a b c", Text.Snippet("a  b\n\nc", 10));
        Assert.Equal("0123456789", Text.Snippet("0123456789", 10));
        Assert.Equal("012345678…", Text.Snippet("0123456789A", 10));
        Assert.Equal("가나다라…", Text.Snippet("가나다라마바", 5));
    }

    [Fact]
    public void DurationInMinutesAndHours()
    {
        Assert.Equal("1분", Text.Duration(0));
        Assert.Equal("1분", Text.Duration(59));
        Assert.Equal("3분", Text.Duration(180));
        Assert.Equal("59분", Text.Duration(59 * 60 + 59));
        Assert.Equal("1시간 0분", Text.Duration(3600));
        Assert.Equal("1시간 5분", Text.Duration(3900));
        Assert.Equal("26시간 1분", Text.Duration(26 * 3600 + 60));
    }

    [Fact]
    public void AgoIsRelativeToNow()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal("방금", Text.Ago(now, now));
        Assert.Equal("방금", Text.Ago(now.AddSeconds(-30), now));
        Assert.Equal("3분 전", Text.Ago(now.AddSeconds(-185), now));
        Assert.Equal("1시간 5분 전", Text.Ago(now.AddSeconds(-3900), now));
    }

    [Fact]
    public void TitlesLoseTheirSpinner()
    {
        Assert.Equal("Fix parser", Text.WithoutSpinner("✳ Fix parser"));
        Assert.Equal("Fix parser", Text.WithoutSpinner("◑ Fix parser"));
        Assert.Equal("Fix parser", Text.WithoutSpinner("Fix parser"));
        Assert.Equal("1 thing", Text.WithoutSpinner("1 thing"));
    }
}

public class OrcaPairingTests
{
    private static readonly JsonObject Payload = new()
    {
        ["endpoint"] = "ws://192.168.0.23:6768/mobile?v=2",
        ["deviceToken"] = "tok_abc123",
        ["publicKeyB64"] = "MCowBQYDK2VuAyEA+/xyz==",
    };

    private static string Base64Url(JsonObject o) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(o.ToJsonString())).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    [Fact]
    public void AcceptsBarePayload()
    {
        var pairing = OrcaPairing.FromCode("  " + Base64Url(Payload) + "\n");
        Assert.NotNull(pairing);
        Assert.Equal("ws://192.168.0.23:6768/mobile?v=2", pairing.Endpoint);
        Assert.Equal("tok_abc123", pairing.DeviceToken);
        Assert.Equal("MCowBQYDK2VuAyEA+/xyz==", pairing.PublicKeyB64);
    }

    [Fact]
    public void AcceptsPairingUrl() => Assert.Equal("tok_abc123", OrcaPairing.FromCode("orca://pair?code=" + Base64Url(Payload))?.DeviceToken);

    [Fact]
    public void HandlesEveryPaddingLength()
    {
        // Payload lengths 0, 1 and 2 mod 3 produce no, two and one padding characters.
        foreach (var pad in new[] { "", "x", "xy" })
        {
            var p = (JsonObject)Payload.DeepClone();
            p["deviceToken"] = "t" + pad;
            Assert.Equal("t" + pad, OrcaPairing.FromCode(Base64Url(p))?.DeviceToken);
        }
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.Null(OrcaPairing.FromCode(""));
        Assert.Null(OrcaPairing.FromCode("hello world!!"));
        Assert.Null(OrcaPairing.FromCode("orca://pair?code=%%%"));
        Assert.Null(OrcaPairing.FromCode(Convert.ToBase64String("plain text"u8.ToArray())));
        // JSON without a device token.
        Assert.Null(OrcaPairing.FromCode(Base64Url(new JsonObject { ["endpoint"] = "ws://h:1", ["publicKeyB64"] = "k" })));
    }

    [Fact]
    public void JsonRoundTrips()
    {
        var pairing = OrcaPairing.FromCode(Base64Url(Payload))!;
        Assert.Equal(pairing, OrcaPairing.FromJson(pairing.ToJson()));
    }

    [Theory]
    [InlineData("ws://192.168.0.23:6768/mobile?v=2", "ws://127.0.0.1:6768/mobile?v=2")]
    [InlineData("wss://mac.local:443/ws", "wss://127.0.0.1:443/ws")]
    [InlineData("ws://10.0.0.2", "ws://127.0.0.1")]
    [InlineData("ws://[fe80::1]:6768/m", "ws://127.0.0.1:6768/m")]
    // Not a WebSocket URL: left alone.
    [InlineData("http://192.168.0.23:6768", "http://192.168.0.23:6768")]
    public void LocalEndpointUsesLoopbackKeepingPortAndPath(string endpoint, string expected) =>
        Assert.Equal(expected, new OrcaPairing(endpoint, "t", "k").LocalEndpoint);
}

public class TerminalMatchTests
{
    private static OrcaAgent Agent(string pane, string path, string type = "claude") =>
        new(pane, "x", "done", null, null, null, null, null, false, path, type);

    private static JsonObject T(string tab, string? leaf, string handle, string path, string? identity = null)
    {
        var o = new JsonObject { ["tabId"] = tab, ["handle"] = handle, ["worktreePath"] = path };
        if (leaf is not null) o["leafId"] = leaf;
        if (identity is not null) o["agentIdentity"] = identity;
        return o;
    }

    [Fact]
    public void ExactPaneWins() =>
        Assert.Equal("term_a", OrcaClient.Match(Agent("tab:leaf", @"C:\w"),
            [T("tab", "leaf", "term_a", @"C:\w"), T("pty:1", "pty:1", "term_b", @"C:\w", "claude")]));

    [Fact]
    public void UnmountedWorkspaceMatchesByFolder() =>
        Assert.Equal("term_pangyo", OrcaClient.Match(Agent("t:l", @"C:\ws\pangyo"),
            [T("pty:9", "pty:9", "term_osprey", @"C:\ws\osprey", "claude"), T("pty:9", "pty:9", "term_pangyo", @"C:\ws\pangyo", "claude")]));

    [Fact]
    public void AmbiguousFolderMatchesNothing() =>
        Assert.Null(OrcaClient.Match(Agent("t:l", "/w"), [T("pty:1", null, "term_a", "/w", "claude"), T("pty:1", null, "term_b", "/w", "claude")]));

    [Fact]
    public void OtherAgentTypeInTheFolderIsNotAMatch() =>
        Assert.Null(OrcaClient.Match(Agent("t:l", "/w"), [T("pty:1", null, "term_a", "/w", "codex")]));

    [Fact]
    public void NoPathNoFallback() => Assert.Null(OrcaClient.Match(Agent("t:l", ""), [T("pty:1", null, "term_a", "", "claude")]));

    [Fact]
    public void ChatSessionMatchesNothing() =>
        // The only terminal in its folder belongs to another agent.
        Assert.Null(OrcaClient.Match(Agent("structured-agent-session-a1:b2", "/w"), [T("pty:1", "pty:1", "term_a", "/w", "claude")]));
}
