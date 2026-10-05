import AppKit

// MARK: - Sprite
// Pixel grid decoded from the Claude Code welcome logo:
//    ▐▛███▜▌
//   ▝▜█████▛▘
//     ▘▘ ▝▝
// Each quadrant block becomes one pixel, giving an 18 x 5 sprite. Terminal cells
// are twice as tall as wide, so every sprite pixel is drawn 1 wide x 2 tall.

func rgb(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat) -> CGColor {
    CGColor(red: r / 255, green: g / 255, blue: b / 255, alpha: 1)
}

let clawdOrange = rgb(215, 119, 87)
let eyeColor = rgb(20, 18, 18)
let starColor = rgb(255, 214, 90)
let cookieColor = rgb(210, 150, 90)
let chipColor = rgb(90, 52, 30)

enum Eyes { case open, closed, happy, wide, dizzy }
enum Legs { case stand, step, dangle, tucked }

/// Things Clawd can hold or wear during an activity.
enum Prop: Equatable {
    case none, balls, laptop, bubble(bulb: Bool), hammer(up: Bool), headphones, broom, mug(raised: Bool)
}

struct Pose {
    var legs: Legs = .stand
    var armL: CGFloat = 2      // claw row: 2 = rest, 1 = raised, 0.5 = way up
    var armR: CGFloat = 2
    var eyes: Eyes = .open
    var look: CGFloat = 0      // -1 left ... 1 right
    var eyeDY: CGFloat = 0     // negative looks up
    var bob: CGFloat = 0       // body offset in pixels, negative = up
    var squash: CGFloat = 1    // vertical stretch around the feet
    var mouth = 0              // 0 none, 1 chomp, 2 yawn
    var rotation: CGFloat = 0  // radians, used while tumbling through the air
    var phase: CGFloat = 0     // running clock for looping details
    var prop: Prop = .none
    var attention = false     // an Orca agent is waiting on the user

    var armsUp: Bool {
        get { armL < 2 && armR < 2 }
        set { armL = newValue ? 1 : 2; armR = armL }
    }
}

enum Glyph {
    case heart, z, bang, note, question, crumb, star, steam, dust, bit

    var rows: [String] {
        switch self {
        case .heart:    return [".#.#.", "#####", ".###.", "..#.."]
        case .z:        return ["###", "..#", ".#.", "#..", "###"]
        case .bang:     return ["#", "#", "#", ".", "#"]
        case .note:     return ["..##", "..#.", "..#.", "###.", "##.."]
        case .question: return ["###", "..#", ".##", "...", ".#."]
        case .crumb:    return ["#"]
        case .star:     return [".#.", "###", ".#."]
        case .steam:    return ["#.", ".#", "#.", ".#"]
        case .dust:     return [".##.", "####", ".##."]
        case .bit:      return ["#", "#"]
        }
    }

    var color: CGColor {
        switch self {
        case .heart:           return rgb(237, 92, 115)
        case .z:               return rgb(217, 217, 230)
        case .bang, .question: return rgb(245, 245, 245)
        case .note:            return rgb(130, 190, 255)
        case .crumb:           return cookieColor
        case .star:            return starColor
        case .steam:           return rgb(200, 200, 205)
        case .dust:            return rgb(170, 160, 150)
        case .bit:             return rgb(120, 220, 140)
        }
    }

    /// Upward drift in pixel units per second (negative falls).
    var rise: CGFloat {
        switch self {
        case .heart, .z:  return 3
        case .note, .steam, .bit: return 2.5
        case .bang, .question, .dust: return 1
        case .star:       return 2
        case .crumb:      return -5
        }
    }
}

struct Effect {
    var glyph: Glyph
    var x: CGFloat, y: CGFloat // in pixel units, window space
    var vx: CGFloat = 0
    var age: CGFloat = 0
    var life: CGFloat = 1.6
    var color: CGColor? = nil
    var rise: CGFloat? = nil
}

/// Canvas in pixel units. Sprite sits at the bottom, effects float above it.
let canvasW: CGFloat = 20, canvasH: CGFloat = 22
let spriteX: CGFloat = 1, spriteY: CGFloat = 10   // sprite row 0 lands here
let pixelH: CGFloat = 2
let feetY = spriteY + 5 * pixelH                   // bottom of the legs

func drawGlyph(_ ctx: CGContext, _ rows: [String], x: CGFloat, y: CGFloat, size s: CGFloat) {
    for (ry, row) in rows.enumerated() {
        for (rx, ch) in row.enumerated() where ch == "#" {
            ctx.fill(CGRect(x: x + CGFloat(rx) * s, y: y + CGFloat(ry) * s, width: s, height: s))
        }
    }
}

