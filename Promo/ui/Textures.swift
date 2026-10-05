// Renders the UI textures and captions used by the promo scene.
// Run: swift Promo/ui/Textures.swift Promo/textures Promo/captions
//
// These are drawn mock-ups of the app's dark-mode UI (chat popover, terminal view, sign cards)
// with demo content, so the video shows no personal data. Real screenshots dropped into
// Promo/shots/ with the same file names replace them at render time (see README).

import AppKit
import SwiftUI

let texDir = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "Promo/textures"
let capDir = CommandLine.arguments.count > 2 ? CommandLine.arguments[2] : "Promo/captions"

// MARK: - Palette (macOS dark appearance)

let fg = Color.white.opacity(0.92)
let fg2 = Color.white.opacity(0.55)
let fg3 = Color.white.opacity(0.32)
let sysBlue = Color(red: 10 / 255, green: 132 / 255, blue: 255 / 255)
let sysOrange = Color(red: 255 / 255, green: 159 / 255, blue: 10 / 255)
let sysGreen = Color(red: 48 / 255, green: 209 / 255, blue: 88 / 255)
let sysRed = Color(red: 255 / 255, green: 105 / 255, blue: 97 / 255)
let windowBG = Color(red: 0.157, green: 0.153, blue: 0.157)
let sidebarBG = Color(red: 0.20, green: 0.20, blue: 0.21).opacity(0.72)
let mono = Font.system(size: 14, design: .monospaced)

// MARK: - Pieces

struct Spinner: View {
    var size: CGFloat = 12
    var body: some View {
        ZStack {
            ForEach(0..<8) { i in
                Capsule().fill(Color.white.opacity(0.15 + 0.6 * Double(i) / 7))
                    .frame(width: size * 0.14, height: size * 0.3)
                    .offset(y: -size * 0.33)
                    .rotationEffect(.degrees(Double(i) * 45))
            }
        }
        .frame(width: size, height: size)
    }
}

struct SidebarRow: View {
    var name: String, sub: String, mark: Int, selected = false   // mark: 0 none, 1 dot, 2 spinner, 3 check
    var body: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text(name).font(.system(size: 13)).foregroundStyle(fg)
                Text(sub).font(.system(size: 11)).foregroundStyle(fg2)
            }
            Spacer(minLength: 4)
            switch mark {
            case 1: Circle().fill(sysOrange).frame(width: 8, height: 8)
            case 2: Spinner(size: 11)
            case 3: Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).foregroundStyle(sysGreen)
            default: EmptyView()
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 6)
        .background(selected ? Color.white.opacity(0.11) : .clear, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
    }
}

struct SectionTitle: View {
    var text: String
    var body: some View {
        Text(text).font(.system(size: 11, weight: .semibold)).foregroundStyle(fg3)
            .padding(.horizontal, 10).padding(.top, 12).padding(.bottom, 2)
    }
}

/// Same agents as the real screenshots in Promo/shots, so mock and real frames match.
struct Sidebar: View {
    var selected = "checkout-flow"
    var checkoutWorking = false
    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            SectionTitle(text: "확인 필요")
            if !checkoutWorking {
                SidebarRow(name: "checkout-flow", sub: "권한 필요 · 1분 전", mark: 1, selected: selected == "checkout-flow")
            }
            SidebarRow(name: "api-server", sub: "답장 필요 · 4분 전", mark: 1, selected: selected == "api-server")
            SectionTitle(text: "작업 중")
            if checkoutWorking {
                SidebarRow(name: "checkout-flow", sub: "작업 중 · Bash", mark: 2, selected: selected == "checkout-flow")
            }
            SidebarRow(name: "ios-widget", sub: "작업 중 · Bash", mark: 2)
            SidebarRow(name: "web-app", sub: "작업 중 · Edit", mark: 2, selected: selected == "web-app")
            SectionTitle(text: "완료")
            SidebarRow(name: "docs-site", sub: "완료 · 5분 전", mark: 3)
            Text("쉬는 중 2").font(.system(size: 11, weight: .semibold)).foregroundStyle(fg3)
                .padding(.horizontal, 10).padding(.top, 12)
            Spacer()
        }
        .padding(.horizontal, 10).padding(.top, 8)
        .frame(width: 240)
        .frame(maxHeight: .infinity)
        .background(sidebarBG)
    }
}

