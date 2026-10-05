using Clawd.Core.Bridge;
using Clawd.Core.Chat;
using Clawd.Core.Orca;
using Clawd.Core.Pet;
using Clawd.Core.Platform;
using Clawd.Core.Terminal;

namespace Clawd.Core.Tests;

public class ChatModelTests
{
    private static ChatRow Row(string id, RowKind kind) => new(new OrcaAgent(id, id, "idle", null, null, null, null, null, false), kind, null, "");

    [Fact]
    public void MoveWalksVisibleRowsOnly()
    {
        var model = new ChatModel { Rows = [Row("a", RowKind.Question), Row("b", RowKind.Working), Row("c", RowKind.Resting)] };
        model.Selected = "a";
        model.Move(1);
        Assert.Equal("b", model.Selected);
        model.Move(1);   // resting is collapsed
        Assert.Equal("b", model.Selected);
        model.ShowResting = true;
        model.Move(5);
        Assert.Equal("c", model.Selected);
        model.Move(-9);
        Assert.Equal("a", model.Selected);
    }

    [Fact]
    public void DraftsAreKeptPerAgent()
    {
        var model = new ChatModel { Rows = [Row("a", RowKind.Question), Row("b", RowKind.Working)] };
        model.Selected = "a";
        model.Draft = "hello";
        model.Selected = "b";
        Assert.Equal("", model.Draft);
        model.Selected = "a";
        Assert.Equal("hello", model.Draft);
        model.Draft = "";
        Assert.False(model.Drafts.ContainsKey("a"));
    }

    [Fact]
    public void SelectionAndLayoutChangesAreReported()
    {
        var model = new ChatModel();
        var selections = new List<string?>();
        var layouts = 0;
        model.SelectionChanged += selections.Add;
        model.LayoutChanged += () => layouts++;
        model.Selected = "x";
        model.Selected = "x";
        model.Mode = ChatMode.Terminal;
        model.Viewport = (600, 400);
        model.Viewport = (600, 400);
        Assert.Equal(["x"], selections);
        Assert.Equal(2, layouts);
    }

    [Fact]
    public void TerminalViewOnlyForAgentsWithATerminal()
    {
        var model = new ChatModel { Rows = [Row("tab:leaf", RowKind.Working), Row("structured-agent-session-a1:b2", RowKind.Working)] };
        model.Mode = ChatMode.Terminal;
        model.Selected = "tab:leaf";
        Assert.True(model.ShowsTerminal);
        model.Selected = "structured-agent-session-a1:b2";
        Assert.False(model.ShowsTerminal);
        // The choice is kept for the next agent that has one.
        Assert.Equal(ChatMode.Terminal, model.Mode);
        model.Selected = "tab:leaf";
        Assert.True(model.ShowsTerminal);
    }
}

public class TerminalTests
{
    [Fact]
    public void KeysMapToTerminalBytes()
    {
        Assert.Equal("\r", TerminalKeys.Map(TerminalKeys.Enter, TerminalKeys.Modifiers.None));
        Assert.Equal("\u001b\r", TerminalKeys.Map(TerminalKeys.Enter, TerminalKeys.Modifiers.Shift));
        Assert.Equal("\u0003", TerminalKeys.Map('C', TerminalKeys.Modifiers.Control));
        Assert.Equal("\u001b", TerminalKeys.Map(TerminalKeys.OpenBracket, TerminalKeys.Modifiers.Control));
        Assert.Equal("\u001c", TerminalKeys.Map(TerminalKeys.Backslash, TerminalKeys.Modifiers.Control));
        Assert.Equal("\u001d", TerminalKeys.Map(TerminalKeys.CloseBracket, TerminalKeys.Modifiers.Control));
        Assert.Null(TerminalKeys.Map(TerminalKeys.OpenBracket, TerminalKeys.Modifiers.None));
        Assert.Equal("\u007f", TerminalKeys.Map(TerminalKeys.Back, TerminalKeys.Modifiers.None));
        Assert.Equal("\u0017", TerminalKeys.Map(TerminalKeys.Back, TerminalKeys.Modifiers.Control));
        Assert.Equal("\u001b[Z", TerminalKeys.Map(TerminalKeys.Tab, TerminalKeys.Modifiers.Shift));
        Assert.Equal("\u001b[A", TerminalKeys.Map(TerminalKeys.Up, TerminalKeys.Modifiers.None));
        Assert.Equal("\u001bb", TerminalKeys.Map(TerminalKeys.Left, TerminalKeys.Modifiers.Control));
        Assert.Null(TerminalKeys.Map('A', TerminalKeys.Modifiers.None));   // text comes through the text input
    }

