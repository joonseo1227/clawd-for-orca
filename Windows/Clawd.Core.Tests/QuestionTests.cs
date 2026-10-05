using System.Text.Json.Nodes;
using Clawd.Core.Orca;
using Clawd.Core.Transcripts;

namespace Clawd.Core.Tests;

public class QuestionTests
{
    /// <summary>Claude's AskUserQuestion input, as Claude Code 2.1 wrote it to the transcript.</summary>
    internal static JsonObject ColorAndFruits() => new()
    {
        ["questions"] = new JsonArray(
            new JsonObject
            {
                ["question"] = "Which color?", ["header"] = "Color", ["multiSelect"] = false,
                ["options"] = new JsonArray(
                    new JsonObject { ["label"] = "Red", ["description"] = "Red color" },
                    new JsonObject { ["label"] = "Green", ["description"] = "Green color" },
                    new JsonObject { ["label"] = "Blue", ["description"] = "Blue color" }),
            },
            new JsonObject
            {
                ["question"] = "Which fruits?", ["header"] = "Fruits", ["multiSelect"] = true,
                ["options"] = new JsonArray(
                    new JsonObject { ["label"] = "Apple", ["description"] = "Apple fruit" },
                    new JsonObject { ["label"] = "Banana", ["description"] = "Banana fruit" },
                    new JsonObject { ["label"] = "Cherry", ["description"] = "Cherry fruit" }),
            }),
    };

    /// <summary>The dialog as Orca's `terminal.read --screen` returned it (rules shortened).</summary>
    private static readonly string[] ColorScreen =
    [
        " ▐▛███▛█   Claude Code v2.1.289",
        "❯ Use the AskUserQuestion tool right now to ask me two questions",
        "────────────────────────────────────────",
        "←  ☐ Color  ☐ Fruits  ✔ Submit  →",
        "Which color?",
        "❯ 1. Red",
        "     Red color",
        "  2. Green",
        "     Green color",
        "  3. Blue",
        "     Blue color",
        "  4. Type something.",
        "────────────────────────────────────────",
        "  5. Chat about this",
        "Enter to select · Tab/Arrow keys to navigate · Esc to cancel",
    ];

    private static readonly string[] FruitScreen =
    [
        "────────────────────────────────────────",
        "←  ☒ Color  ☐ Fruits  ✔ Submit  →",
        "Which fruits?",
        "❯ 1. [ ] Apple",
        "         Apple fruit",
        "  2. [ ] Banana",
        "         Banana fruit",
        "  3. [ ] Cherry",
        "         Cherry fruit",
        "  4. [ ] Type something",
        "     Submit",
        "────────────────────────────────────────",
        "  5. Chat about this",
        "Enter to select · Tab/Arrow keys to navigate · Esc to cancel",
    ];

    private static readonly string[] ReviewScreen =
    [
        "────────────────────────────────────────",
        "←  ☒ Color  ☒ Fruits  ✔ Submit  →",
        "Review your answers",
        " ● Which color?",
        "   → Green",
        " ● Which fruits?",
        "   → Apple, Cherry",
        "Ready to submit your answers?",
        "❯ 1. Submit answers",
        "  2. Cancel",
    ];

    private readonly AgentQuestion _question = AgentQuestion.FromToolInput(ColorAndFruits(), "toolu_q")!;

    private static AgentQuestion.Answer Pick(params int[] picked) => new() { Picked = [.. picked] };