struct GlassPill<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        content
            .background(Color.white.opacity(0.10), in: Capsule())
            .overlay(Capsule().strokeBorder(Color.white.opacity(0.14), lineWidth: 0.5))
    }
}

struct Header: View {
    var terminal: Bool
    var title = "checkout-flow"
    var status = "권한 필요 · 1분 전"
    var statusColor = sysOrange
    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.system(size: 13, weight: .bold)).foregroundStyle(fg)
                Text(status).font(.system(size: 11)).foregroundStyle(statusColor)
            }
            Spacer()
            GlassPill {
                HStack(spacing: 0) {
                    Image(systemName: "bubble.left.and.bubble.right")
                        .frame(width: 34, height: 24)
                        .background(terminal ? .clear : Color.white.opacity(0.16), in: Capsule())
                    Image(systemName: "terminal")
                        .frame(width: 34, height: 24)
                        .background(terminal ? Color.white.opacity(0.16) : .clear, in: Capsule())
                }
                .font(.system(size: 12)).foregroundStyle(fg).padding(2)
            }
            GlassPill {
                Image(systemName: "arrow.up.forward.app").font(.system(size: 12)).foregroundStyle(fg)
                    .frame(width: 30, height: 28)
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 12)
    }
}

struct ToolRow: View {
    var symbol: String, name: String, arg: String
    var body: some View {
        HStack(spacing: 7) {
            Image(systemName: "chevron.right").font(.system(size: 9, weight: .bold)).foregroundStyle(fg3).frame(width: 12)
            Image(systemName: symbol).font(.system(size: 12)).foregroundStyle(fg2).frame(width: 16)
            (Text(name).bold().foregroundColor(fg) + Text("  ") + Text(arg).foregroundColor(fg2))
                .font(.system(size: 13))
            Spacer()
        }
    }
}

struct Permission: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label { Text("Bash 명령을 실행할까요?") } icon: { Image(systemName: "hand.raised.fill") }
                .font(.system(size: 13, weight: .semibold)).foregroundStyle(sysOrange)
            VStack(alignment: .leading, spacing: 10) {
                Text("auth 테스트만 골라서 실행해요").font(.system(size: 13)).foregroundStyle(fg2)
                Text("npm test -- auth").font(.system(size: 13, design: .monospaced)).foregroundStyle(fg)
                HStack(spacing: 8) {
                    Text("허용").font(.system(size: 13, weight: .semibold)).foregroundStyle(.white)
                        .padding(.horizontal, 18).padding(.vertical, 7)
                        .background(sysBlue, in: Capsule())
                    ForEach(["이 세션 동안 허용", "거부"], id: \.self) { t in
                        Text(t).font(.system(size: 13, weight: .medium)).foregroundStyle(fg)
                            .padding(.horizontal, 16).padding(.vertical, 7)
                            .background(Color.white.opacity(0.12), in: Capsule())
                    }
                }
                .padding(.top, 2)
            }
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.white.opacity(0.06), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Color.white.opacity(0.08), lineWidth: 0.5))
        }
    }
}

struct Composer: View {
    var body: some View {
        HStack {
            Text("위 버튼이나 ⌘1–3으로 답해 주세요").font(.system(size: 13)).foregroundStyle(fg3)
            Spacer()
        }
        .padding(.leading, 14).frame(height: 36)
        .background(Color.white.opacity(0.07), in: Capsule())
        .overlay(Capsule().strokeBorder(Color.white.opacity(0.12), lineWidth: 0.5))
        .padding(16)
    }
}

struct Timeline: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Spacer(minLength: 80)
                Text("로그인 실패하는 테스트 좀 고쳐줘").font(.system(size: 13)).foregroundStyle(.white)
                    .padding(.horizontal, 12).padding(.vertical, 8)
                    .background(sysBlue, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            }
            .padding(.vertical, 2)
            Label { Text("생각함") } icon: { Image(systemName: "brain") }
                .font(.system(size: 13)).foregroundStyle(fg2)
            ToolRow(symbol: "doc.text", name: "Read", arg: "src/auth/session.ts")
            ToolRow(symbol: "magnifyingglass", name: "Grep", arg: "expiresAt")
            ToolRow(symbol: "doc.text", name: "Read", arg: "test/auth/session.test.ts")
            ToolRow(symbol: "pencil", name: "Edit", arg: "src/auth/session.ts")
            Text("토큰 만료를 확인할 때 초와 밀리초를 섞어서 비교하고 있었어요. 둘 다 밀리초로 맞췄어요. 테스트로 확인해 볼게요.")
                .font(.system(size: 13)).foregroundStyle(fg).lineSpacing(3)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.vertical, 2)
            Permission()
            Spacer(minLength: 0)
        }
        .padding(20)
    }
}