    [Fact]
    public void StylerColoursClaudeCodeParts()
    {
        var lines = TerminalStyler.Lines(["⏺ Bash(npm test)", "  ⎿  PASS", "       ✓ ok", "✻ Thinking… (3s · esc to interrupt)", new string('─', 20), "  12 +  added", ""], null);
        Assert.Equal(6, lines.Count);   // trailing blank rows dropped when not fitted
        Assert.Equal(TerminalRole.Success, lines[0].Spans[0].Role);
        Assert.Equal("⏺", lines[0].Spans[0].Text);
        Assert.All(lines[1].Spans, s => Assert.Equal(TerminalRole.Dim, s.Role));
        Assert.All(lines[2].Spans, s => Assert.Equal(TerminalRole.Dim, s.Role));   // wrapped tool output stays dim
        Assert.True(lines[4].IsRule);
        Assert.Equal(TerminalRole.Success, lines[5].Spans[0].Role);
        Assert.Equal(10, TerminalStyler.Lines(["a"], 10).Count);
    }

    [Fact]
    public void CursorBecomesItsOwnSpan()
    {
        var spans = TerminalStyler.Style("❯ " + TerminalCursor.Mark + "Try it", dim: false);
        var cursor = Assert.Single(spans, s => s.Cursor);
        Assert.Equal("T", cursor.Text);
        Assert.Equal("❯ Try it", string.Concat(spans.Select(s => s.Text)));
        // At the end of the line the cursor sits on a space.
        Assert.Equal(" ", TerminalStyler.Style("❯ hi" + TerminalCursor.Mark, false).Single(s => s.Cursor).Text);
    }

    [Fact]
    public void WideCharactersTakeTwoCells() => Assert.Equal(6, TerminalStyler.Cells("ab한글"));
}

public class SettingsTests
{
    [Fact]
    public void ShortcutDisplayAndValidation()
    {
        Assert.Equal("Ctrl+Alt+J", Shortcut.Standard.Display);
        Assert.Null(Shortcut.FromKeyPress('J', Shortcut.Mods.Shift));      // would swallow typing
        Assert.Null(Shortcut.FromKeyPress(0x11, Shortcut.Mods.Control));   // Ctrl on its own
        Assert.Equal("F8", Shortcut.FromKeyPress(0x77, Shortcut.Mods.None)?.Display);
        Assert.Equal("Ctrl+Win+Shift+K", Shortcut.FromKeyPress('K', Shortcut.Mods.Control | Shortcut.Mods.Windows | Shortcut.Mods.Shift)?.Display);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("clawd-settings-").FullName, "settings.json");
        try
        {
            var s = Settings.Load(path);
            Assert.True(s.Sound);
            Assert.Equal(Shortcut.Standard, s.Shortcut);
            s.Hidden = true;
            s.Notifications = NotificationPolicy.Always;
            s.Shortcut = new Shortcut(0x77, Shortcut.Mods.None);
            s.Save();
            var again = Settings.Load(path);
            Assert.True(again.Hidden);
            Assert.Equal(NotificationPolicy.Always, again.Notifications);
            Assert.Equal(new Shortcut(0x77, Shortcut.Mods.None), again.Shortcut);
            File.WriteAllText(path, "{ not json");
            Assert.True(Settings.Load(path).Sound);   // a damaged file falls back to defaults
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}

public class PlatformTests
{
    [Fact]
    public void EnvironmentBlockParsing()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ProcessEnvironment.Parse("=C:=C:\\work\0ORCA_PANE_KEY=tab-1:leaf-2\0Path=C:\\bin;D:\\x\0EMPTY=\0\0junk=1\0", env);
        Assert.Equal("tab-1:leaf-2", env["ORCA_PANE_KEY"]);
        Assert.Equal("C:\\bin;D:\\x", env["PATH"]);
        Assert.Equal("", env["EMPTY"]);
        Assert.False(env.ContainsKey("junk"));   // past the block's end marker
        Assert.False(env.ContainsKey(""));
    }

    [Fact]
    public void FolderContainment()
    {
        Assert.True(Paths.IsSameOrInside("/w/project", "/w/project/"));
        Assert.True(Paths.IsSameOrInside("/w/project/sub", "/w/project"));
        Assert.False(Paths.IsSameOrInside("/w/project-two", "/w/project"));
        Assert.False(Paths.IsSameOrInside("/w/x", ""));
        if (OperatingSystem.IsWindows()) Assert.True(Paths.IsSameOrInside(@"c:\W\Project\sub", "C:/w/project"));
    }
}

