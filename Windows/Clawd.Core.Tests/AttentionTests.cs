using Clawd.Core.Chat;
using Clawd.Core.Orca;

namespace Clawd.Core.Tests;

[UseCulture("ko-KR")]
public class AttentionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static OrcaAgent A(string key, string state, string name = "w", DateTimeOffset? started = null, bool active = false) =>
        new(key, name, state, null, null, "last reply", null, started, active);

    [Fact]
    public void WaitingAgentNudgesUnlessLookedAt()
    {
        var att = new Attention { Looking = a => a.WorktreeActive };
        var events = att.Changed([A("p1", "working"), A("p2", "working")], [A("p1", "waiting"), A("p2", "blocked", active: true)], T0);
        Assert.Equal([new AttentionEvent.Waiting(A("p1", "waiting"), false), new AttentionEvent.Waiting(A("p2", "blocked", active: true), true)], events);
        Assert.Equal(T0, att.WaitingSince["p1"]);
        // Only the one the user isn't looking at is pending.
        Assert.Equal(["p1"], att.Pending([A("p1", "waiting"), A("p2", "blocked", active: true)], T0).Select(a => a.PaneKey));
    }

    [Fact]
    public void FinishingWorkIsAnnouncedWithItsDuration()
    {
        var att = new Attention();
        var events = att.Changed([A("p", "working", started: T0.AddMinutes(-3))], [A("p", "done")], T0);
        var done = Assert.IsType<AttentionEvent.Done>(Assert.Single(events));
        Assert.Equal(TimeSpan.FromMinutes(3), done.Took);
        Assert.True(att.Finished.ContainsKey("p"));
        // Starting again clears the "done" entry.
        att.Changed([A("p", "done")], [A("p", "working")], T0.AddSeconds(5));
        Assert.False(att.Finished.ContainsKey("p"));
    }

    [Fact]
    public void UnchangedStateDoesNothing()
    {
        var att = new Attention();
        att.Changed([], [A("p", "waiting")], T0);
        Assert.Empty(att.Changed([A("p", "waiting")], [A("p", "waiting")], T0.AddSeconds(10)));
    }

    [Fact]
    public void RemindsEveryThreeMinutesUntilAcknowledged()
    {
        var att = new Attention();
        att.Changed([], [A("p", "waiting")], T0);
        Assert.Empty(att.Changed([A("p", "waiting")], [A("p", "waiting")], T0 + Attention.NudgeEvery));
        Assert.Equal([new AttentionEvent.Nudge()], att.Changed([A("p", "waiting")], [A("p", "waiting")], T0 + Attention.NudgeEvery + TimeSpan.FromSeconds(1)));
        att.Handled(A("p", "waiting"));
        Assert.Empty(att.Pending([A("p", "waiting")], T0));
        Assert.Empty(att.Changed([A("p", "waiting")], [A("p", "waiting")], T0 + Attention.NudgeEvery * 3));
    }

    [Fact]
    public void AStateChangeForgetsTheAcknowledgementAndPrompt()
    {
        var att = new Attention();
        att.Changed([], [A("p", "waiting")], T0);
        att.Handled(A("p", "waiting"));
        att.Permissions["p"] = PermissionPrompt.Parse(PermissionPromptTests.BashDialog())!;
        att.Changed([A("p", "waiting")], [A("p", "working")], T0.AddSeconds(1));
        att.Changed([A("p", "working")], [A("p", "waiting")], T0.AddSeconds(2));
        Assert.Single(att.Pending([A("p", "waiting")], T0.AddSeconds(2)));
        Assert.False(att.Permissions.ContainsKey("p"));
    }

    [Fact]
    public void RecentDoneFromOrcaCountsUntilAnHourPasses()
    {
        var att = new Attention();
        Assert.Equal(T0.AddMinutes(-10), att.FinishedAt(A("p", "done", started: T0.AddMinutes(-10)), T0));
        Assert.Null(att.FinishedAt(A("p", "done", started: T0.AddMinutes(-61)), T0));
        att.Handled(A("p", "done"));
        Assert.Null(att.FinishedAt(A("p", "done", started: T0.AddMinutes(-10)), T0));
    }

    [Fact]
    public void RowsSortByUrgencyThenWaitThenName()
    {
        var att = new Attention();
        att.Changed([], [A("q", "waiting", "zeta"), A("b", "blocked", "alpha")], T0);
        att.WaitingSince["q"] = T0.AddMinutes(-5);
        OrcaAgent[] agents =
        [
            A("idle2", "idle", "agent 10"), A("idle1", "idle", "agent 2"), A("w", "working", "beta"),
            A("q", "waiting", "zeta"), A("b", "blocked", "alpha"), A("d", "done", "gamma", started: T0.AddMinutes(-1)),
        ];
        var rows = att.Rows(agents, new Dictionary<string, string> { ["w"] = "tab title" }, T0);
        Assert.Equal(["b", "q", "w", "d", "idle1", "idle2"], rows.Select(r => r.Id));
        Assert.Equal([RowKind.Permission, RowKind.Question, RowKind.Working, RowKind.Finished, RowKind.Resting, RowKind.Resting], rows.Select(r => r.Kind));
        Assert.Equal("답장 필요 · 5분 전", rows[1].Status);
        Assert.Equal("tab title", rows[2].Title);
        Assert.Equal("쉬는 중", rows[4].Status);
    }

    [Fact]
    public void WaitingCardSaysHowManyAndSince()
    {
        var card = Sign.Waiting([A("p", "waiting", "web"), A("q", "waiting")], null, T0.AddMinutes(-4), T0);
        Assert.Equal(SignTone.Urgent, card.Tone);
        Assert.Equal("답장 필요", card.Title);
        Assert.Equal("web", card.Name);
        Assert.Equal("last reply", card.Detail);
        Assert.Equal("외 1개 더 · 4분 전부터 기다리는 중 · 클릭해서 답하기", card.Hint);
        var permission = Sign.Waiting([A("p", "waiting")], PermissionPrompt.Parse(PermissionPromptTests.BashDialog()), T0, T0);
        Assert.Equal("권한 필요", permission.Title);
        Assert.Equal("Run the unit tests", permission.Detail);
    }

    [Fact]
    public void SummaryAndStatus()
    {
        Assert.Equal("Orca를 찾을 수 없어요", Sign.Summary(false, true, [], [], T0).Title);
        Assert.Equal("다들 쉬는 중", Sign.Summary(true, true, [], [], T0).Title);
        var s = Sign.Summary(true, true, [A("w", "working", "web") with { Tool = "Bash" }], [new Attention.FinishedTask(A("d", "done", "docs"), T0.AddMinutes(-2), null)], T0);
        Assert.Equal("작업 중 1개", s.Title);
        Assert.Equal("web  Bash\ndocs  2분 전 완료", s.Detail);
        Assert.Equal("확인 2", Sign.StatusTitle(true, 2, 1, 1));
        Assert.Equal("작업 1", Sign.StatusTitle(true, 0, 1, 1));
        Assert.Equal("", Sign.StatusTitle(false, 2, 1, 1));
        Assert.Equal("작업 완료", Sign.Done(A("d", "done"), TimeSpan.FromMinutes(2)).Title);
        Assert.Equal("2분 걸림 · 클릭해서 이어서 말하기", Sign.Done(A("d", "done"), TimeSpan.FromMinutes(2)).Hint);
        var chat = OrcaAgent.ChatSessionPrefix + "s1:leaf";
        Assert.Equal("2분 걸림 · 클릭해서 보기", Sign.Done(A(chat, "done"), TimeSpan.FromMinutes(2)).Hint);
        Assert.Equal("4분 전부터 기다리는 중 · 클릭해서 보기", Sign.Waiting([A(chat, "waiting")], null, T0.AddMinutes(-4), T0).Hint);
    }
}