let panelShape = RoundedRectangle(cornerRadius: 18, style: .continuous)

struct ChatPanel: View {
    var body: some View {
        HStack(spacing: 0) {
            Sidebar()
            Rectangle().fill(Color.black.opacity(0.5)).frame(width: 1)
            VStack(spacing: 0) {
                Header(terminal: false)
                Timeline()
                Composer()
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(windowBG)
        }
        .frame(width: 920, height: 640)
        .clipShape(panelShape)
    }
}

// MARK: - Terminal (Claude Code running in an Orca terminal)

struct TLine: View {
    var parts: [(String, Color)]
    var body: some View {
        parts.reduce(Text("")) { $0 + Text($1.0).foregroundColor($1.1) }
            .font(mono).lineLimit(1)
    }
}

let clawdOrangeC = Color(red: 215 / 255, green: 119 / 255, blue: 87 / 255)
let tGray = Color.white.opacity(0.5)
let tFg = Color.white.opacity(0.88)

/// The standing sprite from Sources/Sprite.swift: body, claws, legs, eyes (pixels are 1 wide x 2 tall).
struct PixelClawd: View {
    var px: CGFloat
    var body: some View {
        Canvas { ctx, _ in
            func r(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat, _ c: Color) {
                ctx.fill(Path(CGRect(x: (x - 1) * px, y: y * 2 * px, width: w * px, height: h * 2 * px)), with: .color(c))
            }
            r(3, 0, 12, 4, clawdOrangeC); r(1, 2, 2, 1, clawdOrangeC); r(15, 2, 2, 1, clawdOrangeC)
            for x in [4.0, 6, 11, 13] { r(x, 4, 1, 1, clawdOrangeC) }
            r(5, 1, 1, 1, .black); r(12, 1, 1, 1, .black)
        }
        .frame(width: 16 * px, height: 10 * px)
    }
}

struct TerminalPanel: View {
    var body: some View {
        HStack(spacing: 0) {
            Sidebar(selected: "checkout-flow", checkoutWorking: true)
            Rectangle().fill(Color.black.opacity(0.5)).frame(width: 1)
            VStack(spacing: 0) {
                Header(terminal: true, title: "checkout-flow", status: "작업 중 · Bash", statusColor: fg2)
                VStack(alignment: .leading, spacing: 5) {
                    HStack(spacing: 16) {
                        PixelClawd(px: 4.4)
                        VStack(alignment: .leading, spacing: 3) {
                            TLine(parts: [("Claude Code", tFg)])
                            TLine(parts: [("Opus · ~/work/checkout-flow", tGray)])
                        }
                    }
                    .padding(.vertical, 6)
                    TLine(parts: [(" ", tFg)])
                    TLine(parts: [("> ", tGray), ("결제 페이지 테스트 커버리지 80%까지 올려줘", tFg)])
                    TLine(parts: [(" ", tFg)])
                    TLine(parts: [("⏺ ", sysGreen), ("Read", tFg), ("(src/checkout/payment.ts)", tGray)])
                    TLine(parts: [("  ⎿  Read 132 lines", tGray)])
                    TLine(parts: [("⏺ ", sysGreen), ("Write", tFg), ("(test/checkout/payment.test.ts)", tGray)])
                    TLine(parts: [("  ⎿  ", tGray), ("+ it('카드 승인이 거절되면 다시 시도한다', ...)", sysGreen)])
                    TLine(parts: [("     ", tGray), ("+ it('쿠폰 금액이 결제 금액보다 크면 0원', ...)", sysGreen)])
                    TLine(parts: [("⏺ ", sysGreen), ("Bash", tFg), ("(npm run test -- --coverage)", tGray)])
                    HStack(spacing: 0) {
                        TLine(parts: [("  ⎿  ", tGray)])
                        TLine(parts: [(" PASS ", Color.black)]).background(sysGreen)
                        TLine(parts: [(" test/checkout/payment.test.ts", tFg)])
                    }
                    TLine(parts: [("     Statements: 82.4%   Branches: 80.1%", tGray)])
                    TLine(parts: [(" ", tFg)])
                    TLine(parts: [("⏺ ", tFg), ("커버리지 82%가 됐어요. 거절·쿠폰 경우를 테스트 6개로 채웠어요.", tFg)])
                    TLine(parts: [(" ", tFg)])
                    HStack(spacing: 0) {
                        TLine(parts: [("> ", tGray)])
                        Rectangle().fill(tFg).frame(width: 8, height: 15)
                    }
                    .padding(.vertical, 6).padding(.horizontal, 4)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Color.white.opacity(0.22), lineWidth: 1))
                    TLine(parts: [("  ? for shortcuts", tGray)])
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, 16).padding(.top, 6)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .background(Color(red: 0.08, green: 0.08, blue: 0.085))
                HStack {
                    Text("키보드 입력은 터미널로 바로 가요 · ⌘T 대화 보기 · ⌘W 닫기")
                        .font(.system(size: 11)).foregroundStyle(fg2)
                    Spacer()
                }
                .padding(.horizontal, 16).padding(.vertical, 8)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(windowBG)
        }
        .frame(width: 920, height: 640)
        .clipShape(panelShape)
    }
}