    [Fact]
    public void ParsesTranscriptInput()
    {
        Assert.Equal(new AgentQuestion.QuestionSource.Terminal("toolu_q"), _question.Source);
        Assert.Equal(["Which color?", "Which fruits?"], _question.Items.Select(i => i.Question));
        Assert.Equal([false, true], _question.Items.Select(i => i.MultiSelect));
        Assert.Equal(["Apple", "Banana", "Cherry"], _question.Items[1].Options.Select(o => o.Label));
        Assert.False(_question.AnswersOnClick);
        // A question without choices can't be mirrored; the whole dialog is left to Orca.
        var bare = new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["question"] = "Why?", ["options"] = new JsonArray() }) };
        Assert.Null(AgentQuestion.FromToolInput(bare, "t"));
    }

    [Fact]
    public void Completeness()
    {
        Assert.False(_question.Complete([Pick(1), new()]));
        Assert.True(_question.Complete([Pick(1), Pick(0, 2)]));
        Assert.True(_question.Complete([new() { Other = "Purple" }, new() { Other = "Kiwi" }]));
        // One choice in a single-choice question: a pick and typed text together are two.
        Assert.False(_question.Complete([new() { Picked = [1], Other = "Purple" }, Pick(0)]));
    }

    [Fact]
    public void Keystrokes()
    {
        var color = _question.Items[0];
        var fruits = _question.Items[1];
        Assert.Equal(["2"], QuestionDialog.Keys(color, Pick(1)));
        Assert.Equal(["4", "Deep purple", "\r"], QuestionDialog.Keys(color, new() { Other = "Deep\npurple " }));
        Assert.Equal(["1", "3", QuestionDialog.Next], QuestionDialog.Keys(fruits, Pick(2, 0)));
        const string down = QuestionDialog.Down;
        Assert.Equal(["1", down, down, down, "Kiwi", "\t", "\r"], QuestionDialog.Keys(fruits, new() { Picked = [0], Other = "Kiwi" }));
        // Boxes already ticked in Orca: only the differences are toggled.
        Assert.Equal(["1", "3", "4", QuestionDialog.Next], QuestionDialog.Keys(fruits, Pick(0, 1), new HashSet<int> { 1, 2, 3 }));
        Assert.Null(QuestionDialog.Keys(fruits, new() { Other = "Kiwi" }, new HashSet<int> { 3 }));
    }

    [Fact]
    public void TickedBoxes()
    {
        string[] screen = ["Pick toppings?", "❯ 1. [ ] Ham", "  2. [✔] Olive", "     Olive topping", "  3. [✔] Onion",
            "  4. [✔] Corn", "     Submit", "Enter to select · ↑/↓ to navigate · Esc to cancel"];
        Assert.Equal([1, 2, 3], QuestionDialog.Ticked(screen).Order());
        Assert.Empty(QuestionDialog.Ticked(FruitScreen));
    }

    [Fact]
    public void ReadsTheDialog()
    {
        Assert.Equal("Which color?", QuestionDialog.ShownQuestion(ColorScreen));
        Assert.True(QuestionDialog.Showing(_question.Items[0], ColorScreen));
        Assert.False(QuestionDialog.Showing(_question.Items[1], ColorScreen));
        Assert.True(QuestionDialog.Showing(_question.Items[1], FruitScreen));
        // The review lists the questions too, but asks none of them.
        Assert.False(QuestionDialog.Showing(_question.Items[1], ReviewScreen));
        Assert.True(QuestionDialog.Reviewing(ReviewScreen));
        Assert.False(QuestionDialog.Reviewing(FruitScreen));
        string[] permission = ["Bash command", "  rm -rf build", "Do you want to proceed?", "❯ 1. Yes", "  2. No", "Esc to cancel"];
        Assert.False(QuestionDialog.Showing(_question.Items[0], permission));
    }

    [Fact]
    public void WrappedQuestion()
    {
        var item = new AgentQuestion.Item("q1", "Which of these deployment targets should the release pipeline publish to first?", null, false,
            [new AgentQuestion.Option("1", "A", null)]);
        string[] screen = ["☐ Target", "Which of these deployment targets should the release", "pipeline publish to first?",
            "❯ 1. A", "  2. Type something.", "Enter to select · Esc to cancel"];
        Assert.True(QuestionDialog.Showing(item, screen));
    }

    [Fact]
    public void TranscriptReaderTracksTheQuestion()
    {
        using var file = new TempTranscript();
        file.Append(Entry.User("u1", "Ask me"));
        file.Append(Entry.Assistant("a1", new JsonArray(Entry.ToolUse("toolu_q", "AskUserQuestion", ColorAndFruits()))));
        var reader = new TranscriptReader(file.Path);
        reader.Timeline();
        Assert.Equal(_question, reader.Question);
        file.Append(Entry.User("r1", new JsonArray(Entry.ToolResult("toolu_q", "The user answered"))));
        reader.Timeline();
        Assert.Null(reader.Question);
    }
}

public class SessionTests
{
    /// <summary>An `agentSession.history` page, trimmed from what Orca 1.4 returned.</summary>
    private static JsonObject HistoryPage() => JsonNode.Parse("""
        {
          "fence": 1,
          "items": [
            {"itemId": "old", "revision": 0, "body": {"kind": "message", "role": "user", "blocks": [{"type": "text", "text": "Earlier"}]}},
            {"itemId": "orca:m1", "revision": 0, "body": {"kind": "message", "role": "user", "blocks": [{"type": "text", "text": "Ask me, then write the file"}]}},
            {"itemId": "turn", "revision": 2, "body": {"kind": "turn", "turnId": "t", "state": "running"}},
            {"itemId": "orca:tool1", "revision": 2, "body": {"kind": "tool-call", "name": "Bash", "state": "completed",
              "input": {"command": "echo hi > /tmp/x", "description": "Write test string"}, "output": {"head": "(Bash completed with no output)"}}},
            {"itemId": "orca:tool2", "revision": 1, "body": {"kind": "tool-call", "name": "Read", "state": "running", "input": {"file_path": "/repo/README.md"}}},
            {"itemId": "orca:prompt", "revision": 1, "body": {"kind": "question", "question": "Which color?",
              "options": [{"id": "q1:choice-1", "label": "Red"}, {"id": "q1:choice-2", "label": "Green"}],
              "questions": [{"id": "q1", "question": "Which color?", "header": "Color", "multiSelect": false,
                "options": [{"id": "q1:choice-1", "label": "Red", "description": "Choose red"}, {"id": "q1:choice-2", "label": "Green", "description": "Choose green"}]}],
              "resolution": {"state": "pending", "selectedOptionId": null}}},
            {"itemId": "orca:approval", "revision": 1, "body": {"kind": "approval", "title": "Bash", "displayName": "Bash command",
              "detail": "rm -rf build", "description": "Clean the build folder",
              "options": [{"id": "allow", "label": "Yes"}, {"id": "deny", "label": "No"}], "resolution": {"state": "pending"}}},
            {"itemId": "orca:a1", "revision": 2, "body": {"kind": "message", "role": "assistant", "blocks": [{"type": "text", "text": "Done."}]}}
          ]
        }
        """)!.AsObject();

