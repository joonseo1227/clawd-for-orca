using System.Text.Json.Nodes;
using Clawd.Core.Platform;

namespace Clawd.Core.Transcripts;

/// <summary>
/// Follows one transcript as it grows. It remembers how many bytes it has consumed and the
/// timeline built from them, so each call parses only the lines appended since the last one.
/// The first read (and any read after the file was truncated, replaced or jumped far ahead)
/// parses just the tail: the last megabyte, or six when the latest prompt isn't in it.
/// Calls are serialised on the reader, so it may be shared between threads.
/// </summary>
public sealed class TranscriptReader(string path)
{
    public string Path { get; } = path;
    private static readonly int[] Windows = [1_000_000, 6_000_000];
    private const int Shown = 80;
    private const int Kept = 240;      // items held before trimming the front back to Shown

    private bool _loaded;
    private FileIdentity? _identity;
    private long _offset;              // bytes consumed: always just past a newline
    private bool _sawPrompt;

    // The current turn: everything since the latest prompt (or the window start without one).
    private List<TimelineItem> _items = [];
    private int _trimmed;              // items dropped from the front of the turn
    private Dictionary<string, int> _toolIndex = [];   // tool_use id -> absolute item index
    private HashSet<string> _seen = [];                // entry uuids already in this turn
    private int _sequence;             // entries read since the last reset, for positional ids
    private List<TimelineItem> _last = [];

    /// <summary>Opens for reading while Claude Code keeps appending (and may replace or delete it).</summary>
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

    /// <summary>The timeline since the user's latest prompt, newest last, at most 80 items, and whether
    /// it differs from what the previous call returned.</summary>
    public (IReadOnlyList<TimelineItem> Items, bool Changed) Timeline()
    {
        lock (this)
        {
            FileStream stream;
            try { stream = OpenShared(Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Reset();
                return Finish();
            }
            using (stream)
            {
                var size = stream.Length;
                var identity = FileIdentity.Of(stream);
                // Rewritten, truncated, or so far ahead that reading the tail is cheaper.
                if (_loaded && (!identity.SameFile(_identity) || size < _offset || size - _offset > Windows[^1])) Reset();
                if (!_loaded)
                {
                    Load(stream, size);
                    _identity = identity;
                }
                else if (size > _offset && Read(stream, _offset, size) is { } data)
                {
                    _offset += Consume(data);
                }
            }
            return Finish();
        }
    }

    private (IReadOnlyList<TimelineItem>, bool) Finish()
    {
        var current = _items.Skip(Math.Max(0, _items.Count - Shown)).ToList();
        var changed = !current.SequenceEqual(_last);
        _last = current;
        return (current, changed);
    }

    private void Reset()
    {
        _loaded = false;
        _offset = 0;
        _sawPrompt = false;
        StartTurn();
        _sequence = 0;
    }

    private void StartTurn()
    {
        _items = [];
        _trimmed = 0;
        _toolIndex = [];
        _seen = [];
    }

    /// <summary>First read: the tail window, widened when the latest prompt isn't inside it.</summary>
    private void Load(FileStream stream, long size)
    {
        foreach (var window in Windows)
        {
            Reset();
            _loaded = true;
            _offset = size;    // a window with no line break in it: follow only what comes next
            var start = Math.Max(0, size - window);
            if (Read(stream, start, size) is not { } data) { _loaded = false; return; }   // retry next call
            var from = 0;
            if (start > 0)
            {
                // Cut mid-line: skip to the next full one.
                var nl = Array.IndexOf(data, (byte)'\n');
                if (nl < 0) continue;
                from = nl + 1;
            }
            _offset = start + from + Consume(data.AsSpan(from));
            if (_sawPrompt || start == 0) return;
        }
    }

    private static byte[]? Read(FileStream stream, long start, long end)
    {
        try
        {
            var buffer = new byte[end - start];
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException) { return null; }
    }

    /// <summary>Parses the complete lines in <paramref name="data"/> and returns how many bytes they took.
    /// A trailing line without its newline is still being written; it is left for the next read.</summary>
    private int Consume(ReadOnlySpan<byte> data)
    {
        var lineStart = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n') continue;
            if (i > lineStart) Ingest(data[lineStart..i]);
            lineStart = i + 1;
        }
        return lineStart;
    }

    private void Ingest(ReadOnlySpan<byte> line)
    {
        if (Json.ParseObject(line) is not { } entry || entry.Bool("isSidechain") == true) return;
        var type = entry.Str("type");
        if (type is not ("user" or "assistant")) return;
        _sequence++;
        var uuid = entry.Str("uuid");
        var key = uuid ?? $"#{_sequence}";
        var sequence = _sequence;
        string Id(int b) => uuid is not null ? $"{uuid}:{b}" : $"p{sequence}.{b}";
        var message = entry.Obj("message");

        if (type == "user")
        {
            if (Transcripts.PromptText(entry) is { } text)
            {
                _sawPrompt = true;
                StartTurn();
                _seen.Add(key);
                Append(new TimelineItem(uuid is not null ? $"{uuid}:u" : $"p{sequence}.u", TimelineKind.User, text));
                return;
            }
            if (!_seen.Add(key)) return;
            foreach (var block in message.Objects("content").Where(b => b.Str("type") == "tool_result"))
            {
                if (block.Str("tool_use_id") is not { } toolId || !_toolIndex.TryGetValue(toolId, out var abs)) continue;
                var i = abs - _trimmed;
                if (i < 0 || i >= _items.Count || _items[i].Kind != TimelineKind.Tool) continue;
                _items[i] = _items[i] with { Result = Transcripts.ResultText(block["content"]) ?? "", Failed = block.Bool("is_error") == true };
            }
            return;
        }

        if (!_seen.Add(key)) return;
        var blocks = message?["content"] as JsonArray ?? [];
        for (var b = 0; b < blocks.Count; b++)
        {
            if (blocks[b] is not JsonObject block) continue;
            switch (block.Str("type"))
            {
                case "text":
                    var text = (block.Str("text") ?? "").Trim();
                    if (text.Length > 0) Append(new TimelineItem(Id(b), TimelineKind.Text, text));
                    break;
                case "thinking" or "redacted_thinking":
                    var summary = (block.Str("thinking") ?? "").Trim();
                    if (_items.Count > 0 && _items[^1].Kind == TimelineKind.Thinking)
                    {
                        // Consecutive blocks are one stretch of thinking.
                        if (summary.Length > 0)
                        {
                            var last = _items[^1];
                            _items[^1] = last with { Text = last.Text + (last.Text.Length == 0 ? "" : "\n\n") + summary };
                        }
                    }
                    else
                    {
                        Append(new TimelineItem(Id(b), TimelineKind.Thinking, summary));
                    }
                    break;
                case "tool_use":
                    var name = block.Str("name") ?? Strings.Get("Tool_Unnamed");
                    var toolId = block.Str("id") ?? "x" + Id(b);
                    _toolIndex[toolId] = _trimmed + _items.Count;
                    Append(TimelineItem.Tool(toolId, name, Transcripts.Summary(name, block.Obj("input") ?? [])));
                    break;
            }
        }
    }

    /// <summary>Adds an item, dropping old ones from the front once a long turn has piled up enough
    /// that they can no longer be shown.</summary>
    private void Append(TimelineItem item)
    {
        _items.Add(item);
        if (_items.Count <= Kept) return;
        var drop = _items.Count - Shown;
        _items.RemoveRange(0, drop);
        _trimmed += drop;
        _toolIndex = _toolIndex.Where(kv => kv.Value >= _trimmed).ToDictionary();
    }
}
