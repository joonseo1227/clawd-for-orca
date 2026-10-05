using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Clawd.Core.Transcripts;

namespace Clawd.Core.Orca;

// A session started in Orca's chat runs without a terminal: Orca keeps its conversation as a
// history of items and takes messages and answers through `agentSession.*` calls, the same ones
// its own chat view and phone app use. Each call that changes the session carries an envelope: a
// fresh operation id, the session's fence (bumped whenever its host restarts) and a SHA-256 over
// what the call does, which Orca recomputes and must match.

/// <summary>What Clawd shows of a chat session: the latest turn, and whatever it is waiting on.</summary>
public sealed record SessionSnapshot(int Fence, IReadOnlyList<TimelineItem> Timeline, AgentQuestion? Question, SessionApproval? Approval);

/// <summary>A pending permission request in a chat session, drawn like a terminal's dialog.</summary>
public sealed class SessionApproval(string itemId, int revision, PermissionPrompt prompt, IReadOnlyList<string> optionIds) : IEquatable<SessionApproval>
{
    public string ItemId { get; } = itemId;
    public int Revision { get; } = revision;
    public PermissionPrompt Prompt { get; } = prompt;
    /// <summary>By the prompt's option number, from 1.</summary>
    public IReadOnlyList<string> OptionIds { get; } = optionIds;

    public bool Equals(SessionApproval? o) => o is not null && ItemId == o.ItemId && Revision == o.Revision && Prompt.Equals(o.Prompt) && OptionIds.SequenceEqual(o.OptionIds);
    public override bool Equals(object? obj) => Equals(obj as SessionApproval);
    public override int GetHashCode() => HashCode.Combine(ItemId, Revision);

    public static SessionApproval? Parse(JsonObject item)
    {
        var body = item.Obj("body");
        if (body.Str("kind") != "approval" || body.Obj("resolution").Str("state") != "pending"
            || item.Str("itemId") is not { } itemId || item.Int("revision") is not { } revision) return null;
        var raw = body.Objects("options").Select(o => o.Str("id") is { } id && o.Str("label") is { } label ? (id, label) : default)
            .Where(o => o.id is not null).ToList();
        if (raw.Count == 0) return null;
        var title = body.Str("displayName") ?? body.Str("title") ?? "";
        var lines = new[] { body.Str("detail"), body.Str("description") }.OfType<string>()
            .SelectMany(s => s.Split(['\r', '\n'])).Where(s => s.Trim().Length > 0);
        var prompt = new PermissionPrompt(body.Str("title") ?? title, [title, .. lines],
            raw.Select((o, i) => new PermissionPrompt.Option(i + 1, o.label)).ToList());
        return new SessionApproval(itemId, revision, prompt, raw.Select(o => o.id).ToList());
    }
}

public static class OrcaSession
{
    /// <summary>Items read per look; a turn longer than this shows its latest part.</summary>
    public const int HistoryLimit = 120;

    /// <summary>The snapshot from an <c>agentSession.history</c> page.</summary>
    public static SessionSnapshot? Snapshot(JsonObject? page)
    {
        if (page.Int("fence") is not { } fence) return null;
        var items = page.Objects("items").ToList();
        items.Reverse();
        var question = items.Select(AgentQuestion.FromSessionItem).FirstOrDefault(q => q is not null);
        var approval = items.Select(SessionApproval.Parse).FirstOrDefault(a => a is not null);
        items.Reverse();
        return new SessionSnapshot(fence, Timeline(items), question, approval);
    }

    /// <summary>The turn since the latest user message, like a transcript's.</summary>
    public static List<TimelineItem> Timeline(IEnumerable<JsonObject> items)
    {
        var result = new List<TimelineItem>();
        foreach (var item in items)
        {
            if (item.Str("itemId") is not { } id || item.Obj("body") is not { } body) continue;
            switch (body.Str("kind"))
            {
                case "message":
                    var text = string.Join("\n", body.Objects("blocks").Select(b => b.Str("text")).OfType<string>()).Trim();
                    if (body.Str("role") == "user")
                    {
                        result.Clear();
                        if (text.Length > 0) result.Add(new TimelineItem(id, TimelineKind.User, text));
                    }
                    else if (text.Length > 0)
                    {
                        result.Add(new TimelineItem(id, TimelineKind.Text, text));
                    }
                    break;
                case "tool-call":
                    var name = body.Str("name") ?? Strings.Get("Tool_Unnamed");
                    var state = body.Str("state") ?? "";
                    var done = state is not ("running" or "pending");
                    result.Add(TimelineItem.Tool(id, name, Transcripts.Transcripts.Summary(name, body.Obj("input") ?? []),
                        done ? Text.Snippet(body.Obj("output").Str("head"), 160) ?? "" : null,
                        state is "failed" or "error" or "errored"));
                    break;
            }
        }
        return result;
    }

    // MARK: Envelope

    public static string OperationId() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");