public class PetTests
{
    [Fact]
    public void SpriteFillsWholePixelsAtIntegerScale()
    {
        var r = new Rasterizer((int)(Sprite.CanvasW * 5), (int)(Sprite.CanvasH * 5));
        r.Scale(5, 5);
        Sprite.Render(r, new Pose(), []);
        // Body: sprite x 3..15, rows 0..4 (2 units each) → canvas x 4..16, y 10..18.
        Assert.Equal(255, r.AlphaAt(5 * 5, 11 * 5));
        Assert.Equal(0, r.AlphaAt(1, 1));                 // above the head is clear (click-through)
        // Edges are hard: no partial alpha anywhere on a resting pose.
        for (var i = 3; i < r.Pixels.Length; i += 4) Assert.True(r.Pixels[i] is 0 or 255);
    }

    [Fact]
    public void ClearRectLeavesOutOnlyThatRectangle()
    {
        var r = new Rasterizer(10, 10);
        r.SetFill(new Rgba(1, 0, 0));
        r.FillRect(0, 0, 10, 10);
        r.ClearRect(-5, -5, 12, 8);                     // partly off the canvas: the top-left 7 x 3
        Assert.Equal(0, r.AlphaAt(0, 0));
        Assert.Equal(0, r.AlphaAt(6, 2));
        Assert.Equal(255, r.AlphaAt(7, 2));
        Assert.Equal(255, r.AlphaAt(6, 3));
        r.ClearRect(20, 20, 5, 5);                      // entirely outside: nothing happens
        Assert.Equal(255, r.AlphaAt(9, 9));
    }

    [Fact]
    public void EveryActivityAndEffectRenders()
    {
        var r = new Rasterizer(100, 110);
        r.Scale(5, 5);
        foreach (var a in Activities.All)
            for (var t = 0.0; t < a.Duration(); t += 0.37)
                Sprite.Render(r, Activities.Pose(a, t, 1), [new Effect(Glyph.Heart, 5, 6, age: 0.4), new Effect(Glyph.Z, 15, 11, age: 1)]);
        Sprite.Render(r, Activities.SleepPose(6), []);
        Sprite.Render(r, new Pose { Rotation = 1.2, Eyes = Eyes.Dizzy, Attention = true }, []);
        Assert.Contains(r.Pixels, b => b != 0);
    }