/// Draws into a y-down context where 1 unit = 1 sprite pixel.
func render(_ ctx: CGContext, pose: Pose, effects: [Effect]) {
    // px: sprite-grid coordinates (rows are 2 units tall), moves with the body bob.
    func px(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat = 1, _ h: CGFloat = 1) {
        ctx.fill(CGRect(x: spriteX + x, y: spriteY + (y + pose.bob) * pixelH, width: w, height: h * pixelH))
    }
    // bp / ap: canvas units, with and without the body bob.
    func bp(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat) {
        ctx.fill(CGRect(x: x, y: y + pose.bob * pixelH, width: w, height: h))
    }
    func ap(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat) {
        ctx.fill(CGRect(x: x, y: y, width: w, height: h))
    }

    ctx.saveGState()
    if pose.rotation != 0 {
        let cx = spriteX + 9, cy = spriteY + 2.5 * pixelH
        ctx.translateBy(x: cx, y: cy)
        ctx.rotate(by: pose.rotation)
        ctx.translateBy(x: -cx, y: -cy)
    }
    if pose.squash != 1 {
        // Tucked legs mean the body itself rests on the ground.
        let anchor = pose.legs == .tucked ? spriteY + 4 * pixelH : feetY
        ctx.translateBy(x: 0, y: feetY)
        ctx.scaleBy(x: 1, y: pose.squash)
        ctx.translateBy(x: 0, y: -anchor)
    }

    if pose.prop == .headphones {
        ctx.setFillColor(rgb(60, 60, 72))
        bp(4, 9.2, 12, 0.6); bp(3.4, 9.2, 0.6, 2.4); bp(16, 9.2, 0.6, 2.4)
    }

    ctx.setFillColor(clawdOrange)
    px(3, 0, 12, 4)                       // body
    px(1, pose.armL, 2); px(15, pose.armR, 2)

    // Legs are not bobbed with the body so feet stay planted.
    if pose.legs != .tucked {
        let legXs: [CGFloat] = pose.legs == .step ? [5, 7, 10, 12] : [4, 6, 11, 13]
        let legH: CGFloat = pose.legs == .dangle ? 2 : 1
        let lift = min(pose.bob, 0)
        for x in legXs {
            ctx.fill(CGRect(x: spriteX + x, y: spriteY + (4 + lift) * pixelH, width: 1, height: (legH - lift) * pixelH))
        }
    }

    ctx.setFillColor(eyeColor)
    let shift = max(-1, min(1, pose.look)) * 0.3
    let ey = 1 + pose.eyeDY
    for (i, ex) in [CGFloat(5), 12].enumerated() {
        switch pose.eyes {
        case .open:   px(ex + shift, ey)
        case .wide:   px(ex + shift - 0.15, ey - 0.15, 1.3, 1.3)
        case .closed: px(ex - 0.1, 1.6, 1.2, 0.3)
        case .happy:  // ^ shape
            px(ex - 0.2, 1.4, 0.45, 0.3); px(ex + 0.25, 1.1, 0.5, 0.3); px(ex + 0.75, 1.4, 0.45, 0.3)
        case .dizzy:
            let sign: CGFloat = i == 0 ? 1 : -1
            px(ex + sin(pose.phase * 18) * 0.3 * sign, 1 + cos(pose.phase * 18) * 0.2 * sign)
        }
    }
    switch pose.mouth {
    case 1: px(8, 2.4, 2, 0.5)
    case 2: px(8.4, 2.1, 1.2, 0.9)
    default: break
    }

    switch pose.prop {
    case .none, .balls, .bubble: break
    case .laptop:
        ctx.setFillColor(rgb(175, 180, 190)); ap(6.5, 15, 7, 4.5)
        ctx.setFillColor(rgb(120, 125, 135)); ap(5, 19.3, 10, 0.7)
        ctx.setFillColor(rgb(235, 238, 245)); ap(9.6, 16.8, 0.8, 0.8)
    case .hammer(let up):
        ctx.setFillColor(rgb(150, 100, 60)); ap(7, 16, 6, 4)
        ctx.setFillColor(rgb(110, 70, 40)); ap(7, 17.8, 6, 0.4); ap(9.8, 16, 0.4, 4)
        ctx.setFillColor(rgb(140, 95, 55))
        if up { bp(17.2, 8.5, 0.6, 4.5) } else { bp(13, 15.1, 3.5, 0.6) }
        ctx.setFillColor(rgb(150, 155, 165))
        if up { bp(16, 7.3, 3, 1.4) } else { bp(11.6, 14.8, 1.4, 1.2) }
    case .headphones:
        ctx.setFillColor(rgb(70, 70, 85)); bp(2.8, 11, 1.5, 2.6); bp(15.7, 11, 1.5, 2.6)
        ctx.setFillColor(rgb(230, 90, 90)); bp(3.2, 11.8, 0.7, 1); bp(16.1, 11.8, 0.7, 1)
    case .broom:
        let sway = sin(pose.phase * 6)
        ctx.setFillColor(rgb(140, 95, 55))
        for k in 0..<5 {
            let f = CGFloat(k) / 4
            bp(17 - f * (2 - sway * 0.5), 12 + f * 5.5, 0.6, 1.6)
        }
        ctx.setFillColor(rgb(220, 180, 90)); ap(13.5 + sway * 0.6, 18.3, 3.5, 1.7)
    case .mug(let raised):
        let (x, y): (CGFloat, CGFloat) = raised ? (13.6, 14.2) : (16.3, 15.2)
        ctx.setFillColor(rgb(242, 240, 232)); bp(x, y, 1.8, 2); bp(x + 1.8, y + 0.5, 0.5, 1)
        ctx.setFillColor(rgb(110, 70, 40)); bp(x, y, 1.8, 0.4)
    }
    ctx.restoreGState()

    switch pose.prop {
    case .balls:
        // Each ball arcs from the left claw over the head to the right claw.
        let colors = [rgb(235, 90, 80), rgb(90, 150, 240), rgb(250, 200, 70)]
        for i in 0..<3 {
            var t = pose.phase * 1.1 + CGFloat(i) / 3
            t -= floor(t)
            ctx.setFillColor(colors[i])
            ap(2.6 + 14 * t, 12.5 - 36 * t * (1 - t), 1.2, 1.2)
        }
    case .bubble(let bulb):
        ctx.setFillColor(rgb(245, 245, 245))
        ap(14.6, 8.4, 0.8, 0.8); ap(15.8, 6.6, 1.1, 1.1)
        ap(13.5, 0.6, 5.6, 5.2); ap(13, 1.1, 6.6, 4.2)
        if bulb {
            ctx.setFillColor(starColor)
            drawGlyph(ctx, [".###.", "#####", "#####", ".###."], x: 15.05, y: 1.3, size: 0.7)
            ctx.setFillColor(rgb(150, 150, 160)); ap(15.75, 4.1, 1.4, 0.7)
        } else {
            ctx.setFillColor(eyeColor)
            let dots = Int(pose.phase * 2.5) % 4
            for d in 0..<dots { ap(14.3 + CGFloat(d) * 1.5, 2.8, 0.8, 0.8) }
        }
    default: break
    }

    if pose.attention {
        ctx.setFillColor(starColor)
        let hop: CGFloat = sin(pose.phase * 6) > 0 ? 0 : 0.6
        drawGlyph(ctx, Glyph.bang.rows, x: 9.6, y: 3.5 + hop + pose.bob * pixelH, size: 0.8)
    }

    if pose.eyes == .dizzy {
        ctx.setFillColor(starColor)
        for i in 0..<3 {
            let a = pose.phase * 5 + CGFloat(i) * 2.094
            let x = spriteX + 9 + cos(a) * 6, y = spriteY - 1.5 + sin(a) * 1.2
            drawGlyph(ctx, Glyph.star.rows, x: x - 0.75, y: y - 0.75, size: 0.5)
        }
    }

    for e in effects {
        let fade = min(1, max(0, (e.life - e.age) / (e.life * 0.4)))
        let size: CGFloat = e.glyph == .z ? 0.45 + e.age * 0.25 : (e.glyph == .crumb ? 0.6 : 0.5)
        ctx.setFillColor((e.color ?? e.glyph.color).copy(alpha: fade)!)
        drawGlyph(ctx, e.glyph.rows, x: e.x + e.vx * e.age, y: e.y - (e.rise ?? e.glyph.rise) * e.age, size: size)
    }
}

