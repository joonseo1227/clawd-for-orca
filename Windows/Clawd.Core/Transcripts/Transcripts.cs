using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Clawd.Core.Platform;

namespace Clawd.Core.Transcripts;

/// <summary>
/// Claude Code appends every step of a session to ~/.claude/projects/&lt;dir&gt;/&lt;sessionId&gt;.jsonl as
/// it happens: what it says between tool calls, each tool call and its result. Reading that
/// file lets the chat view follow an agent step by step instead of waiting for its final reply.
/// Thinking blocks usually come without their text (only a signature); sometimes the API sends
/// a short summary instead, which the view shows. An empty block can only say that Claude thought.
/// </summary>
public static partial class Transcripts
{
    /// <summary>~/.claude, or CLAUDE_CONFIG_DIR when Claude Code was told to use another folder.</summary>
    public static string ClaudeHome =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom ? custom : Path.Combine(AppPaths.Home, ".claude");

    // sessionId -> transcript path.
    private static readonly ConcurrentDictionary<string, string> PathCache = new();

    public sealed record Session(int Pid, string Id, string Cwd, double UpdatedAt, string? PaneKey);

    /// <summary>Live Claude Code processes, from the per-process files Claude Code keeps.</summary>
    public static List<Session> Sessions()
    {
        var result = new List<Session>();
        string[] files;
        try { files = Directory.GetFiles(Path.Combine(ClaudeHome, "sessions"), "*.json"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result; }
        foreach (var file in files)
        {
            JsonObject? json;
            try { json = Json.ParseObject(File.ReadAllBytes(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (json.Str("sessionId") is not { } id || json.Str("cwd") is not { } cwd) continue;
            var pid = json.Int("pid") ?? (int.TryParse(json.Str("pid"), out var p) ? p : 0);
            if (pid <= 0 || !IsAlive(pid)) continue;   // skip files left by exited processes
            var updated = json.Num("updatedAt") ?? (double.TryParse(json.Str("updatedAt"), out var u) ? u : 0);
            var env = ProcessEnvironment.Read(pid);
            result.Add(new Session(pid, id, cwd, updated, env.GetValueOrDefault("ORCA_PANE_KEY")));
        }
        return result;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static string? PathFor(string sessionId)
    {
        if (PathCache.TryGetValue(sessionId, out var cached)) return cached;
        string[] dirs;
        try { dirs = Directory.GetDirectories(Path.Combine(ClaudeHome, "projects")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        foreach (var dir in dirs)
        {
            var file = Path.Combine(dir, sessionId + ".jsonl");
            if (!File.Exists(file)) continue;
            PathCache[sessionId] = file;
            return file;
        }
        return null;
    }

    /// <summary>The transcript of the Claude Code session running in an Orca pane. Orca starts every pane's
    /// processes with ORCA_PANE_KEY set, so the pane identifies its session exactly, even when
    /// several tabs run Claude in the same folder. Without it, fall back to the folder and the
    /// last prompt.</summary>
    public static string? Locate(string paneKey, string path, string? prompt)
    {
        var all = Sessions();
        if (all.FirstOrDefault(s => s.PaneKey == paneKey) is { } exact) return PathFor(exact.Id);
        var candidates = all.Where(s => Paths.IsSameOrInside(s.Cwd, path)).ToList();
        if (candidates.Count <= 1) return candidates.Count == 1 ? PathFor(candidates[0].Id) : null;
        if (Text.Snippet(prompt, 60) is { } wanted)
        {
            foreach (var s in candidates)
            {
                if (PathFor(s.Id) is not { } file || LastPrompt(file) is not { } last) continue;
                if (Text.Snippet(last, 60) == wanted) return file;
            }
        }
        return PathFor(candidates.MaxBy(s => s.UpdatedAt)!.Id);
    }

    private static string? LastPrompt(string file)
    {
        foreach (var line in Tail(file, 400_000).AsEnumerable().Reverse())
        {
            if (!line.Contains("\"last-prompt\"", StringComparison.Ordinal)) continue;
            if (Json.ParseObject(line) is { } json) return json.Str("lastPrompt");
        }
        return null;
    }

    /// <summary>Complete lines from the last <paramref name="bytes"/> of the file.</summary>
    private static List<string> Tail(string file, int bytes)
    {
        try
        {
            using var stream = TranscriptReader.OpenShared(file);
            var start = Math.Max(0, stream.Length - bytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n').ToList();
            if (start > 0 && lines.Count > 0) lines.RemoveAt(0);   // cut mid-line
            return lines;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    // Readers by transcript path, most recently used last.
    private static readonly List<(string Path, TranscriptReader Reader)> Readers = [];
    private static readonly object ReadersGate = new();

    /// <summary>The incremental reader for a transcript, kept for the 16 most recently read files.</summary>
    public static TranscriptReader Reader(string file)
    {
        var path = System.IO.Path.GetFullPath(file);
        lock (ReadersGate)
        {
            var i = Readers.FindIndex(r => string.Equals(r.Path, path, Paths.Comparison));
            if (i >= 0)
            {
                var hit = Readers[i];
                Readers.RemoveAt(i);
                Readers.Add(hit);
                return hit.Reader;
            }
            var reader = new TranscriptReader(path);
            Readers.Add((path, reader));
            if (Readers.Count > 16) Readers.RemoveRange(0, Readers.Count - 16);
            return reader;
        }
    }

    /// <summary>Everything since the user's latest prompt, newest last. Goes through the cached reader,
    /// so repeated calls only parse what was appended since the last one.</summary>
    public static IReadOnlyList<TimelineItem> Timeline(string file) => Reader(file).Timeline().Items;

    // Tags Claude Code wraps around text it injects into user entries: reminders, slash-command
    // echoes and their output, background task notices, bash-mode input and output.
    [GeneratedRegex(@"<(system-reminder|command-name|command-message|command-args|command-contents|local-command-stdout|local-command-stderr|local-command-caveat|task-notification|bash-input|bash-stdout|bash-stderr|user-prompt-submit-hook)\b[^>]*>[\s\S]*?</\1>")]
    private static partial Regex Injected();

    /// <summary>The text of a real prompt the user typed, or null for tool results, interrupt markers and
    /// anything Claude Code injected (isMeta entries, tagged reminders and command echoes).</summary>
    public static string? PromptText(JsonObject entry)
    {
        if (entry.Str("type") != "user" || entry.Bool("isMeta") == true || entry.Obj("message") is not { } message) return null;
        var parts = new List<string>();
        if (message.Str("content") is { } s) parts.Add(s);
        foreach (var block in message.Objects("content"))
        {
            if (block.Str("type") == "tool_result") return null;
            if (block.Str("type") == "text" && block.Str("text") is { } t) parts.Add(t);
        }
        var text = string.Join("\n", parts
            .Select(part =>
            {
                var kept = Injected().Replace(part, "").Trim();
                return kept.StartsWith("[Request interrupted by user", StringComparison.Ordinal) ? "" : kept;
            })
            .Where(p => p.Length > 0));
        return text.Length == 0 ? null : text;
    }

    public static string? ResultText(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return Text.Snippet(s, 160);
        var texts = (content as JsonArray)?.OfType<JsonObject>().Select(o => o.Str("text")).OfType<string>() ?? [];
        return Text.Snippet(string.Join(" ", texts), 160);
    }

    /// <summary>One line saying what the tool call does.</summary>
    public static string Summary(string name, JsonObject input)
    {
        string? S(string key) => input.Str(key);
        var file = S("file_path") is { } f ? f.Split('/', '\\')[^1] : null;
        var line = name switch
        {
            "Bash" => S("description") ?? S("command"),
            "Read" or "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => file,
            "Grep" or "Glob" => S("pattern"),
            "WebSearch" => S("query"),
            "WebFetch" => S("url"),
            "Agent" or "Task" => S("description"),
            "TodoWrite" => Strings.Get("Tool_TodoWrite"),
            // Pick by key in a fixed order so the line stays put.
            _ => new[] { "description", "command", "query", "pattern", "url", "file_path", "path", "prompt", "text", "value" }
                .Concat(input.Select(kv => kv.Key).Order(StringComparer.Ordinal))
                .Select(S).FirstOrDefault(v => v is not null),
        };
        return Text.Snippet(line, 120) ?? "";
    }
}