    [Fact]
    public void IconIsAValidIcoWithPngImages()
    {
        var ico = IconArt.Ico(IconArt.AppIcon);
        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
        Assert.Equal(IconArt.IcoSizes.Length, BitConverter.ToUInt16(ico, 4));
        var firstOffset = BitConverter.ToInt32(ico, 6 + 12);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], ico[firstOffset..(firstOffset + 4)]);
        Assert.Equal(255, IconArt.AppIcon(256).AlphaAt(128, 128));
        Assert.Equal(255, IconArt.Sprite(32, badge: true).AlphaAt(25, 25));   // the badge's centre
    }

    private static PetEnv Env(Vec mouse = default) => new(new Box(0, 40, 1920, 1000), new Vec(100, 110), mouse, 0, []);

    [Fact]
    public void ThrownPetLandsOnTheGround()
    {
        var pet = new PetBrain(5) { Pos = new Vec(500, 900) };
        pet.Thrown(new Vec(400, 0));
        var env = Env(new Vec(-5000, -5000));
        for (var i = 0; i < 300; i++) pet.Step(1 / 30.0, env);
        Assert.Equal(pet.Ground(env), pet.Pos.Y, 3);
        Assert.NotEqual(PetBrain.State.Fly, pet.Current);
        Assert.InRange(pet.Pos.X, 0, 1920 - 100);
    }

    [Fact]
    public void FeetRestOnTheWorkAreaBottom()
    {
        // The window hangs below the work area by the canvas rows under the feet.
        var pet = new PetBrain(5);
        Assert.Equal(40 - (Sprite.CanvasH - Sprite.FeetY) * 5, pet.Ground(Env()));
    }

    [Fact]
    public void StandsUnderTheCardWhileAnAgentWaits()
    {
        var env = Env(new Vec(-5000, -5000));
        var pet = new PetBrain(5) { Holding = true };
        pet.Pos = new Vec(500, pet.Ground(env));
        for (var i = 0; i < 3; i++) pet.Step(1 / 30.0, env);
        Assert.Equal(PetBrain.State.Hold, pet.Current);
    }

    /// <summary>A trick picked from the menu, or the celebration for a finished task, used to be cut
    /// off on the next frame while another agent was waiting.</summary>
    [Fact]
    public void TrickPlaysOutWhileAnAgentWaits()
    {
        var env = Env(new Vec(-5000, -5000));
        var pet = new PetBrain(5) { Holding = true };
        pet.Pos = new Vec(500, pet.Ground(env));
        pet.Start(Activity.Celebrate);
        for (var i = 0; i < (int)((Activity.Celebrate.Duration() - 0.5) * 30); i++) pet.Step(1 / 30.0, env);
        Assert.Equal(PetBrain.State.Act, pet.Current);
        for (var i = 0; i < 45; i++) pet.Step(1 / 30.0, env);
        Assert.Equal(PetBrain.State.Hold, pet.Current);
    }

    [Fact]
    public void SnackIsChasedAndEaten()
    {
        var pet = new PetBrain(5) { Pos = new Vec(300, 30) };
        var snack = new Snack(new Vec(900, 600));
        var env = new PetEnv(new Box(0, 40, 1920, 1000), new Vec(100, 110), new Vec(-5000, -5000), 0, [snack]);
        for (var i = 0; i < 30 * 40 && !snack.Gone; i++)
        {
            snack.Step(1 / 30.0, env.Bounds);
            pet.Step(1 / 30.0, env);
        }
        Assert.True(snack.Gone);
        Assert.Equal(3, snack.Bites);
    }

    [Fact]
    public void WakesUpWhenAnAgentStartsWorking()
    {
        var env = Env(new Vec(-5000, -5000));
        var pet = new PetBrain(5);
        pet.Pos = new Vec(500, pet.Ground(env));
        pet.Sleep();
        for (var i = 0; i < 30; i++) pet.Step(1 / 30.0, env);
        pet.WorkActivities = [Activity.Build];
        Assert.Equal((PetBrain.State.Act, Activity.Stretch), (pet.Current, pet.CurrentActivity));
        for (var i = 0; i < (int)((Activity.Stretch.Duration() + 0.1) * 30); i++) pet.Step(1 / 30.0, env);
        Assert.Equal((PetBrain.State.Act, Activity.Build), (pet.Current, pet.CurrentActivity));
    }

    [Fact]
    public void RestsWithoutWorkTricks()
    {
        var pet = new PetBrain(5);
        for (var i = 0; i < 200; i++)
        {
            pet.PickNext();
            if (pet.Current == PetBrain.State.Act) Assert.Contains(pet.CurrentActivity, Activities.Resting);
        }
    }

    [Fact]
    public void OnlyMirrorsWorkWhileSeveralAgentsWork()
    {
        var pet = new PetBrain(5) { WorkActivities = [Activity.Build, Activity.Type] };
        for (var i = 0; i < 50; i++)
        {
            pet.PickNext();
            Assert.Equal(PetBrain.State.Act, pet.Current);
            Assert.Contains(pet.CurrentActivity, new[] { Activity.Juggle, Activity.Build, Activity.Type });
        }
    }
}

public class BridgeStartTests
{
    private sealed class Store(bool exists, OrcaPairing? pairing) : IPairingStore
    {
        public bool Exists() => exists;
        public OrcaPairing? Load() => pairing;
        public bool Save(OrcaPairing p) => true;
        public void Clear() { }
    }

    private static readonly OrcaPairing Pairing = new("wss://example.invalid", "token", "key");

    /// <summary>Each reason the bridge can't start has its own message: a missing script used to read
    /// "Can't find the Orca app".</summary>
    [Fact]
    public void SaysWhyItCantStart()
    {
        var script = Path.GetTempFileName();
        try
        {
            var missing = Path.Combine(Path.GetTempPath(), "clawd-no-bridge-" + Guid.NewGuid().ToString("N") + ".js");
            var noPairing = new OrcaBridge(new Store(true, null), () => null, script, null);
            Assert.Null(noPairing.Subscribe("term", _ => { }));
            Assert.Equal(Strings.Get("Bridge_NoPairing"), noPairing.LastError);
            var noScript = new OrcaBridge(new Store(true, Pairing), () => null, missing, null);
            Assert.Null(noScript.Subscribe("term", _ => { }));
            Assert.Equal(Strings.Get("Bridge_NoScript"), noScript.LastError);
            var noOrca = new OrcaBridge(new Store(true, Pairing), () => null, script, null);
            Assert.Null(noOrca.Subscribe("term", _ => { }));
            Assert.Equal(Strings.Get("Bridge_NoOrca"), noOrca.LastError);
        }
        finally
        {
            File.Delete(script);
        }
    }
}
