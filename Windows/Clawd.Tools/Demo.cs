using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clawd.Tools;

/// <summary>
/// A self-contained demo for Clawd: made-up Orca agents and Claude Code transcripts, the same
/// data Scripts/demo/make_demo.py writes for the Mac app (so the PC needs no Python). Produces
/// &lt;dir&gt;/agents.json (a `worktree.ps` answer with fresh timestamps) and
/// &lt;dir&gt;/transcripts/&lt;pane&gt;.jsonl and .screen.txt, for CLAWD_FAKE_ORCA and CLAWD_FAKE_TRANSCRIPTS.
/// The conversation is Korean, or English with <c>en</c> (the script's ko→en table, copied
/// verbatim), for screenshots in each language.
/// </summary>
internal static class Demo
{
    private static readonly JsonSerializerOptions Raw = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>make_demo.py's <c>EN</c>: keep the two in step (NOTES.md has the parity check).</summary>
    private static readonly Dictionary<string, string> En = new()
    {
        ["결제 페이지 테스트 커버리지 80%까지 올려줘"] = "Raise test coverage on the checkout page to 80%",
        ["마이그레이션은 두 가지 방법이 있어요. 기존 orders 테이블을 바로 바꿀까요, 새 테이블로 옮기고 나중에 바꿔치기할까요?"] = "There are two ways to migrate. Should I change the orders table in place, or move to a new table and swap it in later?",
        ["주문 테이블 스키마 정리해줘"] = "Clean up the orders table schema",
        ["헤더를 모바일에서도 깔끔하게 보이게 반응형으로 고쳐줘"] = "Make the header responsive so it looks clean on mobile",
        ["위젯 타임라인이 자정에 안 바뀌는 버그 잡아줘"] = "Fix the widget timeline not updating at midnight",
        ["README에 설치 방법과 스크린샷을 추가했어요. 깨진 링크 세 개도 같이 고쳤어요."] = "Added install steps and screenshots to the README, and fixed three broken links.",
        ["문서 정리해줘"] = "Tidy up the docs",
        ["색 토큰 이름을 정리했어요."] = "Renamed the colour tokens.",
        ["색 토큰 정리해줘"] = "Clean up the colour tokens",
        ["terraform plan 결과 변경 사항이 없어요."] = "terraform plan shows no changes.",
        ["plan 돌려줘"] = "Run plan",
        ["헤더 구조부터 봐야 한다. 내비게이션 링크가 몇 개인지, 지금 어떤 브레이크포인트를 쓰는지 확인하고 768px 아래에서 메뉴를 접는 방향으로 간다."] = "Start with the header structure: count the nav links, check the breakpoints in use, then collapse the menu below 768px.",
        ["헤더에 링크가 6개라 375px 화면에서는 두 줄로 넘쳐요. 768px 아래에서는 햄버거 메뉴로 접고, 로고와 검색만 남길게요."] = "The header has 6 links, so it wraps to two lines at 375px. Below 768px I'll collapse them into a menu and keep only the logo and search.",
        ["헤더 스냅샷 테스트 실행"] = "Run the header snapshot tests",
        ["태블릿 화면 확인"] = "Check the tablet layout",
        ["테스트 커버리지를 확인해 볼게요."] = "Let me check the test coverage.",
        ["결제 페이지 테스트를 커버리지 리포트와 함께 실행"] = "Run the checkout page tests with a coverage report",
        ["스냅샷 테스트 12개 모두 통과했어요. 이어서 태블릿 크기도 확인해 볼게요."] = "All 12 snapshot tests pass. Next I'll check the tablet size.",
        ["바꾼 내용이에요.\n\n- **768px 아래**: 링크 6개를 `MobileMenu`로 접고, 로고와 검색 버튼만 남겼어요.\n- **메뉴 열기**: 화면 전체를 덮는 시트로 열리고, `Esc`나 바깥을 누르면 닫혀요.\n- **높이**: 72px 고정에서 `min-height: 56px`로 바꿔서 작은 화면에서 공간을 덜 써요.\n\n| 화면 | 전 | 후 |\n| --- | --- | --- |\n| 375px | 두 줄로 넘침 | 한 줄 |\n| 1024px | 그대로 | 그대로 |\n\n스냅샷 테스트 12개 모두 통과했어요. 이어서 태블릿 크기도 확인해 볼게요."] = "Here's what changed.\n\n- **Below 768px**: the 6 links collapse into `MobileMenu`; only the logo and search stay.\n- **Opening the menu**: it opens as a full-screen sheet and closes with `Esc` or a click outside.\n- **Height**: from a fixed 72px to `min-height: 56px`, so it takes less room on small screens.\n\n| Screen | Before | After |\n| --- | --- | --- |\n| 375px | wraps to two lines | one line |\n| 1024px | unchanged | unchanged |\n\nAll 12 snapshot tests pass. Next I'll check the tablet size.",
    };

