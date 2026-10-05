import AppKit
import SwiftUI
import Testing
@testable import Clawd

@Suite struct TerminalPaletteTests {
    /// The colour under an appearance, resolved on a background thread, as AppKit may do.
    static func resolved(_ color: Color, _ name: NSAppearance.Name) async -> [Int] {
        await withCheckedContinuation { done in
            DispatchQueue.global().async {
                var rgb: [Int] = []
                NSAppearance(named: name)!.performAsCurrentDrawingAppearance {
                    let c = NSColor(color).usingColorSpace(.sRGB)!
                    rgb = [c.redComponent, c.greenComponent, c.blueComponent].map { Int(($0 * 255).rounded()) }
                }
                done.resume(returning: rgb)
            }
        }
    }

    @Test func followsAppearanceOffTheMainThread() async {
        #expect(await Self.resolved(ClaudePalette.error, .aqua) == [171, 43, 63])
        #expect(await Self.resolved(ClaudePalette.error, .darkAqua) == [255, 107, 128])
    }
}
