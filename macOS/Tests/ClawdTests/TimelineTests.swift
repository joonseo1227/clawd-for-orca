import Testing
@testable import Clawd

@MainActor @Suite struct TimelineEntryTests {
    func user(_ id: String) -> TimelineItem { TimelineItem(id: id, kind: .user, text: "prompt") }
    func text(_ id: String) -> TimelineItem { TimelineItem(id: id, kind: .text, text: "reply") }
    func tool(_ id: String) -> TimelineItem { TimelineItem(id: id, kind: .tool(name: "Bash", result: nil, failed: false), text: "ls") }
    func thinking(_ id: String, _ text: String = "") -> TimelineItem { TimelineItem(id: id, kind: .thinking, text: text) }

    @Test func groupsConsecutiveStepsBetweenMessages() {
        let items = [user("u"), thinking("t1", "Plan"), tool("a"), tool("b"), text("r1"), tool("c"), text("r2")]
        let entries = TimelineEntry.build(items, live: false)
        #expect(entries == [
            .item(items[0]),
            .steps([items[1], items[2], items[3]]),
            .item(items[4]),
            .item(items[5]),          // a single step stays a plain item
            .item(items[6]),
        ])
    }

    @Test func userAndTextBreakRuns() {
        let items = [tool("a"), tool("b"), user("u"), tool("c"), tool("d")]
        #expect(TimelineEntry.build(items, live: false) == [
            .steps([items[0], items[1]]), .item(items[2]), .steps([items[3], items[4]]),
        ])
    }

    @Test func emptyThinkingIsDroppedUnlessLatestWhileLive() {
        let items = [user("u"), thinking("t1"), tool("a"), thinking("t2")]
        #expect(TimelineEntry.build(items, live: false) == [.item(items[0]), .item(items[2])])
        #expect(TimelineEntry.build(items, live: true) == [.item(items[0]), .steps([items[2], items[3]])])
        // Thinking with a summary always shows.
        let summarized = [user("u"), thinking("t1", "Considering options"), text("r")]
        #expect(TimelineEntry.build(summarized, live: false).count == 3)
    }

    @Test func dropsEmptyThinkingWithoutSplittingTheRun() {
        let items = [tool("a"), thinking("t"), tool("b")]
        #expect(TimelineEntry.build(items, live: false) == [.steps([items[0], items[2]])])
    }

    @Test func idsAreStableAsTheRunGrows() {
        let before = TimelineEntry.build([user("u"), tool("a"), tool("b")], live: true)
        let after = TimelineEntry.build([user("u"), tool("a"), tool("b"), tool("c"), thinking("t")], live: true)
        #expect(before.map(\.id) == ["u", "steps-a"])
        #expect(after.map(\.id) == before.map(\.id))
        #expect(Set(after.map(\.id)).count == after.count)
    }

    @Test func emptyInput() {
        #expect(TimelineEntry.build([], live: true).isEmpty)
    }
}

@Suite struct MarkdownBlockTests {
    @Test func parsesMixedDocument() {
        let source = """
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
        """
        #expect(MarkdownBlock.parse(source) == [
            .heading(level: 1, text: "Summary"),
            .paragraph("The build is **green**\non every target."),
            .heading(level: 2, text: "Changes"),
            .listItem(marker: "•", indent: 0, text: "Parser"),
            .listItem(marker: "•", indent: 1, text: "Handles tables"),
            .listItem(marker: "•", indent: 0, text: "Tests"),
            .listItem(marker: "2)", indent: 0, text: "Second"),
            .listItem(marker: "10.", indent: 0, text: "Tenth"),
            .code(language: "swift", text: "let x = 1\n\n  print(x)"),
            .table(header: ["File", "Lines"], rows: [["a.swift", "10"], ["b.swift", "20"]]),
            .quote("Quoted note"),
            .rule,
            .rule,
        ])
    }

    @Test func hashWithoutSpaceIsNotAHeading() {
        #expect(MarkdownBlock.parse("#hashtag here") == [.paragraph("#hashtag here")])
        #expect(MarkdownBlock.parse("### Deep") == [.heading(level: 3, text: "Deep")])
    }

    @Test func unterminatedFenceRunsToTheEnd() {
        #expect(MarkdownBlock.parse("intro\n```\ncode line") == [.paragraph("intro"), .code(language: "", text: "code line")])
    }

    @Test func pipeLineWithoutSeparatorIsParagraph() {
        #expect(MarkdownBlock.parse("| not | a table |\nnext") == [.paragraph("| not | a table |\nnext")])
    }

    @Test func dashWithoutSpaceIsNotAListItem() {
        #expect(MarkdownBlock.parse("-flag value") == [.paragraph("-flag value")])
    }
}
