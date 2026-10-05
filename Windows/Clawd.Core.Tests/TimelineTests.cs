using Clawd.Core.Markdown;
using Clawd.Core.Transcripts;

namespace Clawd.Core.Tests;

[UseCulture("ko-KR")]
public class TimelineEntryTests
{
    private static TimelineItem User(string id) => new(id, TimelineKind.User, "prompt");
    private static TimelineItem Reply(string id) => new(id, TimelineKind.Text, "reply");
    private static TimelineItem Tool(string id) => TimelineItem.Tool(id, "Bash", "ls");
    private static TimelineItem Thinking(string id, string text = "") => new(id, TimelineKind.Thinking, text);
    private static TimelineEntry One(TimelineItem i) => new TimelineEntry.Single(i);
    private static TimelineEntry Run(params TimelineItem[] i) => new TimelineEntry.Steps(i);

    [Fact]
    public void GroupsConsecutiveStepsBetweenMessages()
    {
        TimelineItem[] items = [User("u"), Thinking("t1", "Plan"), Tool("a"), Tool("b"), Reply("r1"), Tool("c"), Reply("r2")];
        Assert.Equal(
            [One(items[0]), Run(items[1], items[2], items[3]), One(items[4]), One(items[5]) /* a single step stays a plain item */, One(items[6])],
            TimelineEntry.Build(items, live: false));
    }

    [Fact]
    public void UserAndTextBreakRuns()
    {
        TimelineItem[] items = [Tool("a"), Tool("b"), User("u"), Tool("c"), Tool("d")];
        Assert.Equal([Run(items[0], items[1]), One(items[2]), Run(items[3], items[4])], TimelineEntry.Build(items, live: false));
    }

    [Fact]
    public void EmptyThinkingIsDroppedUnlessLatestWhileLive()
    {
        TimelineItem[] items = [User("u"), Thinking("t1"), Tool("a"), Thinking("t2")];
        Assert.Equal([One(items[0]), One(items[2])], TimelineEntry.Build(items, live: false));
        Assert.Equal([One(items[0]), Run(items[2], items[3])], TimelineEntry.Build(items, live: true));
        // Thinking with a summary always shows.
        Assert.Equal(3, TimelineEntry.Build([User("u"), Thinking("t1", "Considering options"), Reply("r")], live: false).Count);
    }

    [Fact]
    public void DropsEmptyThinkingWithoutSplittingTheRun()
    {
        TimelineItem[] items = [Tool("a"), Thinking("t"), Tool("b")];
        Assert.Equal([Run(items[0], items[2])], TimelineEntry.Build(items, live: false));
    }

