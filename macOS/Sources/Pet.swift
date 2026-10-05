import AppKit

// MARK: - Pet behaviour

struct Env {
    var bounds: NSRect
    var size: CGSize
    var mouse: CGPoint
    var mouseSpeed: CGFloat
    var snacks: [Snack]
}

final class Pet {
    enum State { case walk, idle, sleep, drag, fly, hop, wave, pet, dizzy, follow, chase, eat, act, alert, hold }

    let scale: CGFloat
    var state: State = .idle
    var stateTime: CGFloat = 0
    var stateLength: CGFloat = 2
    var dir: CGFloat = 1
    var pos = CGPoint.zero        // window origin, screen points
    var vel = CGPoint.zero        // points per second
    var spin: CGFloat = 0
    var flightTopSpeed: CGFloat = 0
    var hopEyes: Eyes = .happy
    var following = false

    var clock: CGFloat = 0
    var tick = 0
    var blinkIn: CGFloat = 3
    var blinking: CGFloat = 0
    var rub: CGFloat = 0           // how much the cursor has been rubbing Clawd lately
    var happyFor: CGFloat = 0      // lingering happy face after petting or eating
    var startleCooldown: CGFloat = 0
    var mouseAway: CGFloat = 0
    var mouseDX: CGFloat = 0
    var everyCounter: CGFloat = 0
    var fired: Set<Int> = []
    var boredom: CGFloat = 0       // seconds since the user last played with Clawd
    var activity: Activity = .stretch
    var workActivities: [Activity] = [] {  // mirrors what Orca agents are doing right now
        // Work starting wakes Clawd, so a sleeping pet never hides that agents are busy.
        didSet { if oldValue.isEmpty && !workActivities.isEmpty && state == .sleep { start(.stretch) } }
    }
    var attention = false
    var holding = false                   // stand under the waiting-agent card
    var frozen = false                    // held in place (menu or chat open, pointer on Clawd)
    weak var meal: Snack?

    var effects: [Effect] = []
    var pose = Pose()

    init(scale: CGFloat) { self.scale = scale }

    func ground(_ env: Env) -> CGFloat { env.bounds.minY - (canvasH - feetY) * scale }

    func enter(_ s: State, length: CGFloat = 0) {
        state = s; stateTime = 0; stateLength = length; everyCounter = 0; fired = []
    }

    func emit(_ g: Glyph, x: CGFloat = 9, y: CGFloat = 5, vx: CGFloat = 0, life: CGFloat = 1.6,
              color: CGColor? = nil, rise: CGFloat? = nil) {
        effects.append(Effect(glyph: g, x: x, y: y, vx: vx, life: life, color: color, rise: rise))
    }

    /// True the first time it is asked with `id` in the current state.
    func once(_ id: Int) -> Bool { fired.insert(id).inserted }

    /// True once every `interval` seconds while in the current state.
    func every(_ interval: CGFloat, dt: CGFloat) -> Bool {
        everyCounter += dt
        if everyCounter >= interval { everyCounter -= interval; return true }
        return false
    }

