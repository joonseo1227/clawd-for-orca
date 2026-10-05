// Clawd desktop pet for macOS.
//   ./clawd               run the pet
//   ./clawd --orca        print the Orca agents Clawd is watching
//   ./clawd --timeline f  print the chat timeline built from a transcript
//   ./clawd --timeline-replay f  check the incremental transcript reader against fresh reads
import AppKit
import ImageIO

let args = CommandLine.arguments
if args.contains("--orca") {
    // Debug: print the agents Clawd sees in Orca.
    let client = OrcaClient(cli: OrcaInstallation.locate()?.cli)
    guard let agents = OrcaAgent.parse(OrcaRuntime.call("worktree.ps", [:])) else {
        print("Orca isn't running"); exit(1)
    }
    for a in agents {
        print(a.state.padding(toLength: 8, withPad: " ", startingAt: 0), a.paneKey, a.name, a.tool ?? "-", "→", a.activity)
        if a.needsYou, let screen = client.screen(of: a), let p = PermissionPrompt.parse(screen) {
            print("    ", p.question, "|", p.detail.joined(separator: " / "))
            for o in p.options { print("     ", o.number, o.title, "(\(o.label))") }
        }
    }
} else if let i = args.firstIndex(of: "--timeline"), i + 1 < args.count {
    // Debug: print the chat timeline Clawd builds from a Claude Code transcript.
    for item in Transcripts.timeline(URL(fileURLWithPath: args[i + 1])) {
        switch item.kind {
        case .thinking: print("[thinking]", item.text.isEmpty ? "(empty)" : String(item.text.prefix(100)))
        case .user: print("[me]", String(item.text.prefix(60)))
        case .text: print("[Claude]", String(item.text.prefix(60)))
        case .tool(let name, let result, _): print("[\(name)]", item.text, "→", result.map { String($0.prefix(40)) } ?? "running")
        }
    }
} else if let i = args.firstIndex(of: "--timeline-replay"), i + 1 < args.count {
    // Debug: grow a copy of a transcript from half its size in uneven chunks (cutting lines in
    // two), then truncate it and replace it, checking after every step that the incremental
    // reader returns exactly what a fresh reader builds from the same bytes.
    guard let data = try? Data(contentsOf: URL(fileURLWithPath: args[i + 1])) else { print("unreadable"); exit(1) }
    let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("clawd-replay-\(getpid()).jsonl")
    defer { try? FileManager.default.removeItem(at: tmp) }
    var seed: UInt64 = 42
    func chunk() -> Int { seed = seed &* 6364136223846793005 &+ 1442695040888963407; return Int(seed >> 33) % 300_000 + 1 }
    var pos = data.count / 2
    try! data.prefix(pos).write(to: tmp)
    let reader = TranscriptReader(url: tmp)
    var steps = 0, mismatches = 0, windowed = 0, unchanged = 0
    func check(_ label: String) {
        let (inc, changed) = reader.timeline()
        let fresh = TranscriptReader(url: tmp).timeline().items
        steps += 1
        if !changed { unchanged += 1 }
        guard inc != fresh else { return }
        // A fresh read sees at most 6 MB; when the prompt lies further back it starts mid-turn,
        // while the incremental reader still knows the whole turn. Those differ only at the front.
        let tail = fresh.dropFirst()
        if fresh.first?.kind != .user, inc.count >= tail.count, Array(inc.suffix(tail.count)) == Array(tail) {
            windowed += 1
        } else {
            mismatches += 1
            print("mismatch \(label): \(inc.count) vs \(fresh.count) items",
                  inc.first?.id ?? "-", fresh.first?.id ?? "-")
        }
    }
    check("start")
    let handle = try! FileHandle(forWritingTo: tmp)
    while pos < data.count {
        let end = min(data.count, pos + chunk())
        try! handle.seekToEnd()
        try! handle.write(contentsOf: data[pos..<end])
        pos = end
        check("at \(pos)")
    }
    try! handle.truncate(atOffset: UInt64(data.count / 3))
    try! handle.close()
    check("truncated")
    try! data.prefix(data.count * 2 / 3).write(to: tmp, options: .atomic)   // new inode
    check("replaced")
    print("\(steps) steps, \(mismatches) mismatches, \(windowed) prompt beyond fresh window, \(unchanged) reported unchanged,",
          reader.timeline().items.count, "items, ids unique:",
          Set(reader.timeline().items.map(\.id)).count == reader.timeline().items.count)
} else if args.contains("--sessions") {
    // Debug: which Claude Code session runs in which Orca pane.
    for s in Transcripts.sessions() { print(s.pid, s.id.prefix(8), s.paneKey ?? "-", s.cwd) }
} else if let i = args.firstIndex(of: "--icon-layer"), i + 1 < args.count {
    // Build step: Clawd as a transparent 1024 px layer for the app icon.
    let size = 1024
    let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    // Sprite spans 18 x 10 units; scale it to ~70% of the canvas and centre it.
    let unit = CGFloat(size) * 0.7 / 18
    ctx.translateBy(x: (CGFloat(size) - 18 * unit) / 2, y: CGFloat(size) - (CGFloat(size) - 10 * unit) / 2)
    ctx.scaleBy(x: unit, y: -unit)
    ctx.translateBy(x: -spriteX, y: -spriteY)
    render(ctx, pose: Pose(), effects: [])
    let dest = CGImageDestinationCreateWithURL(URL(fileURLWithPath: args[i + 1]) as CFURL, "public.png" as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, ctx.makeImage()!, nil)
    CGImageDestinationFinalize(dest)
} else {
    // A write to a pipe or socket whose other end is gone must fail with EPIPE, not end the app.
    signal(SIGPIPE, SIG_IGN)
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
