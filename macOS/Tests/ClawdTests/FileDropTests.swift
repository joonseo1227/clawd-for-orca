import Foundation
import Testing
@testable import Clawd

@Suite struct FileDropTests {
    @Test func escapesLikeTerminal() {
        #expect(FileDrop.escape("/Users/me/My Files/report (1).png") == #"/Users/me/My\ Files/report\ \(1\).png"#)
        #expect(FileDrop.escape("/tmp/한글 파일.txt") == #"/tmp/한글\ 파일.txt"#)
        #expect(FileDrop.escape("/plain/path.swift") == "/plain/path.swift")
    }

    @Test func joinsOnlyFileURLs() {
        let urls = [URL(fileURLWithPath: "/a b.txt"), URL(string: "https://example.com")!, URL(fileURLWithPath: "/c.png")]
        #expect(FileDrop.text(for: urls) == #"/a\ b.txt /c.png"#)
    }

    @Test func appendsWithOneSpace() {
        #expect(FileDrop.append("/x", to: "") == "/x ")
        #expect(FileDrop.append("/x", to: "look at") == "look at /x ")
        #expect(FileDrop.append("/x", to: "look at ") == "look at /x ")
        #expect(FileDrop.append("", to: "keep") == "keep")
    }

    @Test func pastesBracketed() {
        #expect(FileDrop.paste("/x") == "\u{1b}[200~/x \u{1b}[201~")
    }
}
