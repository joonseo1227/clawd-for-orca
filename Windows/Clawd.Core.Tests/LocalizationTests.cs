using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Clawd.Core.Chat;
using Clawd.Core.Orca;
using Clawd.Core.Pet;
using Clawd.Core.Transcripts;
using Xunit.Sdk;

namespace Clawd.Core.Tests;

/// <summary>Runs a test (or every test in a class) in the given UI culture, which is what
/// <see cref="Strings"/> reads; restored afterwards.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class UseCultureAttribute(string name) : BeforeAfterTestAttribute
{
    private CultureInfo? _ui, _culture;

    public override void Before(MethodInfo methodUnderTest)
    {
        (_ui, _culture) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
    }

    public override void After(MethodInfo methodUnderTest)
    {
        if (_ui is not null) CultureInfo.CurrentUICulture = _ui;
        if (_culture is not null) CultureInfo.CurrentCulture = _culture;
    }
}

/// <summary>The resource files themselves: both languages have the same keys, every key the code
/// names exists, plurals come in pairs, and every format string can be filled in.</summary>
public partial class ResourceFileTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Clawd.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Windows/Clawd.sln not found above " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, string> Load(string relative) =>
        XDocument.Load(Path.Combine(Root, relative)).Root!.Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);

    public static TheoryData<string> Sets => new() { "core", "app" };

    /// <summary>English and Korean files of a set, and the source folder whose code uses the keys.</summary>
    private static (string English, string Korean, string Folder) Files(string set) => set == "core"
        ? ("Clawd.Core/Localization/Strings.resx", "Clawd.Core/Localization/Strings.ko.resx", "Clawd.Core")
        : ("Clawd/Strings/en-US/Resources.resw", "Clawd/Strings/ko-KR/Resources.resw", "Clawd");

    [Theory, MemberData(nameof(Sets))]
    public void BothLanguagesHaveTheSameKeys(string set)
    {
        var (english, korean, _) = Files(set);
        var en = Load(english);
        var ko = Load(korean);
        Assert.True(en.Count > 0, set);
        Assert.Empty(en.Keys.Except(ko.Keys));
        Assert.Empty(ko.Keys.Except(en.Keys));
        Assert.All(en.Concat(ko), kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
    }

    /// <summary>로 or 으로 right after the number of answers is only right for some counts (2로, 3으로).</summary>
    [Fact]
    public void NoKoreanParticleFollowsTheAnswerCount()
    {
        var hint = Load(Files("app").Korean)["Chat_PlaceholderAnswer"];
        Assert.DoesNotMatch(@"\{0\}(으로|로)", hint);
        Assert.Equal("위 버튼이나 Ctrl+1–3 키로 답해 주세요", string.Format(hint, 3));
    }

    [Theory, MemberData(nameof(Sets))]
    public void PluralsComeInPairs(string set)
    {
        var (english, korean, _) = Files(set);
        foreach (var keys in new[] { Load(english).Keys, Load(korean).Keys })
            foreach (var key in keys.Where(k => k.EndsWith("_One", StringComparison.Ordinal) || k.EndsWith("_Other", StringComparison.Ordinal)))
            {
                var stem = key[..key.LastIndexOf('_')];
                Assert.Contains(stem + "_One", keys);
                Assert.Contains(stem + "_Other", keys);
            }
    }

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    /// <summary>A translation may use any of the arguments, so each language is filled with as many
    /// as the larger of the two uses; a stray brace or a bad index throws here.</summary>
    [Theory, MemberData(nameof(Sets))]
    public void FormatStringsFillIn(string set)
    {
        var (english, korean, _) = Files(set);
        var en = Load(english);
        var ko = Load(korean);
        foreach (var key in en.Keys)
        {
            int Count(string s) => Placeholder().Matches(s).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1).DefaultIfEmpty(0).Max();
            var args = Enumerable.Range(0, Math.Max(Count(en[key]), Count(ko[key]))).Select(i => (object)$"<{i}>").ToArray();
            foreach (var text in new[] { en[key], ko[key] })
            {
                var filled = string.Format(CultureInfo.InvariantCulture, text, args);
                Assert.DoesNotContain("{", filled.Replace("{{", "", StringComparison.Ordinal));
            }
            // A plural's count is {0}: both forms must show it.
            if (key.EndsWith("_One", StringComparison.Ordinal) || key.EndsWith("_Other", StringComparison.Ordinal))
                Assert.Contains("{0}", en[key]);
        }
    }

    [GeneratedRegex(@"\b(?:Strings|L)\.(?:Get|Format|Plural)\(([^;\n]*)")]
    private static partial Regex Lookup();

    [GeneratedRegex("\"([A-Z][A-Za-z]*_[A-Za-z0-9_]+)\"")]
    private static partial Regex KeyLiteral();

    /// <summary>Keys named in the code exist (literals on the same line as Strings.Get/Format/Plural or
    /// L.Get/…), and the reverse: every resource is named somewhere in the code.</summary>
    [Theory, MemberData(nameof(Sets))]
    public void CodeAndResourcesAgree(string set)
    {
        var (english, _, folder) = Files(set);
        var keys = Load(english).Keys.ToHashSet();
        var looked = new HashSet<string>();
        var named = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, folder), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var code = File.ReadAllText(file);
            foreach (Match call in Lookup().Matches(code))
                foreach (Match literal in KeyLiteral().Matches(call.Groups[1].Value))
                    looked.Add(literal.Groups[1].Value);
            foreach (Match literal in KeyLiteral().Matches(code)) named.Add(literal.Groups[1].Value);
        }
        Assert.NotEmpty(looked);
        bool Defined(string key) => keys.Contains(key) || (keys.Contains(key + "_One") && keys.Contains(key + "_Other"));
        foreach (var key in looked) Assert.True(Defined(key), $"{set}: {key} is used but not defined");
        // Activity names are looked up as "Activity_" + the enum member.
        if (set == "core") named.UnionWith(Activities.All.Select(a => "Activity_" + a));
        var unused = keys.Where(k => !named.Contains(k) && !named.Contains(k[..Math.Max(0, k.LastIndexOf('_'))])).ToList();
        Assert.Empty(unused);
    }
}