// MARK: - Sign cards (what Clawd shows above its head)

struct Card: View {
    var tint: Color, symbol: String, title: String, name: String?, detail: String?, hint: String?
    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            ZStack {
                RoundedRectangle(cornerRadius: 56 * 0.26, style: .continuous).fill(tint.gradient)
                Image(systemName: symbol).font(.system(size: 28, weight: .semibold)).foregroundStyle(.white)
            }
            .frame(width: 56, height: 56)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.system(size: 28, weight: .bold)).foregroundStyle(fg)
                if let name { Text(name).font(.system(size: 19, weight: .semibold)).foregroundStyle(fg) }
                if let detail {
                    Text(detail).font(.system(size: 14)).foregroundStyle(Color.white.opacity(0.78)).padding(.top, 2)
                }
                if let hint {
                    Text(hint).font(.system(size: 12, weight: .medium)).foregroundStyle(fg2).padding(.top, 4)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 18).padding(.vertical, 16).padding(.trailing, 18)
        .frame(width: 440, alignment: .leading)
        .background(Color(red: 0.141, green: 0.141, blue: 0.141),
                    in: RoundedRectangle(cornerRadius: 22, style: .continuous))
        .overlay(alignment: .topTrailing) {
            Image(systemName: "xmark").font(.system(size: 10, weight: .semibold)).foregroundStyle(fg3).padding(14)
        }
    }
}

// MARK: - Captions (composited over the video by ffmpeg)

struct Caption: View {
    var text: String
    var body: some View {
        Text(text)
            .font(.system(size: 52, weight: .bold))
            .tracking(-0.8)
            .foregroundStyle(Color(red: 0.08, green: 0.07, blue: 0.06))
            .fixedSize()
            // a soft warm-white halo keeps the dark type readable over darker parts of the frame
            .shadow(color: Color(red: 1.0, green: 0.97, blue: 0.93).opacity(0.9), radius: 14)
            .shadow(color: Color(red: 1.0, green: 0.97, blue: 0.93).opacity(0.6), radius: 4)
            .padding(.horizontal, 40).padding(.vertical, 30)
    }
}

struct EndTitle: View {
    var title: String, sub: String
    var body: some View {
        VStack(spacing: 18) {
            Text(title)
                .font(.system(size: 104, weight: .bold))
                .tracking(-2.5)
                .foregroundStyle(Color(red: 0.08, green: 0.07, blue: 0.06))
            Text(sub)
                .font(.system(size: 40, weight: .medium))
                .tracking(-0.4)
                .foregroundStyle(Color(red: 0.08, green: 0.07, blue: 0.06).opacity(0.7))
        }
        .frame(width: 1920, height: 300)
    }
}

// MARK: - Output

// MARK: - Agent chips (light mode): one working agent each, styled like a sidebar row

