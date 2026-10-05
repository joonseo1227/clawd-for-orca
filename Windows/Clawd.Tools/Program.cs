// Clawd developer tools.
//   clawd-tools icon <out.ico>        write the app icon, drawn from the sprite
//   clawd-tools demo <dir> [ko|en]    write demo agents and transcripts (Scripts/demo/make_demo.py)
//   clawd-tools orca                  print the agents Clawd sees and any permission prompt on screen
//   clawd-tools sessions              live Claude Code sessions and their Orca pane keys
//   clawd-tools timeline <file.jsonl> print the chat timeline built from a transcript
using Clawd.Core;
using Clawd.Core.Orca;
using Clawd.Core.Pet;
using Clawd.Core.Transcripts;
using Clawd.Tools;

var command = args.FirstOrDefault();
switch (command)
{
    case "icon" when args.Length > 1:
        File.WriteAllBytes(args[1], IconArt.Ico(IconArt.AppIcon));
        Console.WriteLine(args[1]);
        return 0;

    case "demo":
        Console.WriteLine(Demo.Write(args.Length > 1 ? args[1] : "demo-out",
            args.Length > 2 ? args[2] : Environment.GetEnvironmentVariable("CLAWD_DEMO_LANG") ?? "ko"));
        return 0;

    case "orca":
    {
        var watcher = new OrcaWatcher();
        var client = watcher.Client;
        Console.WriteLine($"runtime: {client.Runtime.MetadataPath}");
        Console.WriteLine($"install: {watcher.Installation?.AppDirectory ?? "-"}  cli: {watcher.Installation?.Cli ?? "-"}");
        if (await client.WorktreesAsync() is not { } agents)
        {
            Console.WriteLine("Orca isn't running");
            return 1;
        }
        foreach (var a in agents)
        {
            Console.WriteLine($"{a.State,-8} {a.PaneKey} {a.Name} {a.Tool ?? "-"} → {a.Activity}");
            if (a.NeedsYou && await client.ScreenAsync(a) is { } screen && PermissionPrompt.Parse(screen) is { } p)
            {
                Console.WriteLine($"     {p.Question} | {string.Join(" / ", p.Detail)}");
                foreach (var o in p.Options) Console.WriteLine($"      {o.Number} {o.Title} ({o.Label})");
            }
        }
        return 0;
    }

    case "sessions":
        foreach (var s in Transcripts.Sessions()) Console.WriteLine($"{s.Pid} {s.Id[..Math.Min(8, s.Id.Length)]} {s.PaneKey ?? "-"} {s.Cwd}");
        return 0;

    case "timeline" when args.Length > 1:
        foreach (var item in Transcripts.Timeline(args[1]))
        {
            static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
            Console.WriteLine(item.Kind switch
            {
                TimelineKind.Thinking => $"[생각] {(item.Text.Length == 0 ? "(내용 없음)" : Cut(item.Text, 100))}",
                TimelineKind.User => $"[나] {Cut(item.Text, 60)}",
                TimelineKind.Text => $"[Claude] {Cut(item.Text, 60)}",
                _ => $"[{item.ToolName}] {item.Text} → {(item.Result is { } r ? Cut(r, 40) : "실행 중")}",
            });
        }
        return 0;

    default:
        Console.Error.WriteLine("usage: clawd-tools icon <out.ico> | demo <dir> [ko|en] | orca | sessions | timeline <file.jsonl>");
        return 2;
}