    /// <summary>Orca's payload fingerprint: SHA-256 over {method, sessionId, fields} as JSON with keys
    /// sorted at every depth and absent fields left out.</summary>
    public static string Fingerprint(string method, string sessionId, JsonObject fields)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(new JsonObject
        {
            ["method"] = method, ["sessionId"] = sessionId, ["fields"] = fields.DeepClone(),
        })));
        return Convert.ToHexStringLower(digest);
    }

    public static JsonObject Envelope(string method, string sessionId, int fence, JsonObject fields) => new()
    {
        ["sessionId"] = sessionId,
        ["clientOperationId"] = OperationId(),
        ["expectedRuntimeFence"] = fence,
        ["payloadFingerprint"] = Fingerprint(method, sessionId, fields),
    };

    /// <summary>The value as JavaScript's JSON.stringify writes it, keys sorted.</summary>
    public static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.Where(kv => kv.Value is not null).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Quote(kv.Key) + ":" + Canonical(kv.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        JsonValue v when v.TryGetValue<string>(out var s) => Quote(s),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<int>(out var i) => i.ToString(CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };

    /// <summary>A JSON string literal: only quotes, backslashes and control characters are escaped.</summary>
    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case < ' ': sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }
}

public sealed partial class OrcaClient
{
    /// <summary>The latest part of a chat session's history.</summary>
    public async Task<SessionSnapshot?> SessionAsync(string sessionId)
    {
        if (TestHooks.FakeOrca is not null) return null;
        var outcome = await Runtime.RequestAsync("agentSession.history", new JsonObject
        {
            ["sessionId"] = sessionId, ["direction"] = "tail", ["limit"] = OrcaSession.HistoryLimit,
        }).ConfigureAwait(false);
        return outcome.Status == RuntimeStatus.Ok ? OrcaSession.Snapshot(OrcaRuntime.Result(outcome.Frame).Obj("page")) : null;
    }

    /// <summary>Sends a message the way Orca's chat does; Orca queues it while the agent is working.</summary>
    public async Task<bool> SessionSendAsync(string text, string sessionId)
    {
        var fields = new JsonObject
        {
            ["body"] = new JsonObject
            {
                ["kind"] = "message", ["role"] = "user",
                ["blocks"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            },
        };
        return await MutateAsync("agentSession.send", "agentSession.send", sessionId, fields).ConfigureAwait(false) == OrcaWatcher.AnswerResult.Sent;
    }

    public Task<OrcaWatcher.AnswerResult> SessionApproveAsync(SessionApproval approval, int number, string sessionId)
    {
        if (number < 1 || number > approval.OptionIds.Count) return Task.FromResult(OrcaWatcher.AnswerResult.Failed);
        var fields = new JsonObject
        {
            ["itemId"] = approval.ItemId, ["expectedRevision"] = approval.Revision, ["optionId"] = approval.OptionIds[number - 1],
        };
        return MutateAsync("agentSession.respondToApproval", "agentSession.respondTo:approval", sessionId, fields);
    }

    public Task<OrcaWatcher.AnswerResult> SessionAnswerAsync(AgentQuestion question, IReadOnlyList<AgentQuestion.Answer> answers, string sessionId)
    {
        if (question.Source is not AgentQuestion.QuestionSource.Session(var itemId, var revision))
            return Task.FromResult(OrcaWatcher.AnswerResult.Failed);
        var wire = new JsonArray();
        foreach (var (item, a) in question.Items.Zip(answers))
        {
            var entry = new JsonObject
            {
                ["questionId"] = item.Id,
                ["optionIds"] = new JsonArray(a.Picked.Order().Where(j => j < item.Options.Count).Select(j => (JsonNode)item.Options[j].Id).ToArray()),
            };
            if (a.OtherText.Length > 0) entry["other"] = a.OtherText;
            wire.Add(entry);
        }
        var fields = new JsonObject { ["itemId"] = itemId, ["expectedRevision"] = revision, ["answers"] = wire };
        return MutateAsync("agentSession.respondToQuestion", "agentSession.respondTo:question", sessionId, fields);
    }

    /// <summary>Runs one changing call at the session's current fence. Orca answers a refused one
    /// (stale revision, already answered, host restarted) with <c>ok: false</c> inside a successful reply.</summary>
    private async Task<OrcaWatcher.AnswerResult> MutateAsync(string method, string fingerprinted, string sessionId, JsonObject fields)
    {
        if (await SessionAsync(sessionId).ConfigureAwait(false) is not { } snapshot) return OrcaWatcher.AnswerResult.Failed;
        var parameters = (JsonObject)fields.DeepClone();
        parameters["envelope"] = OrcaSession.Envelope(fingerprinted, sessionId, snapshot.Fence, fields);
        var outcome = await Runtime.RequestAsync(method, parameters, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case RuntimeStatus.Ok:
                if (OrcaRuntime.Result(outcome.Frame).Bool("ok") == true) return OrcaWatcher.AnswerResult.Sent;
                Log.Debug(() => $"{method} refused: {outcome.Frame?.ToJsonString()}");
                return OrcaWatcher.AnswerResult.Gone;
            case RuntimeStatus.Unreachable:
                return OrcaWatcher.AnswerResult.Failed;
            default:
                Log.Debug(() => $"{method} failed: {outcome.Code ?? "-"} {outcome.Message ?? ""}");
                return OrcaWatcher.AnswerResult.Failed;
        }
    }
}