    func step(dt: CGFloat, env: Env) {
        clock += dt; tick += 1; stateTime += dt
        startleCooldown -= dt; happyFor -= dt; boredom += dt

        blinkIn -= dt
        if blinkIn <= 0 { blinking = 0.15; blinkIn = .random(in: 2...5) }
        blinking = max(0, blinking - dt)

        pose = Pose()
        pose.phase = clock
        pose.eyes = blinking > 0 ? .closed : (happyFor > 0 ? .happy : .open)

        let frame = CGRect(origin: pos, size: env.size)
        let bodyCenter = CGPoint(x: frame.midX, y: pos.y + (canvasH - spriteY - 2.5 * pixelH) * scale)
        mouseDX = env.mouse.x - bodyCenter.x
        let mouseDist = hypot(mouseDX, env.mouse.y - bodyCenter.y)
        let mouseInside = frame.contains(env.mouse)
        let groundY = ground(env)
        let onGround = pos.y <= groundY + 0.5
        let free = [.walk, .idle, .wave, .follow, .act, .hold].contains(state)
        let snack = env.snacks.min { abs($0.centerX - bodyCenter.x) < abs($1.centerX - bodyCenter.x) }

        // Rubbing the cursor back and forth over Clawd counts as petting.
        if mouseInside && (free || state == .pet) { rub = min(400, rub + env.mouseSpeed * dt) }
        rub = max(0, rub - 200 * dt)
        if free && rub > 150 { boredom = 0; enter(.pet) }

        // Say hi when the cursor comes back after a while.
        if mouseDist > 250 {
            mouseAway += dt
        } else {
            if mouseAway > 8 && (state == .walk || state == .idle) {
                dir = mouseDX >= 0 ? 1 : -1
                enter(.wave, length: 1.6)
                emit(.note, x: 15, y: 6)
            }
            mouseAway = 0
        }

        // A cursor whizzing past makes Clawd jump.
        if !mouseInside && mouseDist < 130 && env.mouseSpeed > 2500 && startleCooldown <= 0
            && (state == .walk || state == .idle || state == .act) && onGround {
            startleCooldown = 4
            vel.y = 300; hopEyes = .wide
            enter(.hop)
            emit(.bang, x: 9.7, y: 4)
        }

        // A trick or celebration plays out first; it ends in pickNext, which comes back here.
        if holding && free && state != .hold && state != .act { enter(.hold) }

        // Held in place: no walking on the spot, no running off after a snack yet.
        if frozen && [.walk, .follow, .chase].contains(state) { enter(.idle, length: 1) }

        if snack != nil && (free || state == .sleep) && !frozen {
            boredom = 0
            emit(.bang, x: 9.7, y: 4)
            enter(.chase)
        }

        var moveX: CGFloat = 0
        func scuttle(_ speed: CGFloat) {
            moveX = dir * speed * dt
            pose.legs = (tick / 4) % 2 == 0 ? .stand : .step
            pose.bob = (tick / 4) % 2 == 0 ? 0 : -0.25
            pose.look = dir
        }
        let atLeft = pos.x <= env.bounds.minX + 1
        let atRight = pos.x + env.size.width >= env.bounds.maxX - 1

        switch state {
        case .walk:
            if (dir < 0 && atLeft) || (dir > 0 && atRight) { dir = -dir }
            scuttle(30)
            if stateTime > stateLength { pickNext() }

        case .idle:
            pose.look = mouseDX / 150
            if stateTime > stateLength { pickNext() }

        case .follow:
            if !following { enter(.idle, length: 1); break }
            if abs(mouseDX) > 35 {
                dir = mouseDX > 0 ? 1 : -1
                scuttle(90)
            } else {
                pose.look = mouseDX / 60
                if env.mouse.y > bodyCenter.y + 40 { pose.armsUp = (tick / 6) % 2 == 0 }
            }

        case .sleep:
            pose = sleepPose(t: stateTime)
            if stateTime > 5 && every(1.5, dt: dt) { emit(.z, x: 15, y: 11) }
            if stateLength > 0 && stateTime > stateLength { start(.stretch) }

        case .drag:
            pose.legs = .dangle
            pose.armsUp = (tick / 4) % 2 == 0
            pose.eyes = .wide
            pose.look = 0

        case .fly:
            vel.y -= gravity * dt
            pos.x += vel.x * dt
            pos.y += vel.y * dt
            flightTopSpeed = max(flightTopSpeed, hypot(vel.x, vel.y))
            if pos.x < env.bounds.minX { pos.x = env.bounds.minX; vel.x = abs(vel.x) * 0.6 }
            if pos.x + env.size.width > env.bounds.maxX { pos.x = env.bounds.maxX - env.size.width; vel.x = -abs(vel.x) * 0.6 }
            if pos.y + env.size.height > env.bounds.maxY { pos.y = env.bounds.maxY - env.size.height; vel.y = -abs(vel.y) * 0.5 }
            if flightTopSpeed > 900 { spin += vel.x * dt * 0.012 }
            pose.legs = .dangle
            pose.armsUp = true
            pose.eyes = .wide
            pose.rotation = spin
            if pos.y <= groundY {
                pos.y = groundY
                let impact = -vel.y
                if impact > 650 {
                    vel.y = impact * 0.4
                    vel.x *= 0.6
                } else {
                    vel = .zero; spin = 0
                    if flightTopSpeed > 1500 { enter(.dizzy, length: 2.5) } else { enter(.idle, length: 1.5) }
                }
            }

        case .hop:
            pose.armsUp = (tick / 3) % 2 == 0
            pose.eyes = hopEyes
            if onGround && vel.y <= 0 && stateTime > 0.1 { enter(.idle, length: .random(in: 1...2)) }

        case .wave:
            pose.armsUp = (tick / 5) % 2 == 0
            pose.eyes = .happy
            pose.look = dir
            if stateTime > stateLength { pickNext() }

        case .pet:
            pose.eyes = .happy
            pose.bob = sin(clock * 8) > 0 ? 0 : 0.15
            if every(0.35, dt: dt) { emit(.heart, x: .random(in: 3...13), y: 6) }
            if rub < 10 { happyFor = 2; enter(.idle, length: 2) }

        case .dizzy:
            pose.eyes = .dizzy
            pose.bob = sin(clock * 6) > 0 ? 0 : 0.15
            if stateTime > stateLength { emit(.question, x: 9.2, y: 4); enter(.idle, length: 2) }

        case .chase:
            guard let snack else { enter(.idle, length: 1); break }
            // Stop with the snack right at the claw tip instead of on top of it.
            let dx = snack.centerX - bodyCenter.x
            if abs(dx) > 55 {
                dir = dx > 0 ? 1 : -1
                scuttle(110)
            } else if snack.grounded {
                dir = dx > 0 ? 1 : -1
                meal = snack
                enter(.eat)
            } else {
                // Snack is still falling or being dangled above: beg for it.
                pose.armsUp = (tick / 4) % 2 == 0
                pose.eyes = .wide
                if onGround && snack.held && Double.random(in: 0...1) < Double(dt) * 0.8 { vel.y = 420 }
            }

        case .eat:
            guard let meal, meal.grounded, abs(meal.centerX - bodyCenter.x) < 70 else { enter(.chase); break }
            pose.mouth = (tick / 4) % 2 == 0 ? 1 : 0
            pose.eyes = .happy
            pose.look = dir
            if every(0.6, dt: dt) {
                meal.bites += 1
                meal.window.contentView?.needsDisplay = true
                let side: CGFloat = dir > 0 ? 15 : 3
                emit(.crumb, x: side, y: 13, vx: dir * 2, life: 0.6)
                emit(.crumb, x: side, y: 13, vx: dir * 4, life: 0.6)
                if meal.bites >= 3 {
                    meal.consume()
                    self.meal = nil
                    emit(.heart, x: 5, y: 6); emit(.heart, x: 11, y: 5)
                    happyFor = 2.5
                    enter(.idle, length: 2)
                }
            }

        case .hold:
            // Arms up under the card so it reads as "this one's for you".
            pose.armL = 0.5; pose.armR = 0.5
            pose.look = mouseDX / 150
            if !holding { pickNext() }

        case .alert:
            pose.eyes = .wide
            let flap = (tick / 3) % 2 == 0
            pose.armL = flap ? 0.5 : 1.5
            pose.armR = flap ? 1.5 : 0.5
            if onGround && once(Int(stateTime / 0.8)) {
                vel.y = 320
                emit(.bang, x: 9.7, y: 4, life: 0.8)
            }
            if stateTime > stateLength { pickNext() }

        case .act:
            let t = stateTime
            let blink = pose.eyes == .closed
            pose = activityPose(activity, t: t, dir: dir)
            if blink && pose.eyes == .open { pose.eyes = .closed }
            switch activity {
            case .type:
                if every(0.45, dt: dt) { emit(.bit, x: .random(in: 7...13), y: 12, life: 1) }
            case .build:
                if case .hammer(up: false) = pose.prop, once(Int(t * 2.2)) {
                    emit(.star, x: 11.5, y: 15, vx: -3, life: 0.5)
                    emit(.star, x: 13, y: 15, vx: 3, life: 0.5)
                }
            case .groove:
                if every(0.6, dt: dt) { emit(.note, x: .random(in: 2...16), y: 7) }
            case .sweep:
                if (dir < 0 && atLeft) || (dir > 0 && atRight) { dir = -dir }
                moveX = dir * 12 * dt
                if every(0.4, dt: dt) { emit(.dust, x: 14, y: 18, vx: -dir * 2, life: 0.8) }
            case .coffee:
                if every(0.5, dt: dt) {
                    let raised = pose.prop == .mug(raised: true)
                    emit(.steam, x: raised ? 14.2 : 16.9, y: raised ? 12.4 : 13.4, life: 1)
                }
            case .celebrate:
                if every(0.07, dt: dt) {
                    let colors = [rgb(235, 90, 80), rgb(90, 150, 240), rgb(250, 200, 70), rgb(120, 220, 140), rgb(237, 92, 115)]
                    emit(.crumb, x: .random(in: 1...19), y: 0, vx: .random(in: -2...2), life: 2.5,
                         color: colors.randomElement()!, rise: -.random(in: 4...8))
                }
                if onGround && vel.y <= 0 && once(Int(t / 0.7)) { vel.y = 350 }
            case .lookAround:
                if t > 2 && once(0) { emit(.question, x: 9.2, y: 4) }
            case .sneeze:
                if t > sneezeAt && once(0) {
                    vel.y = 250
                    emit(.dust, x: 9, y: 15, vx: -6, life: 0.6)
                    emit(.dust, x: 9, y: 15, vx: 6, life: 0.6)
                    emit(.bang, x: 15, y: 6, life: 1)
                }
            case .juggle, .think, .stretch:
                break
            }
            if t > stateLength {
                if activity == .think {
                    vel.y = 380; hopEyes = .happy
                    enter(.hop)
                    emit(.note, x: 9.5, y: 4)
                } else {
                    pickNext()
                }
            }
        }

        // Shared vertical physics for everything except flying and being held.
        if state != .fly && state != .drag {
            if !onGround || vel.y > 0 {
                vel.y -= gravity * dt
                pos.y += vel.y * dt
            }
            if pos.y <= groundY { pos.y = groundY; vel.y = max(vel.y, 0) }
            pos.x = max(env.bounds.minX, min(env.bounds.maxX - env.size.width, pos.x + moveX))
        }

        pose.attention = attention && state != .alert
        for i in effects.indices { effects[i].age += dt }
        effects.removeAll { $0.age > $0.life }
    }