// MARK: - Activities
// Things Clawd does on its own. Poses are pure functions of time.

enum Activity: CaseIterable {
    case juggle, type, think, build, groove, sweep, coffee, celebrate, lookAround, stretch, sneeze

    /// Tricks for idle time. The work ones mirror agents and celebrating marks a finished task,
    /// so neither plays by chance and Clawd never looks busy or done when it isn't.
    static let resting: [Activity] = [.groove, .sweep, .coffee, .stretch, .sneeze]

    var title: String {
        switch self {
        case .juggle: return String(localized: "Juggle", comment: "Something Clawd does, in the Tricks menu")
        case .type: return String(localized: "Code", comment: "Something Clawd does, in the Tricks menu")
        case .think: return String(localized: "Think", comment: "Something Clawd does, in the Tricks menu")
        case .build: return String(localized: "Hammer", comment: "Something Clawd does, in the Tricks menu")
        case .groove: return String(localized: "Listen to music", comment: "Something Clawd does, in the Tricks menu")
        case .sweep: return String(localized: "Sweep", comment: "Something Clawd does, in the Tricks menu")
        case .coffee: return String(localized: "Drink coffee", comment: "Something Clawd does, in the Tricks menu")
        case .celebrate: return String(localized: "Celebrate", comment: "Something Clawd does, in the Tricks menu")
        case .lookAround: return String(localized: "Look around", comment: "Something Clawd does, in the Tricks menu")
        case .stretch: return String(localized: "Stretch", comment: "Something Clawd does, in the Tricks menu")
        case .sneeze: return String(localized: "Sneeze", comment: "Something Clawd does, in the Tricks menu")
        }
    }

