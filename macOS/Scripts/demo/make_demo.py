#!/usr/bin/env python3
"""Writes a self-contained demo for Clawd: Orca agents and Claude Code transcripts.

    python3 make_demo.py <out-dir> [ko|en]

Produces <out-dir>/agents.json (a `worktree.ps` answer with fresh timestamps) and
<out-dir>/transcripts/<pane>.jsonl. Scripts/demo.sh runs Clawd on them.
"""
import json, os, sys, time, uuid

out = sys.argv[1] if len(sys.argv) > 1 else "demo-out"
# Language of the made-up conversation: "ko" (default) or "en", for screenshots in each language.
LANG = sys.argv[2] if len(sys.argv) > 2 else os.environ.get("CLAWD_DEMO_LANG", "ko")

EN = {'결제 페이지 테스트 커버리지 80%까지 올려줘': 'Raise test coverage on the checkout page to 80%', '마이그레이션은 두 가지 방법이 있어요. 기존 orders 테이블을 바로 바꿀까요, 새 테이블로 옮기고 나중에 바꿔치기할까요?': 'There are two ways to migrate. Should I change the orders table in place, or move to a new table and swap it in later?', '주문 테이블 스키마 정리해줘': 'Clean up the orders table schema', '헤더를 모바일에서도 깔끔하게 보이게 반응형으로 고쳐줘': 'Make the header responsive so it looks clean on mobile', '위젯 타임라인이 자정에 안 바뀌는 버그 잡아줘': 'Fix the widget timeline not updating at midnight', 'README에 설치 방법과 스크린샷을 추가했어요. 깨진 링크 세 개도 같이 고쳤어요.': 'Added install steps and screenshots to the README, and fixed three broken links.', '문서 정리해줘': 'Tidy up the docs', '색 토큰 이름을 정리했어요.': 'Renamed the colour tokens.', '색 토큰 정리해줘': 'Clean up the colour tokens', 'terraform plan 결과 변경 사항이 없어요.': 'terraform plan shows no changes.', 'plan 돌려줘': 'Run plan', '헤더 구조부터 봐야 한다. 내비게이션 링크가 몇 개인지, 지금 어떤 브레이크포인트를 쓰는지 확인하고 768px 아래에서 메뉴를 접는 방향으로 간다.': 'Start with the header structure: count the nav links, check the breakpoints in use, then collapse the menu below 768px.', '헤더에 링크가 6개라 375px 화면에서는 두 줄로 넘쳐요. 768px 아래에서는 햄버거 메뉴로 접고, 로고와 검색만 남길게요.': "The header has 6 links, so it wraps to two lines at 375px. Below 768px I'll collapse them into a menu and keep only the logo and search.", '헤더 스냅샷 테스트 실행': 'Run the header snapshot tests', '태블릿 화면 확인': 'Check the tablet layout', '테스트 커버리지를 확인해 볼게요.': 'Let me check the test coverage.', '결제 페이지 테스트를 커버리지 리포트와 함께 실행': 'Run the checkout page tests with a coverage report', '스냅샷 테스트 12개 모두 통과했어요. 이어서 태블릿 크기도 확인해 볼게요.': "All 12 snapshot tests pass. Next I'll check the tablet size.", '바꾼 내용이에요.\n\n- **768px 아래**: 링크 6개를 `MobileMenu`로 접고, 로고와 검색 버튼만 남겼어요.\n- **메뉴 열기**: 화면 전체를 덮는 시트로 열리고, `Esc`나 바깥을 누르면 닫혀요.\n- **높이**: 72px 고정에서 `min-height: 56px`로 바꿔서 작은 화면에서 공간을 덜 써요.\n\n| 화면 | 전 | 후 |\n| --- | --- | --- |\n| 375px | 두 줄로 넘침 | 한 줄 |\n| 1024px | 그대로 | 그대로 |\n\n스냅샷 테스트 12개 모두 통과했어요. 이어서 태블릿 크기도 확인해 볼게요.': "Here's what changed.\n\n- **Below 768px**: the 6 links collapse into `MobileMenu`; only the logo and search stay.\n- **Opening the menu**: it opens as a full-screen sheet and closes with `Esc` or a click outside.\n- **Height**: from a fixed 72px to `min-height: 56px`, so it takes less room on small screens.\n\n| Screen | Before | After |\n| --- | --- | --- |\n| 375px | wraps to two lines | one line |\n| 1024px | unchanged | unchanged |\n\nAll 12 snapshot tests pass. Next I'll check the tablet size."}

def tr(text):
    """The English version of a Korean demo line when LANG is "en"."""
    return EN.get(text, text) if LANG == "en" else text
os.makedirs(os.path.join(out, "transcripts"), exist_ok=True)
now = time.time() * 1000

def agent(key, state, ago, tool=None, tool_input=None, last=None, prompt=None):
    return {"paneKey": key, "state": state, "toolName": tool, "toolInput": tool_input,
            "lastAssistantMessage": last, "prompt": prompt, "agentType": "claude",
            "stateStartedAt": now - ago * 1000, "updatedAt": now - 3000}

