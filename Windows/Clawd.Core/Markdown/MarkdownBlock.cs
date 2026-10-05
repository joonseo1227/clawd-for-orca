using System.Text.RegularExpressions;

namespace Clawd.Core.Markdown;

/// <summary>
/// Claude answers in Markdown. Lists, code blocks, headings and tables are split into blocks
/// here and laid out one by one; inline syntax is handled by <see cref="InlineMarkdown"/>.
/// </summary>
public abstract partial record MarkdownBlock
{
    public sealed record Heading(int Level, string Text) : MarkdownBlock;
    public sealed record Paragraph(string Text) : MarkdownBlock;
    public sealed record ListItem(string Marker, int Indent, string Text) : MarkdownBlock;
    public sealed record Code(string Language, string Text) : MarkdownBlock;
    public sealed record Quote(string Text) : MarkdownBlock;
    public sealed record Table(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : MarkdownBlock
    {
        public bool Equals(Table? other) => other is not null && Header.SequenceEqual(other.Header)
            && Rows.Count == other.Rows.Count && Rows.Zip(other.Rows).All(p => p.First.SequenceEqual(p.Second));
        public override int GetHashCode() => HashCode.Combine(Header.Count, Rows.Count);
    }
    public sealed record Rule : MarkdownBlock;

    [GeneratedRegex(@"^\|?\s*:?-{2,}")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^([-*+]|\d+[.)])\s+")]
    private static partial Regex ListMarker();

    public static List<MarkdownBlock> Parse(string source)
    {
        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var i = 0;

        void Flush()
        {
            if (paragraph.Count > 0) blocks.Add(new Paragraph(string.Join("\n", paragraph)));
            paragraph.Clear();
        }
        static List<string> Cells(string line)
        {
            var t = line.Trim();
            if (t.StartsWith('|')) t = t[1..];
            if (t.EndsWith('|')) t = t[..^1];
            return t.Split('|').Select(c => c.Trim()).ToList();
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var t = line.Trim();

            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                var language = t[3..];
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i++]);
                blocks.Add(new Code(language, string.Join("\n", code)));
                i++;
                continue;
            }
            if (t.Length == 0) { Flush(); i++; continue; }
            if (t is "---" or "***") { Flush(); blocks.Add(new Rule()); i++; continue; }
            var hashes = t.TakeWhile(c => c == '#').Count();
            if (hashes > 0 && hashes < t.Length && t[hashes] == ' ')
            {
                Flush();
                blocks.Add(new Heading(hashes, t[hashes..].Trim()));
                i++;
                continue;
            }
            // A table: a pipe row followed by a |---|---| separator row.
            if (t.StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1].Trim()))
            {
                Flush();
                var header = Cells(t);
                var rows = new List<IReadOnlyList<string>>();
                i += 2;
                while (i < lines.Length && lines[i].Trim().StartsWith('|')) rows.Add(Cells(lines[i++]));
                blocks.Add(new Table(header, rows));
                continue;
            }
            if (t.StartsWith("> ", StringComparison.Ordinal)) { Flush(); blocks.Add(new Quote(t[2..])); i++; continue; }
            var indent = line.TakeWhile(c => c == ' ').Count() / 2;
            if (ListMarker().Match(t) is { Success: true } m)
            {
                Flush();
                var raw = m.Value.Trim();
                var marker = "-*+".Contains(raw[0]) ? "•" : raw;
                blocks.Add(new ListItem(marker, indent, t[m.Length..]));
                i++;
                continue;
            }
            paragraph.Add(line);
            i++;
        }
        Flush();
        return blocks;
    }
}

/// <summary>A run of inline text and how it is set: **bold**, *italic*, ~~struck through~~, `code`,
/// [links](url), and these inside one another.</summary>
public sealed record InlineSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null, bool Strike = false);

public static partial class InlineMarkdown
{
    // An italic's closing star is neither after a star nor before one, so it isn't half of the
    // closing ** of a bold run inside it.
    [GeneratedRegex(@"(?<tick>`+)(?<code>.+?)\k<tick>|\*\*\*(?<bi>.+?)\*\*\*|\*\*(?<b>.+?)\*\*|__(?<b2>.+?)__|~~(?<s>.+?)~~|(?<![\w*])\*(?!\s)(?<i>.+?)(?<![\s*])\*(?![\w*])|(?<!\w)_(?!\s)(?<i2>.+?)(?<!\s)_(?!\w)|\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)\)")]
    private static partial Regex Token();

    /// <summary>Splits inline Markdown into spans; whitespace and line breaks are kept as written.</summary>
    public static List<InlineSpan> Parse(string text)
    {
        var spans = new List<InlineSpan>();
        Add(spans, text, new InlineSpan(""));
        return spans;
    }

    /// <summary>Adds the spans of <paramref name="text"/>, set as <paramref name="style"/> plus its own marks.</summary>
    private static void Add(List<InlineSpan> spans, string text, InlineSpan style)
    {
        var at = 0;
        foreach (Match m in Token().Matches(text))
        {
            if (m.Index > at) spans.Add(style with { Text = text[at..m.Index] });
            var g = m.Groups;
            if (g["code"].Success) spans.Add(style with { Text = g["code"].Value, Code = true });
            else if (g["bi"].Success) Add(spans, g["bi"].Value, style with { Bold = true, Italic = true });
            else if (g["b"].Success || g["b2"].Success) Add(spans, g["b"].Success ? g["b"].Value : g["b2"].Value, style with { Bold = true });
            else if (g["s"].Success) Add(spans, g["s"].Value, style with { Strike = true });
            else if (g["i"].Success || g["i2"].Success) Add(spans, g["i"].Success ? g["i"].Value : g["i2"].Value, style with { Italic = true });
            else Add(spans, g["text"].Value, style with { Link = g["url"].Value });
            at = m.Index + m.Length;
        }
        if (at < text.Length) spans.Add(style with { Text = text[at..] });
    }
}