    var duration: CGFloat {
        switch self {
        case .juggle, .groove, .sweep: return 6
        case .type, .coffee: return 7
        case .think: return 4.5
        case .build: return 5
        case .celebrate, .lookAround: return 3
        case .stretch: return 2.2
        case .sneeze: return 2
        }
    }
}

let thinkBulbAt: CGFloat = 3
let sneezeAt: CGFloat = 1.2

func activityPose(_ a: Activity, t: CGFloat, dir: CGFloat) -> Pose {
    var p = Pose()
    p.phase = t
    let beat = Int(t * 4) % 2 == 0
    switch a {
    case .juggle:
        p.prop = .balls
        var c = t * 3.3
        c -= floor(c)
        p.armL = c < 0.35 ? 1 : 2
        p.armR = c > 0.65 ? 1 : 2
        p.eyeDY = -0.3
        p.look = cos(t * 3.3 * .pi * 2) * -0.6
    case .type:
        p.prop = .laptop
        p.eyeDY = 0.3
        let k = Int(t * 10) % 2 == 0
        p.armL = k ? 1.75 : 2
        p.armR = k ? 2 : 1.75
        if Int(t) % 4 == 3 { p.eyeDY = 0; p.look = 0.8 }   // glance away now and then
    case .think:
        let bulb = t > thinkBulbAt
        p.prop = .bubble(bulb: bulb)
        p.look = bulb ? 0 : 0.8
        p.eyeDY = bulb ? 0 : -0.35
        p.eyes = bulb ? .wide : .open
        p.armR = bulb ? 1 : 1.6
    case .build:
        var c = t * 2.2
        c -= floor(c)
        let up = c < 0.5
        p.prop = .hammer(up: up)
        p.armR = up ? 1 : 2
        p.eyeDY = 0.3
        p.look = -0.2
    case .groove:
        p.prop = .headphones
        p.eyes = .happy
        p.bob = beat ? 0 : -0.25
        p.armL = beat ? 1.5 : 2
        p.armR = beat ? 2 : 1.5
        p.legs = beat ? .stand : .step
    case .sweep:
        p.prop = .broom
        p.eyeDY = 0.3
        p.look = dir * 0.5
        p.armR = 1.6
        p.legs = Int(t * 3) % 2 == 0 ? .stand : .step
    case .coffee:
        let c = t.truncatingRemainder(dividingBy: 3)
        let sip = c > 1.6 && c < 2.6
        p.prop = .mug(raised: sip)
        p.armR = sip ? 1 : 2
        p.eyes = sip ? .closed : (c > 2.6 ? .happy : .open)
    case .celebrate:
        p.armL = beat ? 0.5 : 1
        p.armR = beat ? 1 : 0.5
        p.eyes = .happy
    case .lookAround:
        p.look = t < 0.9 ? -1 : (t < 1.8 ? 1 : 0)
        p.eyes = t < 1.8 ? .open : .wide
    case .stretch:
        let back = t > 1.5
        p.squash = back ? 1 : 1 + 0.15 * min(1, t / 0.6)
        p.armsUp = !back
        if !back { p.armL = 0.5; p.armR = 0.5 }
        p.eyes = back ? .happy : .closed
    case .sneeze:
        if t < sneezeAt {
            p.eyes = .closed
            p.bob = -0.25 * t / sneezeAt
            p.squash = 1 + 0.06 * t / sneezeAt
            p.mouth = t > 0.6 ? 2 : 0
        } else {
            p.eyes = .closed
            p.squash = 0.9
            p.armsUp = true
        }
    }
    return p
}

/// Yawn, nod off, flop over, then sleep.
func sleepPose(t: CGFloat) -> Pose {
    var p = Pose()
    p.phase = t
    p.eyes = .closed
    switch t {
    case ..<1.5:
        p.mouth = 2
        p.armsUp = true
        p.squash = 1.05
    case ..<4.5:
        p.eyes = Int(t * 1.2) % 3 == 0 ? .open : .closed
        p.bob = Int(t * 1.25) % 2 == 0 ? 0 : 0.2
    case ..<5:
        p.squash = 1 - 0.35 * (t - 4.5) / 0.5
        p.legs = t > 4.7 ? .tucked : .stand
    default:
        p.legs = .tucked
        p.squash = 0.65 + sin(t * 2) * 0.02
    }
    return p
}