public class LanguageChoiceTests
{
    [Theory]
    [InlineData("ko-KR", "ko-KR")]
    [InlineData("ko", "ko-KR")]
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("ja-JP", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("", "en-US")]   // invariant
    public void KoreanOnKoreanSystemsEnglishOtherwise(string system, string shown) =>
        Assert.Equal(shown, Strings.Resolve(CultureInfo.GetCultureInfo(system)).Name);

    [Fact, UseCulture("ko-KR")]
    public void KoreanComesFromTheSatelliteAssembly()
    {
        Assert.True(Strings.IsKorean);
        Assert.Equal("방금", Strings.Get("Ago_JustNow"));
    }

    [Fact, UseCulture("de-DE")]
    public void OtherLanguagesGetEnglish()
    {
        Assert.False(Strings.IsKorean);
        Assert.Equal("just now", Strings.Get("Ago_JustNow"));
    }

    [Fact, UseCulture("en-US")]
    public void MissingKeyShowsItself() => Assert.Equal("No_Such_Key", Strings.Get("No_Such_Key"));
}

[UseCulture("en-US")]
public class EnglishTextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static OrcaAgent A(string key, string state, string name = "w", DateTimeOffset? started = null) =>
        new(key, name, state, null, null, "last reply", null, started, false);

    [Fact]
    public void DurationsAndRelativeTimes()
    {
        Assert.Equal("1 min", Text.Duration(0));
        Assert.Equal("59 min", Text.Duration(59 * 60 + 59));
        Assert.Equal("1 hr 0 min", Text.Duration(3600));
        Assert.Equal("26 hr 1 min", Text.Duration(26 * 3600 + 60));
        Assert.Equal("just now", Text.Ago(T0.AddSeconds(-30), T0));
        Assert.Equal("3 min ago", Text.Ago(T0.AddSeconds(-185), T0));
        Assert.Equal("1 hr 5 min ago", Text.Ago(T0.AddSeconds(-3900), T0));
    }

    [Fact]
    public void Cards()
    {
        var card = Sign.Waiting([A("p", "waiting", "web"), A("q", "waiting")], null, T0.AddMinutes(-4), T0);
        Assert.Equal("Reply needed", card.Title);
        Assert.Equal("+1 more · Waiting for 4 min · Click to reply", card.Hint);
        Assert.Equal("Just started waiting · Click to reply", Sign.Waiting([A("p", "waiting")], null, T0.AddSeconds(-30), T0).Hint);
        Assert.Equal("Permission needed", Sign.Waiting([A("p", "blocked")], null, T0, T0).Title);
        Assert.Equal("Task complete", Sign.Done(A("d", "done"), TimeSpan.FromMinutes(2)).Title);
        Assert.Equal("Took 2 min · Click to keep talking", Sign.Done(A("d", "done"), TimeSpan.FromMinutes(2)).Hint);
        Assert.Equal("Click to keep talking", Sign.Done(A("d", "done"), null).Hint);
        // A session in Orca's chat is answered there: its cards invite a look, not a reply.
        var chat = OrcaAgent.ChatSessionPrefix + "s1:leaf";
        Assert.Equal("+1 more · Waiting for 4 min · Click to view", Sign.Waiting([A(chat, "waiting"), A("q", "waiting")], null, T0.AddMinutes(-4), T0).Hint);
        Assert.Equal("Just started waiting · Click to view", Sign.Waiting([A(chat, "waiting")], null, T0.AddSeconds(-30), T0).Hint);
        Assert.Equal("Took 2 min · Click to view", Sign.Done(A(chat, "done"), TimeSpan.FromMinutes(2)).Hint);
        Assert.Equal("Click to view", Sign.Done(A(chat, "done"), null).Hint);
        Assert.Equal("Can’t find Orca", Sign.Summary(false, true, [], [], T0).Title);
        Assert.Equal("Orca integration off", Sign.Summary(true, false, [], [], T0).Title);
        Assert.Equal("Everyone’s resting", Sign.Summary(true, true, [], [], T0).Title);
        var done = new Attention.FinishedTask(A("d", "done", "docs"), T0.AddMinutes(-2), null);
        var one = Sign.Summary(true, true, [A("w", "working", "web") with { Tool = "Bash" }], [done], T0);
        Assert.Equal("1 working", one.Title);
        Assert.Equal("web  Bash\ndocs  finished 2 min ago", one.Detail);
        Assert.Equal("2 working", Sign.Summary(true, true, [A("a", "working"), A("b", "working")], [], T0).Title);
        Assert.Equal("Recently finished", Sign.Summary(true, true, [], [done], T0).Title);
        Assert.Equal("2 waiting", Sign.StatusTitle(true, 2, 1, 1));
        Assert.Equal("1 running", Sign.StatusTitle(true, 0, 1, 1));
        Assert.Equal("3 done", Sign.StatusTitle(true, 0, 0, 3));
    }

    [Fact]
    public void SidebarStatuses()
    {
        var att = new Attention();
        att.Changed([], [A("q", "waiting")], T0);
        att.WaitingSince["q"] = T0.AddMinutes(-5);
        var rows = att.Rows([A("q", "waiting"), A("w", "working") with { Tool = "Edit" }, A("i", "idle")], new Dictionary<string, string>(), T0);
        Assert.Equal(["Reply needed · 5 min ago", "Working · Edit", "Resting"], rows.Select(r => r.Status));
    }

    [Fact]
    public void StepSummaries()
    {
        TimelineItem[] steps =
        [
            TimelineItem.Tool("1", "Bash", "a", "ok"), TimelineItem.Tool("2", "Read", "b", "ok"), TimelineItem.Tool("3", "Bash", "c", "ok"),
            TimelineItem.Tool("4", "Grep", "d", "ok"), TimelineItem.Tool("5", "Edit", "e", "ok"), new TimelineItem("t", TimelineKind.Thinking, "x"),
        ];
        var (title, detail, _) = StepText.Summary(steps);
        Assert.Equal("6 steps", title);
        Assert.Equal("Bash 2, Read 1, Grep 1, +1 more, Thinking 1", detail);
        Assert.Equal("1 step", StepText.Summary([steps[0]]).Title);
        Assert.Equal("Thinking…", StepText.ThinkingLabel(new TimelineItem("t", TimelineKind.Thinking, "")));
        Assert.Equal("Thinking", StepText.ThinkingLabel(steps[^1]));
        Assert.Equal("Update to-do list", Transcripts.Transcripts.Summary("TodoWrite", new System.Text.Json.Nodes.JsonObject()));
    }

    [Fact]
    public void PermissionAnswersAndSettings()
    {
        var prompt = PermissionPrompt.Parse(PermissionPromptTests.BashDialog())!;
        Assert.Equal(["Allow", "Always allow", "Deny"], prompt.Options.Select(o => o.Title));
        Assert.Equal(["When Clawd is hidden", "Always", "Never"], Enum.GetValues<NotificationPolicy>().Select(p => p.Title()));
        Assert.Equal("Juggle", Activity.Juggle.Title());
    }

    /// <summary>Every trick has a name in both languages (the key is built from the enum member).</summary>
    [Fact]
    public void EveryActivityHasATitle()
    {
        foreach (var a in Activities.All) Assert.DoesNotContain("_", a.Title());
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
        foreach (var a in Activities.All) Assert.DoesNotContain("_", a.Title());
        Assert.Equal("저글링", Activity.Juggle.Title());
    }
}

[UseCulture("ko-KR")]
public class KoreanTextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Plurals and argument order: Korean keeps one form and puts the time first.</summary>
    [Fact]
    public void SameWordingAsBefore()
    {
        OrcaAgent A(string key, string state) => new(key, "w", state, null, null, "last reply", null, null, false);
        Assert.Equal("작업 중 2개", Sign.Summary(true, true, [A("a", "working"), A("b", "working")], [], T0).Title);
        Assert.Equal("단계 1개", StepText.Summary([TimelineItem.Tool("1", "Bash", "a", "ok")]).Title);
        Assert.Equal("클릭해서 이어서 말하기", Sign.Done(A("d", "done"), null).Hint);
        Assert.Equal("방금부터 기다리는 중 · 클릭해서 답하기", Sign.Waiting([A("p", "waiting")], null, T0, T0).Hint);
        Assert.Equal(["Clawd를 숨겼을 때", "항상", "보내지 않음"], Enum.GetValues<NotificationPolicy>().Select(p => p.Title()));
        Assert.Equal("작업 중 · Edit", new Attention().Rows([A("w", "working") with { Tool = "Edit" }], new Dictionary<string, string>(), T0)[0].Status);
    }
}