worktrees = [
    ("checkout-flow", [agent("tab-checkout:leaf", "blocked", 95, "Bash", "npm run test -- --coverage",
                             prompt=tr("결제 페이지 테스트 커버리지 80%까지 올려줘"))]),
    ("api-server", [agent("tab-api:leaf", "waiting", 240,
                          last=tr("마이그레이션은 두 가지 방법이 있어요. 기존 orders 테이블을 바로 바꿀까요, 새 테이블로 옮기고 나중에 바꿔치기할까요?"),
                          prompt=tr("주문 테이블 스키마 정리해줘"))]),
    ("web-app", [agent("tab-web:leaf", "working", 130, "Edit", "src/components/Header.tsx",
                       prompt=tr("헤더를 모바일에서도 깔끔하게 보이게 반응형으로 고쳐줘"))]),
    ("ios-widget", [agent("tab-ios:leaf", "working", 40, "Bash", "xcodebuild test", prompt=tr("위젯 타임라인이 자정에 안 바뀌는 버그 잡아줘"))]),
    ("docs-site", [agent("tab-docs:leaf", "done", 300,
                         last=tr("README에 설치 방법과 스크린샷을 추가했어요. 깨진 링크 세 개도 같이 고쳤어요."), prompt=tr("문서 정리해줘"))]),
    ("design-system", [agent("tab-ds:leaf", "idle", 7200, last=tr("색 토큰 이름을 정리했어요."), prompt=tr("색 토큰 정리해줘"))]),
    ("infra", [agent("tab-infra:leaf", "idle", 9000, last=tr("terraform plan 결과 변경 사항이 없어요."), prompt=tr("plan 돌려줘"))]),
]
answer = {"ok": True, "result": {"worktrees": [
    {"displayName": name, "repo": name, "isActive": False, "path": "/demo/" + name, "agents": agents}
    for name, agents in worktrees]}}
json.dump(answer, open(os.path.join(out, "agents.json"), "w"), ensure_ascii=False)

# A transcript for the web-app agent: prompt, thinking, tool calls with results, replies.
entries = []
def entry(kind, content):
    entries.append({"type": kind, "uuid": str(uuid.uuid4()), "isSidechain": False,
                    "message": {"role": kind, "content": content}})
def say(text): entry("assistant", [{"type": "text", "text": text}])
def think(text): entry("assistant", [{"type": "thinking", "thinking": text}])
def tool(name, inp, result, error=False):
    tid = "toolu_" + uuid.uuid4().hex[:20]
    entry("assistant", [{"type": "tool_use", "id": tid, "name": name, "input": inp}])
    if result is not None:
        entry("user", [{"type": "tool_result", "tool_use_id": tid, "content": result, "is_error": error}])

entry("user", tr("헤더를 모바일에서도 깔끔하게 보이게 반응형으로 고쳐줘"))
think(tr("헤더 구조부터 봐야 한다. 내비게이션 링크가 몇 개인지, 지금 어떤 브레이크포인트를 쓰는지 확인하고 768px 아래에서 메뉴를 접는 방향으로 간다."))
tool("Read", {"file_path": "/demo/web-app/src/components/Header.tsx"}, "export function Header() { … 6 nav links, fixed height 72px …")
tool("Grep", {"pattern": "breakpoint", "path": "src/styles"}, "src/styles/tokens.ts:12: export const breakpoint = { md: 768, lg: 1024 }")
say(tr("헤더에 링크가 6개라 375px 화면에서는 두 줄로 넘쳐요. 768px 아래에서는 햄버거 메뉴로 접고, 로고와 검색만 남길게요."))
tool("Edit", {"file_path": "/demo/web-app/src/components/Header.tsx"}, "The file has been updated.")
tool("Write", {"file_path": "/demo/web-app/src/components/MobileMenu.tsx"}, "File created successfully.")
tool("Bash", {"command": "npm run test -- Header", "description": tr("헤더 스냅샷 테스트 실행")},
     "PASS src/components/Header.test.tsx\n  ✓ renders desktop navigation\n  ✓ collapses into a menu below 768px\nTests: 12 passed")
say(tr("""바꾼 내용이에요.

- **768px 아래**: 링크 6개를 `MobileMenu`로 접고, 로고와 검색 버튼만 남겼어요.
- **메뉴 열기**: 화면 전체를 덮는 시트로 열리고, `Esc`나 바깥을 누르면 닫혀요.
- **높이**: 72px 고정에서 `min-height: 56px`로 바꿔서 작은 화면에서 공간을 덜 써요.

| 화면 | 전 | 후 |
| --- | --- | --- |
| 375px | 두 줄로 넘침 | 한 줄 |
| 1024px | 그대로 | 그대로 |

스냅샷 테스트 12개 모두 통과했어요. 이어서 태블릿 크기도 확인해 볼게요."""))
think("")
tool("Bash", {"command": "npx playwright test header --project=tablet", "description": tr("태블릿 화면 확인")}, None)

with open(os.path.join(out, "transcripts", "tab-web_leaf.jsonl"), "w") as f:
    for e in entries:
        f.write(json.dumps(e, ensure_ascii=False) + "\n")
# The checkout agent's terminal, showing Claude Code's permission dialog.
screen = """
⏺ {l1}

────────────────────────────────────────────────────────────────────────
 Bash command

   npm run test -- --coverage
   {l2}

 Do you want to proceed?
 ❯ 1. Yes
   2. Yes, and don't ask again for npm run test commands in /demo/checkout-flow
   3. No, and tell Claude what to do differently (esc)
"""
screen = screen.replace("{l1}", tr("테스트 커버리지를 확인해 볼게요.")).replace("{l2}", tr("결제 페이지 테스트를 커버리지 리포트와 함께 실행"))
with open(os.path.join(out, "transcripts", "tab-checkout_leaf.screen.txt"), "w") as f:
    f.write(screen)
# The web-app agent's terminal mid-task: an edit with its diff, then a test run.
web_screen = """
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
"""
web_screen = web_screen.replace("{l3}", tr("스냅샷 테스트 12개 모두 통과했어요. 이어서 태블릿 크기도 확인해 볼게요."))
with open(os.path.join(out, "transcripts", "tab-web_leaf.screen.txt"), "w") as f:
    f.write(web_screen)
print(out)
