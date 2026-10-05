import AppKit
import Testing
@testable import Clawd

@MainActor @Suite struct PetTests {
    let env = Env(bounds: NSRect(x: 0, y: 0, width: 1200, height: 800),
                  size: CGSize(width: canvasW * 5, height: canvasH * 5),
                  mouse: CGPoint(x: 5000, y: 5000), mouseSpeed: 0, snacks: [])

    func pet() -> Pet {
        let p = Pet(scale: 5)
        p.pos = CGPoint(x: 500, y: p.ground(env))
        return p
    }

    func run(_ p: Pet, seconds: CGFloat) {
        for _ in 0..<Int(seconds * 30) { p.step(dt: 1 / 30, env: env) }
    }

    @Test func standsUnderTheCardWhileAnAgentWaits() {
        let p = pet()
        p.holding = true
        run(p, seconds: 0.1)
        #expect(p.state == .hold)
    }

    /// A trick picked from the menu, or the celebration for a finished task, used to be cut off
    /// on the next frame while another agent was waiting.
    @Test func trickPlaysOutWhileAnAgentWaits() {
        let p = pet()
        p.holding = true
        p.start(.celebrate)
        run(p, seconds: Activity.celebrate.duration - 0.5)
        #expect(p.state == .act && p.activity == .celebrate)
        run(p, seconds: 1.5)
        #expect(p.state == .hold)
    }

    @Test func wakesUpWhenAnAgentStartsWorking() {
        let p = pet()
        p.enter(.sleep)
        run(p, seconds: 1)
        p.workActivities = [.build]
        #expect(p.state == .act && p.activity == .stretch)
        run(p, seconds: Activity.stretch.duration + 0.1)
        #expect(p.state == .act && p.activity == .build)
    }

    @Test func restsWithoutWorkTricks() {
        let p = pet()
        for _ in 0..<200 {
            p.pickNext()
            if p.state == .act { #expect(Activity.resting.contains(p.activity)) }
        }
    }

    @Test func onlyMirrorsWorkWhileSeveralAgentsWork() {
        let p = pet()
        p.workActivities = [.build, .type]
        for _ in 0..<50 {
            p.pickNext()
            #expect(p.state == .act && [.juggle, .build, .type].contains(p.activity))
        }
    }
}