struct LightSpinner: View {
    var size: CGFloat = 14
    var body: some View {
        ZStack {
            ForEach(0..<8) { i in
                Capsule().fill(Color.black.opacity(0.12 + 0.55 * Double(i) / 7))
                    .frame(width: size * 0.14, height: size * 0.3)
                    .offset(y: -size * 0.33)
                    .rotationEffect(.degrees(Double(i) * 45))
            }
        }
        .frame(width: size, height: size)
    }
}

struct AgentChip: View {
    var symbol: String, name: String, tool: String
    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Color.black.opacity(0.06))
                Image(systemName: symbol).font(.system(size: 16, weight: .semibold)).foregroundStyle(Color.black.opacity(0.7))
            }
            .frame(width: 36, height: 36)
            VStack(alignment: .leading, spacing: 1) {
                Text(name).font(.system(size: 16, weight: .semibold)).foregroundStyle(Color.black.opacity(0.88))
                Text("작업 중 · \(tool)").font(.system(size: 13.5, weight: .medium)).foregroundStyle(Color.black.opacity(0.62))
            }
            Spacer(minLength: 10)
            LightSpinner(size: 15)
        }
        .padding(.leading, 12).padding(.trailing, 18)
        .frame(width: 250, height: 60)
    }
}

// MARK: - Desk scene: phone overlay, desktop, cursor (generic, no brands)

/// The player chrome over the clip: action column, handle and caption, progress bar
struct PhoneOverlay: View {
    var body: some View {
        ZStack(alignment: .bottomLeading) {
            LinearGradient(colors: [.clear, Color.black.opacity(0.45)], startPoint: .center, endPoint: .bottom)
            VStack(spacing: 22) {
                ForEach([("heart.fill", "12.4k"), ("bubble.right.fill", "318"), ("paperplane.fill", "공유")], id: \.0) { sym, n in
                    VStack(spacing: 4) {
                        Image(systemName: sym).font(.system(size: 30, weight: .semibold)).foregroundStyle(.white)
                        Text(n).font(.system(size: 12, weight: .semibold)).foregroundStyle(.white)
                    }
                }
            }
            .shadow(color: .black.opacity(0.3), radius: 4)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .trailing)
            .padding(.trailing, 14).padding(.top, 330)
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 8) {
                    Circle().fill(Color.white.opacity(0.9)).frame(width: 30, height: 30)
                    Text("@daily.clips").font(.system(size: 15, weight: .bold)).foregroundStyle(.white)
                }
                Text("오늘도 한 편만 더 보고 일하기 ☕️").font(.system(size: 14)).foregroundStyle(.white)
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.white.opacity(0.35)).frame(width: 358, height: 3)
                    Capsule().fill(Color.white).frame(width: 150, height: 3)
                }
            }
            .padding(16).padding(.bottom, 10)
        }
        .frame(width: 390, height: 844)
    }
}

// Desktop wallpaper at dusk with a dark terminal running a coding-agent session (generic, no brands)
let dDim = Color(red: 0.45, green: 0.47, blue: 0.52)
let dFg = Color(red: 0.12, green: 0.13, blue: 0.16)
let tGreen = Color(red: 0.10, green: 0.55, blue: 0.27)
let tRed = Color(red: 0.78, green: 0.20, blue: 0.20)
let tBlue = Color(red: 0.08, green: 0.36, blue: 0.85)
let tPurple = Color(red: 0.50, green: 0.25, blue: 0.78)
let tYellow = Color(red: 0.66, green: 0.42, blue: 0.0)
let tOrange = Color(red: 0.80, green: 0.40, blue: 0.22)
let addBG = Color(red: 0.86, green: 0.95, blue: 0.88)
let delBG = Color(red: 0.99, green: 0.89, blue: 0.89)

struct TermRow: View {
    var parts: [(String, Color)]
    var bg: Color = .clear
    var body: some View {
        HStack(spacing: 0) {
            ForEach(Array(parts.enumerated()), id: \.offset) { _, p in
                Text(p.0).foregroundStyle(p.1)
            }
            Spacer(minLength: 0)
        }
        .font(.system(size: 12.5, weight: .regular, design: .monospaced))
        .lineLimit(1)
        .frame(height: 17.5)
        .background(bg)
    }
}