    func pickNext() {
        if holding { enter(.hold); return }
        if following { enter(.follow); return }
        if !workActivities.isEmpty {
            // While agents work Clawd only mirrors them; tricks are for idle time.
            // Several agents at once means juggling half the time; otherwise copy one agent's tool,
            // so what each is doing still shows now and then.
            start(workActivities.count >= 2 && Bool.random() ? .juggle : workActivities.randomElement()!)
            return
        }
        if workActivities.isEmpty && boredom > 60 && Double.random(in: 0...1) < 0.3 {
            enter(.sleep, length: .random(in: 30...60))
            return
        }
        let r = Double.random(in: 0...1)
        if r < 0.3 && !frozen {
            dir = Bool.random() ? 1 : -1
            enter(.walk, length: .random(in: 2...6))
        } else if r < 0.5 {
            enter(.idle, length: .random(in: 1.5...4))
        } else {
            start(Activity.resting.filter { $0 != activity }.randomElement()!)
        }
    }

    func start(_ a: Activity) {
        activity = a
        if a == .sweep { dir = Bool.random() ? 1 : -1 }
        enter(.act, length: a.duration)
    }

    // MARK: Input

    func poked() {
        boredom = 0
        if state == .sleep {
            emit(.bang, x: 9.7, y: 4)
            vel.y = 300; hopEyes = .wide
            enter(.hop)
            return
        }
        vel.y = 400; hopEyes = .happy
        enter(.hop)
        emit(.heart, x: 7.5, y: 6)
    }

    func toggleFollow() {
        following.toggle()
        boredom = 0
        emit(following ? .note : .question, x: 9.5, y: 4)
        enter(following ? .follow : .idle, length: 2)
    }

    /// An Orca agent needs the user: make a scene about it.
    func alert() {
        if state == .drag || state == .fly { return }
        enter(.alert, length: 3.5)
    }

    func celebrate() {
        if [.drag, .fly, .eat, .alert].contains(state) { return }
        start(.celebrate)
    }

    func grabbed() {
        rub = 0; boredom = 0
        enter(.drag)
    }

    func thrown(_ v: CGPoint) {
        let speed = hypot(v.x, v.y)
        let k = speed > 3000 ? 3000 / speed : 1
        vel = CGPoint(x: v.x * k, y: v.y * k)
        flightTopSpeed = speed * k
        spin = 0
        enter(.fly)
    }
}
