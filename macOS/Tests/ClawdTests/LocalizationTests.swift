import Foundation
import Testing

/// The String Catalog: English source keys, every one translated to Korean.
@Suite struct LocalizationTests {
    static let macOS = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    static let catalogURL = macOS.appendingPathComponent("Resources/Localizable.xcstrings")

    static func hasHangul(_ s: String) -> Bool {
        s.unicodeScalars.contains { (0xAC00...0xD7A3).contains($0.value) || (0x1100...0x11FF).contains($0.value) || (0x3130...0x318F).contains($0.value) }
    }

    /// Format specifiers by type, ignoring positions: "%1$@ %2$lld" and "%@ %lld" match.
    static func specifiers(_ s: String) -> [String] {
        let regex = try! NSRegularExpression(pattern: #"%(?:\d+\$)?(@|lld|ld|d|f)"#)
        let ns = s as NSString
        return regex.matches(in: s, range: NSRange(location: 0, length: ns.length))
            .map { ns.substring(with: $0.range(at: 1)) }.sorted()
    }

    func catalog() throws -> [String: [String: Any]] {
        let data = try Data(contentsOf: Self.catalogURL)
        let json = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(json["sourceLanguage"] as? String == "en")
        return try #require(json["strings"] as? [String: [String: Any]])
    }

    /// The Korean string unit, or the plural form used for counts other than one.
    func korean(_ entry: [String: Any]) -> [String: Any]? {
        guard let ko = (entry["localizations"] as? [String: Any])?["ko"] as? [String: Any] else { return nil }
        if let unit = ko["stringUnit"] as? [String: Any] { return unit }
        let plural = (ko["variations"] as? [String: Any])?["plural"] as? [String: Any]
        return (plural?["other"] as? [String: Any])?["stringUnit"] as? [String: Any]
    }

    @Test func everyKeyHasAKoreanTranslation() throws {
        let strings = try catalog()
        #expect(strings.count > 100)
        for (key, entry) in strings {
            let unit = korean(entry)
            #expect(unit?["state"] as? String == "translated", "\(key) has no translated Korean")
            let value = unit?["value"] as? String ?? ""
            #expect(!value.isEmpty, "\(key) has an empty Korean translation")
            #expect(Self.specifiers(value) == Self.specifiers(key), "\(key) → \(value): format specifiers differ")
        }
    }

    @Test func sourceKeysAreEnglish() throws {
        for key in try catalog().keys {
            #expect(!Self.hasHangul(key), "Korean in source key \(key)")
        }
    }

    /// Comments may quote Korean; code may not.
    @Test func sourcesHaveNoKoreanStrings() throws {
        let sources = Self.macOS.appendingPathComponent("Sources")
        let files = try FileManager.default.contentsOfDirectory(at: sources, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "swift" }
        #expect(!files.isEmpty)
        for file in files {
            let text = try String(contentsOf: file, encoding: .utf8)
            for (i, line) in text.split(separator: "\n", omittingEmptySubsequences: false).enumerated()
            where Self.hasHangul(String(line)) && !line.trimmingCharacters(in: .whitespaces).hasPrefix("//") {
                Issue.record("\(file.lastPathComponent):\(i + 1) has Korean text; put it in the string catalog")
            }
        }
    }

    /// Compiles the catalog as build.sh does and reads the Korean table back.
    @Test func compiledKoreanTable() throws {
        let out = FileManager.default.temporaryDirectory.appendingPathComponent("clawd-strings-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: out) }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/xcrun")
        p.arguments = ["xcstringstool", "compile", Self.catalogURL.path, "--output-directory", out.path]
        try p.run()
        p.waitUntilExit()
        try #require(p.terminationStatus == 0)
        let ko = try #require(Bundle(url: out.appendingPathComponent("ko.lproj")))
        func t(_ key: String) -> String { ko.localizedString(forKey: key, value: nil, table: nil) }
        #expect(String(format: t("%lld hr %lld min"), 1, 5) == "1시간 5분")
        #expect(String(format: t("%@ ago"), "3분") == "3분 전")
        #expect(t("just now") == "방금")
        #expect(t("Always allow") == "항상 허용")
        #expect(String(format: t("+%lld more · %@"), 2, "x") == "외 2개 더 · x")
        // No particle right after the number: 로 or 으로 would depend on how it is read.
        #expect(String(format: t("Answer with the buttons above or ⌘1–%lld"), 3) == "위 버튼이나 ⌘1–3 키로 답해 주세요")
        // English plurals land in a .stringsdict.
        let dict = try Data(contentsOf: out.appendingPathComponent("en.lproj/Localizable.stringsdict"))
        let plist = try #require(try PropertyListSerialization.propertyList(from: dict, format: nil) as? [String: Any])
        let steps = try #require((plist["%lld steps"] as? [String: Any])?["value"] as? [String: Any])
        #expect(steps["one"] as? String == "%lld step")
        #expect(steps["other"] as? String == "%lld steps")
    }
}