struct AgentTerminal: View {
    var body: some View {
        VStack(spacing: 0) {
            // title bar
            ZStack {
                HStack(spacing: 8) {
                    ForEach([Color(red: 0.93, green: 0.42, blue: 0.37), Color(red: 0.96, green: 0.75, blue: 0.31), Color(red: 0.38, green: 0.78, blue: 0.40)], id: \.self) { c in
                        Circle().fill(c).frame(width: 12, height: 12)
                    }
                    Spacer()
                }
                Text("checkout-flow — agent — 96×32").font(.system(size: 12.5, weight: .medium)).foregroundStyle(dDim)
            }
            .padding(.horizontal, 14).frame(height: 34)
            .background(Color(red: 0.93, green: 0.94, blue: 0.96))
            Rectangle().fill(Color.black.opacity(0.08)).frame(height: 1)
            VStack(alignment: .leading, spacing: 0) {
                TermRow(parts: [("● ", tGreen), ("Update", dFg), ("(src/checkout/PaymentForm.tsx)", dDim)])
                TermRow(parts: [("  ⎿ Updated ", dDim), ("PaymentForm.tsx", dFg), (" with 4 additions and 1 removal", dDim)])
                TermRow(parts: [("     38  ", dDim), ("export function ", tPurple), ("PaymentForm", tBlue), ("({ items, card }) {", dFg)])
                TermRow(parts: [("     39  ", dDim), ("  const ", tPurple), ("total = ", dFg), ("useMemo", tBlue), ("(() => sum(items), [items])", dFg)])
                TermRow(parts: [("     40 -", tRed), ("  return ", tPurple), ("<Button onClick={pay}>Pay</Button>", dFg)], bg: delBG)
                TermRow(parts: [("     40 +", tGreen), ("  return (", tPurple)], bg: addBG)
                TermRow(parts: [("     41 +", tGreen), ("    <Button ", dFg), ("disabled", tYellow), ("={!card.valid} ", dFg), ("onClick", tYellow), ("={pay}>", dFg)], bg: addBG)
                TermRow(parts: [("     42 +", tGreen), ("      Pay {", dFg), ("formatPrice", tBlue), ("(total)}", dFg)], bg: addBG)
                TermRow(parts: [("     43 +", tGreen), ("    </Button>)", dFg)], bg: addBG)
                TermRow(parts: [("     44  ", dDim), ("}", dFg)])
                TermRow(parts: [(" ", dFg)])
                TermRow(parts: [("● ", tGreen), ("Bash", dFg), ("(npm run test -- --coverage)", dDim)])
                TermRow(parts: [("  ⎿ ", dDim), (" PASS ", tGreen), (" src/checkout/PaymentForm.test.tsx", dFg)])
                TermRow(parts: [("      ✓ ", tGreen), ("disables pay until the card is valid ", dDim), ("(14 ms)", dDim)])
                TermRow(parts: [("      ✓ ", tGreen), ("shows the formatted total ", dDim), ("(6 ms)", dDim)])
                TermRow(parts: [("      ✓ ", tGreen), ("submits once per click ", dDim), ("(9 ms)", dDim)])
                TermRow(parts: [("    Tests:  ", dFg), ("31 passed", tGreen), (", 31 total", dFg)])
                TermRow(parts: [("    Coverage: ", dFg), ("statements 78.4%", tYellow), ("  branches 71.2%", dDim)])
                TermRow(parts: [(" ", dFg)])
                TermRow(parts: [("● ", dFg), ("Coverage is under 80%. I'll add tests for the error states.", dFg)])
                TermRow(parts: [(" ", dFg)])
                TermRow(parts: [("✻ ", tOrange), ("Writing tests… ", tOrange), ("(42s · ↓ 2.1k tokens · esc to interrupt)", dDim)])
                Spacer(minLength: 6)
                Rectangle().fill(Color.black.opacity(0.12)).frame(height: 1)
                TermRow(parts: [("> ", dFg), ("▌", Color.black.opacity(0.6))]).padding(.vertical, 6)
                Rectangle().fill(Color.black.opacity(0.12)).frame(height: 1)
                TermRow(parts: [("  ⏵⏵ accept edits on ", tPurple), ("(shift+tab to cycle)", dDim)]).padding(.top, 4)
            }
            .padding(.horizontal, 18).padding(.vertical, 12)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(Color(red: 0.985, green: 0.988, blue: 0.995))
        }
        .frame(width: 700, height: 520)
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(Color.black.opacity(0.10), lineWidth: 1))
    }
}