    [Fact]
    public void IdsAreStableAsTheRunGrows()
    {
        var before = TimelineEntry.Build([User("u"), Tool("a"), Tool("b")], live: true);
        var after = TimelineEntry.Build([User("u"), Tool("a"), Tool("b"), Tool("c"), Thinking("t")], live: true);
        Assert.Equal(["u", "steps-a"], before.Select(e => e.Id));
        Assert.Equal(before.Select(e => e.Id), after.Select(e => e.Id));
        Assert.Equal(after.Count, after.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void EmptyInput() => Assert.Empty(TimelineEntry.Build([], live: true));

    [Fact]
    public void StepsSummaryCountsToolsAndThoughts()
    {
        TimelineItem[] steps =
        [
            TimelineItem.Tool("1", "Bash", "a", "ok"), TimelineItem.Tool("2", "Read", "b", "ok"), TimelineItem.Tool("3", "Bash", "c", "err", true),
            TimelineItem.Tool("4", "Grep", "d", "ok"), TimelineItem.Tool("5", "Edit", "e", "ok"), Thinking("t", "x"),
        ];
        var (title, detail, failed) = StepText.Summary(steps);
        Assert.Equal("단계 6개", title);
        Assert.Equal("Bash 2, Read 1, Grep 1, 외 1개, 생각 1", detail);
        Assert.True(failed);
        Assert.False(StepText.Summary([TimelineItem.Tool("1", "Bash", "a", null, true)]).Failed);   // still running
    }

    [Theory]
    [InlineData("Bash", StepSymbol.Terminal)]
    [InlineData("MultiEdit", StepSymbol.Pencil)]
    [InlineData("WebFetch", StepSymbol.Globe)]
    [InlineData("mcp__x__y", StepSymbol.Puzzle)]
    [InlineData("Whatever", StepSymbol.Wrench)]
    public void StepSymbols(string tool, StepSymbol symbol) => Assert.Equal(symbol, StepText.Symbol(tool));
}

public class MarkdownBlockTests
{
    [Fact]
    public void ParsesMixedDocument()
    {
        const string source = """
            # Summary
            The build is **green**
            on every target.

            ## Changes
            - Parser
              - Handles tables
            * Tests
            2) Second
            10. Tenth

            ```swift
            let x = 1

              print(x)
            ```
            | File | Lines |
            |:-----|------:|
            | a.swift | 10 |
            |b.swift|20|

            > Quoted note
            ---
            ***
            """;
        Assert.Equal<MarkdownBlock>(
        [
            new MarkdownBlock.Heading(1, "Summary"),
            new MarkdownBlock.Paragraph("The build is **green**\non every target."),
            new MarkdownBlock.Heading(2, "Changes"),
            new MarkdownBlock.ListItem("•", 0, "Parser"),
            new MarkdownBlock.ListItem("•", 1, "Handles tables"),
            new MarkdownBlock.ListItem("•", 0, "Tests"),
            new MarkdownBlock.ListItem("2)", 0, "Second"),
            new MarkdownBlock.ListItem("10.", 0, "Tenth"),
            new MarkdownBlock.Code("swift", "let x = 1\n\n  print(x)"),
            new MarkdownBlock.Table(["File", "Lines"], [["a.swift", "10"], ["b.swift", "20"]]),
            new MarkdownBlock.Quote("Quoted note"),
            new MarkdownBlock.Rule(),
            new MarkdownBlock.Rule(),
        ], MarkdownBlock.Parse(source));
    }

    [Fact]
    public void HashWithoutSpaceIsNotAHeading()
    {
        Assert.Equal<MarkdownBlock>([new MarkdownBlock.Paragraph("#hashtag here")], MarkdownBlock.Parse("#hashtag here"));
        Assert.Equal<MarkdownBlock>([new MarkdownBlock.Heading(3, "Deep")], MarkdownBlock.Parse("### Deep"));
    }

    [Fact]
    public void UnterminatedFenceRunsToTheEnd() =>
        Assert.Equal<MarkdownBlock>([new MarkdownBlock.Paragraph("intro"), new MarkdownBlock.Code("", "code line")], MarkdownBlock.Parse("intro\n```\ncode line"));

    [Fact]
    public void PipeLineWithoutSeparatorIsParagraph() =>
        Assert.Equal<MarkdownBlock>([new MarkdownBlock.Paragraph("| not | a table |\nnext")], MarkdownBlock.Parse("| not | a table |\nnext"));

    [Fact]
    public void DashWithoutSpaceIsNotAListItem() =>
        Assert.Equal<MarkdownBlock>([new MarkdownBlock.Paragraph("-flag value")], MarkdownBlock.Parse("-flag value"));

    [Fact]
    public void WindowsLineEndingsParseTheSame() =>
        Assert.Equal(MarkdownBlock.Parse("# A\n- b\n\ntext"), MarkdownBlock.Parse("# A\r\n- b\r\n\r\ntext"));

    [Fact]
    public void InlineSpans()
    {
        Assert.Equal(
        [
            new InlineSpan("Run "), new InlineSpan("npm test", Code: true), new InlineSpan(", it's "), new InlineSpan("green", Bold: true),
            new InlineSpan(" and "), new InlineSpan("fast", Italic: true), new InlineSpan(". See "), new InlineSpan("docs", Link: "https://e.com"),
        ], InlineMarkdown.Parse("Run `npm test`, it's **green** and *fast*. See [docs](https://e.com)"));
        // Snake_case and arithmetic are not emphasis.
        Assert.Equal([new InlineSpan("my_var_name and 2 * 3 * 4")], InlineMarkdown.Parse("my_var_name and 2 * 3 * 4"));
    }

    [Fact]
    public void NestedEmphasisAndStrikethrough()
    {
        Assert.Equal(
        [
            new InlineSpan("very ", Bold: true), new InlineSpan("much", Bold: true, Italic: true), new InlineSpan(" so", Bold: true),
        ], InlineMarkdown.Parse("**very *much* so**"));
        Assert.Equal(
        [
            new InlineSpan("a ", Italic: true), new InlineSpan("b", Bold: true, Italic: true), new InlineSpan(" c", Italic: true),
        ], InlineMarkdown.Parse("*a **b** c*"));
        Assert.Equal([new InlineSpan("all", Bold: true, Italic: true)], InlineMarkdown.Parse("***all***"));
        Assert.Equal(
        [
            new InlineSpan("old", Strike: true), new InlineSpan(" "), new InlineSpan("see ", Link: "https://e.com"), new InlineSpan("docs", Bold: true, Link: "https://e.com"),
            new InlineSpan(" "), new InlineSpan("x", Bold: true, Code: true),
        ], InlineMarkdown.Parse("~~old~~ [see **docs**](https://e.com) **`x`**"));
    }
}

public class FileDropTests
{
    [Fact]
    public void QuotesPathsShellsWouldSplit()
    {
        Assert.Equal("\"C:\\Users\\me\\My Files\\report (1).png\"", FileDrop.Quote(@"C:\Users\me\My Files\report (1).png"));
        Assert.Equal("\"C:\\tmp\\한글 파일.txt\"", FileDrop.Quote(@"C:\tmp\한글 파일.txt"));
        Assert.Equal("\"C:\\a&b\\c.txt\"", FileDrop.Quote(@"C:\a&b\c.txt"));
        Assert.Equal(@"C:\plain\path.cs", FileDrop.Quote(@"C:\plain\path.cs"));
    }

    [Fact]
    public void JoinsPathsSkippingEmptyOnes() =>
        Assert.Equal("\"C:\\a b.txt\" C:\\c.png", FileDrop.Text([@"C:\a b.txt", "", @"C:\c.png"]));

    [Fact]
    public void AppendsWithOneSpace()
    {
        Assert.Equal("C:\\x ", FileDrop.Append(@"C:\x", ""));
        Assert.Equal("look at C:\\x ", FileDrop.Append(@"C:\x", "look at"));
        Assert.Equal("look at C:\\x ", FileDrop.Append(@"C:\x", "look at "));
        Assert.Equal("keep", FileDrop.Append("", "keep"));
    }

    [Fact]
    public void PastesBracketed() => Assert.Equal("\u001b[200~C:\\x \u001b[201~", FileDrop.Paste(@"C:\x"));
}