    [Fact]
    public void Snapshot()
    {
        var s = OrcaSession.Snapshot(HistoryPage())!;
        Assert.Equal(1, s.Fence);
        Assert.Equal(["orca:m1", "orca:tool1", "orca:tool2", "orca:a1"], s.Timeline.Select(i => i.Id));
        Assert.Equal(TimelineKind.User, s.Timeline[0].Kind);
        Assert.Equal(TimelineItem.Tool("orca:tool1", "Bash", "Write test string", "(Bash completed with no output)"), s.Timeline[1]);
        Assert.Null(s.Timeline[2].Result);

        var q = s.Question!;
        Assert.Equal(new AgentQuestion.QuestionSource.Session("orca:prompt", 1), q.Source);
        Assert.Equal(["q1:choice-1", "q1:choice-2"], q.Items[0].Options.Select(o => o.Id));
        Assert.True(q.AnswersOnClick);

        var a = s.Approval!;
        Assert.Equal(["allow", "deny"], a.OptionIds);
        Assert.Equal("Bash command", a.Prompt.Title);
        Assert.Equal("rm -rf build", a.Prompt.Command);
    }

    [Fact]
    public void ResolvedItemsAreNotPending()
    {
        var page = HistoryPage();
        foreach (var item in page.Objects("items"))
            if (item.Obj("body") is { } body && body["resolution"] is not null) body["resolution"] = new JsonObject { ["state"] = "resolved" };
        var s = OrcaSession.Snapshot(page)!;
        Assert.Null(s.Question);
        Assert.Null(s.Approval);
    }

    /// <summary>Reference values from Orca's own computeAgentSessionPayloadFingerprint.</summary>
    [Fact]
    public void FingerprintMatchesOrca()
    {
        var body = new JsonObject
        {
            ["kind"] = "message", ["role"] = "user",
            ["blocks"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "안녕 \"quote\" back\\slash\nline\ttab \u0001 / é 😀" }),
        };
        Assert.Equal("d2c46d5c828a095feddba0d30f111aba94b80382387228eb9a0484b4fbbe259c",
            OrcaSession.Fingerprint("agentSession.send", "1791198875931-95060fb050cf484e9e3c3f7e3b715119", new JsonObject { ["body"] = body }));
        var fields = new JsonObject
        {
            ["itemId"] = "orca:x%3Ay", ["expectedRevision"] = 3,
            ["answers"] = new JsonArray(
                new JsonObject { ["questionId"] = "q1", ["optionIds"] = new JsonArray("q1:choice-2"), ["other"] = "Purple" },
                new JsonObject { ["questionId"] = "q2", ["optionIds"] = new JsonArray() }),
        };
        Assert.Equal("e0528cd12cfe4a83d8e63c0fe3347ea3b178a2a76fd05cfff1a241fff6dafcc0",
            OrcaSession.Fingerprint("agentSession.respondTo:question", "abcdefgh12", fields));
    }

    [Fact]
    public void SessionIds()
    {
        var chat = new OrcaAgent("structured-agent-session-1791198875931-abc:leaf", "x", "blocked", null, null, null, null, null, false);
        Assert.Equal("1791198875931-abc", chat.SessionId);
        Assert.True(chat.CanMessage);
        Assert.False(chat.HasTerminal);
        Assert.Equal("agent-session:1791198875931-abc", chat.ChatTabId);
        var terminal = new OrcaAgent("tab:leaf", "x", "waiting", "AskUserQuestion", null, null, null, null, false);
        Assert.Null(terminal.SessionId);
        Assert.True(terminal.CanMessage && terminal.AsksQuestion);
    }

    [Fact]
    public void QuestionsAreNotPermissions()
    {
        var attention = new Chat.Attention();
        var chat = new OrcaAgent("structured-agent-session-s1:leaf", "x", "blocked", null, null, null, null, DateTimeOffset.UtcNow, false);
        Assert.True(attention.AsksPermission(chat));
        attention.Questions[chat.PaneKey] = OrcaSession.Snapshot(HistoryPage())!.Question!;
        Assert.False(attention.AsksPermission(chat));
        Assert.Equal(Chat.RowKind.Question, attention.Rows([chat], new Dictionary<string, string>(), DateTimeOffset.UtcNow)[0].Kind);
        var terminal = new OrcaAgent("tab:leaf", "x", "waiting", "AskUserQuestion", null, null, null, DateTimeOffset.UtcNow, false);
        Assert.False(attention.AsksPermission(terminal));
    }
}