struct DesktopIcon: View {
    var name: String; var tint: Color; var folder = true
    var body: some View {
        VStack(spacing: 6) {
            ZStack(alignment: .topLeading) {
                if folder {
                    RoundedRectangle(cornerRadius: 5).fill(tint.opacity(0.85)).frame(width: 30, height: 12).offset(x: 4, y: -5)
                    RoundedRectangle(cornerRadius: 7).fill(LinearGradient(colors: [tint, tint.opacity(0.8)], startPoint: .top, endPoint: .bottom))
                        .frame(width: 64, height: 48)
                } else {
                    RoundedRectangle(cornerRadius: 5).fill(Color.white.opacity(0.95)).frame(width: 46, height: 58)
                        .overlay(VStack(alignment: .leading, spacing: 5) { ForEach(0..<5) { i in Capsule().fill(Color.black.opacity(0.18)).frame(width: CGFloat(28 - (i % 3) * 6), height: 3) } }.padding(8), alignment: .topLeading)
                        .offset(x: 9)
                }
            }
            .frame(width: 64, height: 58)
            .shadow(color: .black.opacity(0.18), radius: 4, y: 2)
            Text(name).font(.system(size: 12, weight: .medium)).foregroundStyle(Color(red: 0.15, green: 0.17, blue: 0.22))
                .shadow(color: .white.opacity(0.6), radius: 2, y: 0)
        }
    }
}

struct Desktop: View {
    var body: some View {
        ZStack(alignment: .topLeading) {
            // calm pastel wallpaper: pale sky at the top (the end title sits there), a soft lavender wash
            // and one large, very soft apricot form in Clawd's colour family low on the right
            LinearGradient(stops: [.init(color: Color(red: 0.96, green: 0.97, blue: 0.99), location: 0.0),
                                   .init(color: Color(red: 0.92, green: 0.92, blue: 0.98), location: 0.55),
                                   .init(color: Color(red: 0.88, green: 0.87, blue: 0.96), location: 1.0)],
                           startPoint: .top, endPoint: .bottom)
            Ellipse().fill(Color(red: 0.98, green: 0.80, blue: 0.70).opacity(0.75)).frame(width: 1300, height: 760)
                .blur(radius: 200).offset(x: 900, y: 560)
            Ellipse().fill(Color(red: 0.84, green: 0.53, blue: 0.42).opacity(0.28)).frame(width: 700, height: 380)
                .blur(radius: 150).offset(x: 1250, y: 820)
            // menu bar
            HStack(spacing: 22) {
                Circle().fill(Color.black.opacity(0.65)).frame(width: 13, height: 13)
                ForEach(["Terminal", "Shell", "Edit", "View", "Window", "Help"], id: \.self) { m in
                    Text(m).font(.system(size: 13.5, weight: m == "Terminal" ? .bold : .regular)).foregroundStyle(Color.black.opacity(0.8))
                }
                Spacer()
                PixelClawd(px: 1.6)
                Image(systemName: "wifi").font(.system(size: 13, weight: .semibold)).foregroundStyle(Color.black.opacity(0.75))
                Image(systemName: "battery.75percent").font(.system(size: 15)).foregroundStyle(Color.black.opacity(0.75))
                Text("Sat 6:42 PM").font(.system(size: 13.5, weight: .medium)).foregroundStyle(Color.black.opacity(0.8))
            }
            .padding(.horizontal, 22).frame(height: 34)
            .background(Color.white.opacity(0.42))
            // a few desktop items, top right
            VStack(spacing: 22) {
                DesktopIcon(name: "checkout-flow", tint: Color(red: 0.42, green: 0.66, blue: 0.93))
                DesktopIcon(name: "api-server", tint: Color(red: 0.42, green: 0.66, blue: 0.93))
                DesktopIcon(name: "notes.md", tint: .white, folder: false)
            }
            .frame(width: 120).offset(x: 1780, y: 70)
            AgentTerminal()
                .shadow(color: Color(red: 0.1, green: 0.2, blue: 0.45).opacity(0.22), radius: 40, y: 18)
                .offset(x: 40, y: 440)
        }
        .frame(width: 1920, height: 1080)
        .clipped()
    }
}