    /// <param name="language">"ko" (default) or "en".</param>
    public static string Write(string outDir, string language = "ko")
    {
        string Tr(string text) => language == "en" ? En.GetValueOrDefault(text, text) : text;
        var transcripts = Path.Combine(outDir, "transcripts");
        Directory.CreateDirectory(transcripts);
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        JsonObject Agent(string key, string state, double ago, string? tool = null, string? toolInput = null, string? last = null, string? prompt = null) => new()
        {
            ["paneKey"] = key, ["state"] = state, ["toolName"] = tool, ["toolInput"] = toolInput,
            ["lastAssistantMessage"] = last, ["prompt"] = prompt, ["agentType"] = "claude",
            ["stateStartedAt"] = now - ago * 1000, ["updatedAt"] = now - 3000,
        };

        (string Name, JsonObject Agent)[] worktrees =
        [
            ("checkout-flow", Agent("tab-checkout:leaf", "blocked", 95, "Bash", "npm run test -- --coverage",
                prompt: Tr("결제 페이지 테스트 커버리지 80%까지 올려줘"))),
            ("api-server", Agent("tab-api:leaf", "waiting", 240,
                last: Tr("마이그레이션은 두 가지 방법이 있어요. 기존 orders 테이블을 바로 바꿀까요, 새 테이블로 옮기고 나중에 바꿔치기할까요?"),
                prompt: Tr("주문 테이블 스키마 정리해줘"))),
            ("web-app", Agent("tab-web:leaf", "working", 130, "Edit", "src/components/Header.tsx",
                prompt: Tr("헤더를 모바일에서도 깔끔하게 보이게 반응형으로 고쳐줘"))),
            ("ios-widget", Agent("tab-ios:leaf", "working", 40, "Bash", "xcodebuild test", prompt: Tr("위젯 타임라인이 자정에 안 바뀌는 버그 잡아줘"))),
            ("docs-site", Agent("tab-docs:leaf", "done", 300,
                last: Tr("README에 설치 방법과 스크린샷을 추가했어요. 깨진 링크 세 개도 같이 고쳤어요."), prompt: Tr("문서 정리해줘"))),
            ("design-system", Agent("tab-ds:leaf", "idle", 7200, last: Tr("색 토큰 이름을 정리했어요."), prompt: Tr("색 토큰 정리해줘"))),
            ("infra", Agent("tab-infra:leaf", "idle", 9000, last: Tr("terraform plan 결과 변경 사항이 없어요."), prompt: Tr("plan 돌려줘"))),
        ];
        var answer = new JsonObject
        {
            ["ok"] = true,
            ["result"] = new JsonObject
            {
                ["worktrees"] = new JsonArray(worktrees.Select(w => (JsonNode)new JsonObject
                {
                    ["displayName"] = w.Name, ["repo"] = w.Name, ["isActive"] = false, ["path"] = "/demo/" + w.Name,
                    ["agents"] = new JsonArray(w.Agent),
                }).ToArray()),
            },
        };
        File.WriteAllText(Path.Combine(outDir, "agents.json"), answer.ToJsonString(Raw));

        // A transcript for the web-app agent: prompt, thinking, tool calls with results, replies.
        var entries = new List<JsonObject>();
        void Entry(string kind, JsonNode content) => entries.Add(new JsonObject
        {
            ["type"] = kind, ["uuid"] = Guid.NewGuid().ToString(), ["isSidechain"] = false,
            ["message"] = new JsonObject { ["role"] = kind, ["content"] = content },
        });
        void Say(string text) => Entry("assistant", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }));
        void Think(string text) => Entry("assistant", new JsonArray(new JsonObject { ["type"] = "thinking", ["thinking"] = text }));
        void Tool(string name, JsonObject input, string? result, bool error = false)
        {
            var id = "toolu_" + Guid.NewGuid().ToString("N")[..20];
            Entry("assistant", new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input }));
            if (result is not null)
                Entry("user", new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = result, ["is_error"] = error }));
        }

        Entry("user", Tr("헤더를 모바일에서도 깔끔하게 보이게 반응형으로 고쳐줘"));
        Think(Tr("헤더 구조부터 봐야 한다. 내비게이션 링크가 몇 개인지, 지금 어떤 브레이크포인트를 쓰는지 확인하고 768px 아래에서 메뉴를 접는 방향으로 간다."));
        Tool("Read", new JsonObject { ["file_path"] = "/demo/web-app/src/components/Header.tsx" }, "export function Header() { … 6 nav links, fixed height 72px …");
        Tool("Grep", new JsonObject { ["pattern"] = "breakpoint", ["path"] = "src/styles" }, "src/styles/tokens.ts:12: export const breakpoint = { md: 768, lg: 1024 }");
        Say(Tr("헤더에 링크가 6개라 375px 화면에서는 두 줄로 넘쳐요. 768px 아래에서는 햄버거 메뉴로 접고, 로고와 검색만 남길게요."));
        Tool("Edit", new JsonObject { ["file_path"] = "/demo/web-app/src/components/Header.tsx" }, "The file has been updated.");
        Tool("Write", new JsonObject { ["file_path"] = "/demo/web-app/src/components/MobileMenu.tsx" }, "File created successfully.");
        Tool("Bash", new JsonObject { ["command"] = "npm run test -- Header", ["description"] = Tr("헤더 스냅샷 테스트 실행") },
            "PASS src/components/Header.test.tsx\n  ✓ renders desktop navigation\n  ✓ collapses into a menu below 768px\nTests: 12 passed");
        Say(Tr("""
            바꾼 내용이에요.

            - **768px 아래**: 링크 6개를 `MobileMenu`로 접고, 로고와 검색 버튼만 남겼어요.
            - **메뉴 열기**: 화면 전체를 덮는 시트로 열리고, `Esc`나 바깥을 누르면 닫혀요.
            - **높이**: 72px 고정에서 `min-height: 56px`로 바꿔서 작은 화면에서 공간을 덜 써요.

            | 화면 | 전 | 후 |
            | --- | --- | --- |
            | 375px | 두 줄로 넘침 | 한 줄 |
            | 1024px | 그대로 | 그대로 |

            스냅샷 테스트 12개 모두 통과했어요. 이어서 태블릿 크기도 확인해 볼게요.
            """));
        Think("");
        Tool("Bash", new JsonObject { ["command"] = "npx playwright test header --project=tablet", ["description"] = Tr("태블릿 화면 확인") }, null);

        File.WriteAllText(Path.Combine(transcripts, "tab-web_leaf.jsonl"), string.Concat(entries.Select(e => e.ToJsonString(Raw) + "\n")));

        // The checkout agent's terminal, showing Claude Code's permission dialog.
        File.WriteAllText(Path.Combine(transcripts, "tab-checkout_leaf.screen.txt"), """

            ⏺ {l1}

            ────────────────────────────────────────────────────────────────────────
             Bash command

               npm run test -- --coverage
               {l2}

             Do you want to proceed?
             ❯ 1. Yes
               2. Yes, and don't ask again for npm run test commands in /demo/checkout-flow
               3. No, and tell Claude what to do differently (esc)

            """.Replace("{l1}", Tr("테스트 커버리지를 확인해 볼게요.")).Replace("{l2}", Tr("결제 페이지 테스트를 커버리지 리포트와 함께 실행")));
        // The web-app agent's terminal mid-task: an edit with its diff, then a test run.
        File.WriteAllText(Path.Combine(transcripts, "tab-web_leaf.screen.txt"), """

            ⏺ Update(src/components/Header.tsx)
              ⎿  Updated src/components/Header.tsx with 6 additions and 2 removals
                   12      export function Header() {
                   13 -      return <nav className="header">
                   13 +      const compact = useMediaQuery(`(max-width: ${breakpoint.md}px)`)
                   14 +      return <nav className={compact ? "header compact" : "header"}>
                   15            <Logo />
                   16 -          <NavLinks />
                   16 +          {compact ? <MobileMenu links={links} /> : <NavLinks links={links} />}
                   17 +          <SearchButton />
                   18          </nav>

            ⏺ Bash(npm run test -- Header)
              ⎿  PASS src/components/Header.test.tsx
                   ✓ renders desktop navigation (18 ms)
                   ✓ collapses into a menu below 768px (11 ms)
                 Tests: 12 passed, 12 total

            ⏺ {l3}

            ✻ Checking tablet layout… (38s · ↓ 1.2k tokens · esc to interrupt)

            ────────────────────────────────────────────────────────────────────────────
            ❯ 
            ────────────────────────────────────────────────────────────────────────────
              ⏵⏵ accept edits on (shift+tab to cycle)

            """.Replace("{l3}", Tr("스냅샷 테스트 12개 모두 통과했어요. 이어서 태블릿 크기도 확인해 볼게요.")));
        return Path.GetFullPath(outDir);
    }
}