struct Cursor: View {
    var body: some View {
        Canvas { ctx, _ in
            var p = Path()
            p.move(to: CGPoint(x: 6, y: 4)); p.addLine(to: CGPoint(x: 6, y: 58)); p.addLine(to: CGPoint(x: 19, y: 45))
            p.addLine(to: CGPoint(x: 28, y: 66)); p.addLine(to: CGPoint(x: 37, y: 62)); p.addLine(to: CGPoint(x: 28, y: 42))
            p.addLine(to: CGPoint(x: 46, y: 42)); p.closeSubpath()
            ctx.stroke(p, with: .color(.white), style: StrokeStyle(lineWidth: 6, lineJoin: .round))
            ctx.fill(p, with: .color(.black))
        }
        .frame(width: 52, height: 72)
    }
}

@MainActor
func save<V: View>(_ view: V, _ dir: String, _ name: String, scale: CGFloat) {
    if let only = ProcessInfo.processInfo.environment["ONLY"], !only.split(separator: ",").contains(Substring(name)) { return }
    let r = ImageRenderer(content: view.environment(\.colorScheme, .dark))
    r.scale = scale
    r.isOpaque = false
    guard let cg = r.cgImage else { print("failed: \(name)"); return }
    let rep = NSBitmapImageRep(cgImage: cg)
    let url = URL(fileURLWithPath: dir).appendingPathComponent(name)
    try! rep.representation(using: .png, properties: [:])!.write(to: url)
    print("wrote \(url.path)  \(cg.width)x\(cg.height)")
}

@MainActor
func run() {
    try? FileManager.default.createDirectory(atPath: texDir, withIntermediateDirectories: true)
    try? FileManager.default.createDirectory(atPath: capDir, withIntermediateDirectories: true)
    // Real screenshots in Promo/shots override these (same file names, @2x).
    save(ChatPanel(), texDir, "chat-permission.png", scale: 2)
    save(TerminalPanel(), texDir, "chat-terminal.png", scale: 2)
    save(Card(tint: .green, symbol: "checkmark", title: "작업 완료", name: "docs-site",
              detail: "README를 한국어로 옮기고 링크를 고쳤어요.", hint: "5분 걸림 · 클릭해서 이어서 말하기"), texDir, "card-done.png", scale: 2)

    for (file, symbol, name, tool) in [("chip-web-app.png", "pencil", "web-app", "Edit"),
                                        ("chip-ios-widget.png", "terminal", "ios-widget", "Bash"),
                                        ("chip-api-server.png", "doc.text.magnifyingglass", "api-server", "Read"),
                                        ("chip-docs-site.png", "person.2", "docs-site", "Agent")] {
        save(AgentChip(symbol: symbol, name: name, tool: tool).environment(\.colorScheme, .light), texDir, file, scale: 2)
    }
    save(PhoneOverlay().environment(\.colorScheme, .dark), texDir, "phone-overlay.png", scale: 2)
    save(Desktop().environment(\.colorScheme, .light), texDir, "desktop.png", scale: 2)
    save(Cursor(), texDir, "cursor.png", scale: 3)
    // captions per language, from Promo/copy/<lang>.json -> Promo/captions/<lang>/<id>.png
    let copyDir = URL(fileURLWithPath: capDir).deletingLastPathComponent().appendingPathComponent("copy")
    let langs = (try? FileManager.default.contentsOfDirectory(atPath: copyDir.path))?.filter { $0.hasSuffix(".json") } ?? []
    for file in langs {
        guard let data = try? Data(contentsOf: copyDir.appendingPathComponent(file)),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let cues = json["cues"] as? [[String: Any]] else { print("bad copy file \(file)"); continue }
        let lang = (file as NSString).deletingPathExtension
        let dir = (capDir as NSString).appendingPathComponent(lang)
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        for cue in cues {
            guard let id = cue["id"] as? String, let text = cue["text"] as? String else { continue }
            if (cue["placement"] as? String) == "title" {
                save(EndTitle(title: text, sub: cue["sub"] as? String ?? ""), dir, "\(id).png", scale: 1)
            } else {
                save(Caption(text: text), dir, "\(id).png", scale: 1)
            }
        }
    }
}

MainActor.assumeIsolated { run() }
