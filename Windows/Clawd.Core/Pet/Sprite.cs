namespace Clawd.Core.Pet;

// Pixel grid decoded from the Claude Code welcome logo:
//    ▐▛███▜▌
//   ▝▜█████▛▘
//     ▘▘ ▝▝
// Each quadrant block becomes one pixel, giving an 18 x 5 sprite. Terminal cells
// are twice as tall as wide, so every sprite pixel is drawn 1 wide x 2 tall.

public enum Eyes { Open, Closed, Happy, Wide, Dizzy }
public enum Legs { Stand, Step, Dangle, Tucked }
public enum PropKind { None, Balls, Laptop, Bubble, Hammer, Headphones, Broom, Mug }

/// <summary>Things Clawd can hold or wear during an activity. <see cref="Flag"/> is the bubble's bulb,
/// the hammer raised, or the mug raised.</summary>
public readonly record struct Prop(PropKind Kind, bool Flag = false)
{
    public static readonly Prop None = new(PropKind.None);
}

public struct Pose
{
    public Pose() { }
    public Legs Legs = Legs.Stand;
    public double ArmL = 2;      // claw row: 2 = rest, 1 = raised, 0.5 = way up
    public double ArmR = 2;
    public Eyes Eyes = Eyes.Open;
    public double Look = 0;      // -1 left ... 1 right
    public double EyeDY = 0;     // negative looks up
    public double Bob = 0;       // body offset in pixels, negative = up
    public double Squash = 1;    // vertical stretch around the feet
    public int Mouth = 0;        // 0 none, 1 chomp, 2 yawn
    public double Rotation = 0;  // radians, used while tumbling through the air
    public double Phase = 0;     // running clock for looping details
    public Prop Prop = Prop.None;
    public bool Attention = false;   // an Orca agent is waiting on the user

    public bool ArmsUp
    {
        readonly get => ArmL < 2 && ArmR < 2;
        set { ArmL = value ? 1 : 2; ArmR = ArmL; }
    }
}

public enum Glyph { Heart, Z, Bang, Note, Question, Crumb, Star, Steam, Dust, Bit }

public struct Effect
{
    public Glyph Glyph;
    public double X, Y;          // in pixel units, window space
    public double VX;
    public double Age;
    public double Life;
    public Rgba? Color;
    public double? Rise;

    public Effect(Glyph glyph, double x, double y, double vx = 0, double age = 0, double life = 1.6, Rgba? color = null, double? rise = null)
    {
        Glyph = glyph; X = x; Y = y; VX = vx; Age = age; Life = life; Color = color; Rise = rise;
    }
}

public static class Sprite
{
    public static Rgba Rgb(byte r, byte g, byte b) => new(r, g, b);

    public static readonly Rgba ClawdOrange = Rgb(215, 119, 87);
    public static readonly Rgba EyeColor = Rgb(20, 18, 18);
    public static readonly Rgba StarColor = Rgb(255, 214, 90);
    public static readonly Rgba CookieColor = Rgb(210, 150, 90);
    public static readonly Rgba ChipColor = Rgb(90, 52, 30);

    /// <summary>Canvas in pixel units. Sprite sits at the bottom, effects float above it.</summary>
    public const double CanvasW = 20, CanvasH = 22;
    public const double SpriteX = 1, SpriteY = 10;   // sprite row 0 lands here
    public const double PixelH = 2;
    public const double FeetY = SpriteY + 5 * PixelH;  // bottom of the legs

    public static string[] Rows(this Glyph g) => g switch
    {
        Glyph.Heart => [".#.#.", "#####", ".###.", "..#.."],
        Glyph.Z => ["###", "..#", ".#.", "#..", "###"],
        Glyph.Bang => ["#", "#", "#", ".", "#"],
        Glyph.Note => ["..##", "..#.", "..#.", "###.", "##.."],
        Glyph.Question => ["###", "..#", ".##", "...", ".#."],
        Glyph.Crumb => ["#"],
        Glyph.Star => [".#.", "###", ".#."],
        Glyph.Steam => ["#.", ".#", "#.", ".#"],
        Glyph.Dust => [".##.", "####", ".##."],
        _ => ["#", "#"],
    };

    public static Rgba Color(this Glyph g) => g switch
    {
        Glyph.Heart => Rgb(237, 92, 115),
        Glyph.Z => Rgb(217, 217, 230),
        Glyph.Bang or Glyph.Question => Rgb(245, 245, 245),
        Glyph.Note => Rgb(130, 190, 255),
        Glyph.Crumb => CookieColor,
        Glyph.Star => StarColor,
        Glyph.Steam => Rgb(200, 200, 205),
        Glyph.Dust => Rgb(170, 160, 150),
        _ => Rgb(120, 220, 140),
    };

    /// <summary>Upward drift in pixel units per second (negative falls).</summary>
    public static double Rise(this Glyph g) => g switch
    {
        Glyph.Heart or Glyph.Z => 3,
        Glyph.Note or Glyph.Steam or Glyph.Bit => 2.5,
        Glyph.Bang or Glyph.Question or Glyph.Dust => 1,
        Glyph.Star => 2,
        _ => -5,   // crumb
    };

    public static void DrawGlyph(ICanvas ctx, string[] rows, double x, double y, double s)
    {
        for (var ry = 0; ry < rows.Length; ry++)
            for (var rx = 0; rx < rows[ry].Length; rx++)
                if (rows[ry][rx] == '#') ctx.FillRect(x + rx * s, y + ry * s, s, s);
    }

    /// <summary>Draws into a y-down canvas where 1 unit = 1 sprite pixel.</summary>
    public static void Render(ICanvas ctx, Pose pose, IReadOnlyList<Effect> effects)
    {
        // Px: sprite-grid coordinates (rows are 2 units tall), moves with the body bob.
        void Px(double x, double y, double w = 1, double h = 1) =>
            ctx.FillRect(SpriteX + x, SpriteY + (y + pose.Bob) * PixelH, w, h * PixelH);
        // Bp / Ap: canvas units, with and without the body bob.
        void Bp(double x, double y, double w, double h) => ctx.FillRect(x, y + pose.Bob * PixelH, w, h);
        void Ap(double x, double y, double w, double h) => ctx.FillRect(x, y, w, h);

        ctx.Save();
        if (pose.Rotation != 0)
        {
            double cx = SpriteX + 9, cy = SpriteY + 2.5 * PixelH;
            ctx.Translate(cx, cy);
            ctx.Rotate(pose.Rotation);
            ctx.Translate(-cx, -cy);
        }
        if (pose.Squash != 1)
        {
            // Tucked legs mean the body itself rests on the ground.
            var anchor = pose.Legs == Legs.Tucked ? SpriteY + 4 * PixelH : FeetY;
            ctx.Translate(0, FeetY);
            ctx.Scale(1, pose.Squash);
            ctx.Translate(0, -anchor);
        }

        if (pose.Prop.Kind == PropKind.Headphones)
        {
            ctx.SetFill(Rgb(60, 60, 72));
            Bp(4, 9.2, 12, 0.6); Bp(3.4, 9.2, 0.6, 2.4); Bp(16, 9.2, 0.6, 2.4);
        }

        ctx.SetFill(ClawdOrange);
        Px(3, 0, 12, 4);                       // body
        Px(1, pose.ArmL, 2); Px(15, pose.ArmR, 2);

        // Legs are not bobbed with the body so feet stay planted.
        if (pose.Legs != Legs.Tucked)
        {
            double[] legXs = pose.Legs == Legs.Step ? [5, 7, 10, 12] : [4, 6, 11, 13];
            var legH = pose.Legs == Legs.Dangle ? 2.0 : 1.0;
            var lift = Math.Min(pose.Bob, 0);
            foreach (var x in legXs) ctx.FillRect(SpriteX + x, SpriteY + (4 + lift) * PixelH, 1, (legH - lift) * PixelH);
        }

        ctx.SetFill(EyeColor);
        var shift = Math.Clamp(pose.Look, -1, 1) * 0.3;
        var ey = 1 + pose.EyeDY;
        double[] eyeXs = [5, 12];
        for (var i = 0; i < 2; i++)
        {
            var ex = eyeXs[i];
            switch (pose.Eyes)
            {
                case Eyes.Open: Px(ex + shift, ey); break;
                case Eyes.Wide: Px(ex + shift - 0.15, ey - 0.15, 1.3, 1.3); break;
                case Eyes.Closed: Px(ex - 0.1, 1.6, 1.2, 0.3); break;
                case Eyes.Happy:   // ^ shape
                    Px(ex - 0.2, 1.4, 0.45, 0.3); Px(ex + 0.25, 1.1, 0.5, 0.3); Px(ex + 0.75, 1.4, 0.45, 0.3);
                    break;
                case Eyes.Dizzy:
                    var sign = i == 0 ? 1.0 : -1.0;
                    Px(ex + Math.Sin(pose.Phase * 18) * 0.3 * sign, 1 + Math.Cos(pose.Phase * 18) * 0.2 * sign);
                    break;
            }
        }
        switch (pose.Mouth)
        {
            case 1: Px(8, 2.4, 2, 0.5); break;
            case 2: Px(8.4, 2.1, 1.2, 0.9); break;
        }

        switch (pose.Prop.Kind)
        {
            case PropKind.Laptop:
                ctx.SetFill(Rgb(175, 180, 190)); Ap(6.5, 15, 7, 4.5);
                ctx.SetFill(Rgb(120, 125, 135)); Ap(5, 19.3, 10, 0.7);
                ctx.SetFill(Rgb(235, 238, 245)); Ap(9.6, 16.8, 0.8, 0.8);
                break;
            case PropKind.Hammer:
                var up = pose.Prop.Flag;
                ctx.SetFill(Rgb(150, 100, 60)); Ap(7, 16, 6, 4);
                ctx.SetFill(Rgb(110, 70, 40)); Ap(7, 17.8, 6, 0.4); Ap(9.8, 16, 0.4, 4);
                ctx.SetFill(Rgb(140, 95, 55));
                if (up) Bp(17.2, 8.5, 0.6, 4.5); else Bp(13, 15.1, 3.5, 0.6);
                ctx.SetFill(Rgb(150, 155, 165));
                if (up) Bp(16, 7.3, 3, 1.4); else Bp(11.6, 14.8, 1.4, 1.2);
                break;
            case PropKind.Headphones:
                ctx.SetFill(Rgb(70, 70, 85)); Bp(2.8, 11, 1.5, 2.6); Bp(15.7, 11, 1.5, 2.6);
                ctx.SetFill(Rgb(230, 90, 90)); Bp(3.2, 11.8, 0.7, 1); Bp(16.1, 11.8, 0.7, 1);
                break;
            case PropKind.Broom:
                var sway = Math.Sin(pose.Phase * 6);
                ctx.SetFill(Rgb(140, 95, 55));
                for (var k = 0; k < 5; k++)
                {
                    var f = k / 4.0;
                    Bp(17 - f * (2 - sway * 0.5), 12 + f * 5.5, 0.6, 1.6);
                }
                ctx.SetFill(Rgb(220, 180, 90)); Ap(13.5 + sway * 0.6, 18.3, 3.5, 1.7);
                break;
            case PropKind.Mug:
                var (mx, my) = pose.Prop.Flag ? (13.6, 14.2) : (16.3, 15.2);
                ctx.SetFill(Rgb(242, 240, 232)); Bp(mx, my, 1.8, 2); Bp(mx + 1.8, my + 0.5, 0.5, 1);
                ctx.SetFill(Rgb(110, 70, 40)); Bp(mx, my, 1.8, 0.4);
                break;
        }
        ctx.Restore();

        switch (pose.Prop.Kind)
        {
            case PropKind.Balls:
                // Each ball arcs from the left claw over the head to the right claw.
                Rgba[] colors = [Rgb(235, 90, 80), Rgb(90, 150, 240), Rgb(250, 200, 70)];
                for (var i = 0; i < 3; i++)
                {
                    var t = pose.Phase * 1.1 + i / 3.0;
                    t -= Math.Floor(t);
                    ctx.SetFill(colors[i]);
                    Ap(2.6 + 14 * t, 12.5 - 36 * t * (1 - t), 1.2, 1.2);
                }
                break;
            case PropKind.Bubble:
                ctx.SetFill(Rgb(245, 245, 245));
                Ap(14.6, 8.4, 0.8, 0.8); Ap(15.8, 6.6, 1.1, 1.1);
                Ap(13.5, 0.6, 5.6, 5.2); Ap(13, 1.1, 6.6, 4.2);
                if (pose.Prop.Flag)
                {
                    ctx.SetFill(StarColor);
                    DrawGlyph(ctx, [".###.", "#####", "#####", ".###."], 15.05, 1.3, 0.7);
                    ctx.SetFill(Rgb(150, 150, 160)); Ap(15.75, 4.1, 1.4, 0.7);
                }
                else
                {
                    ctx.SetFill(EyeColor);
                    var dots = (int)(pose.Phase * 2.5) % 4;
                    for (var d = 0; d < dots; d++) Ap(14.3 + d * 1.5, 2.8, 0.8, 0.8);
                }
                break;
        }

        if (pose.Attention)
        {
            ctx.SetFill(StarColor);
            var hop = Math.Sin(pose.Phase * 6) > 0 ? 0 : 0.6;
            DrawGlyph(ctx, Glyph.Bang.Rows(), 9.6, 3.5 + hop + pose.Bob * PixelH, 0.8);
        }

        if (pose.Eyes == Eyes.Dizzy)
        {
            ctx.SetFill(StarColor);
            for (var i = 0; i < 3; i++)
            {
                var a = pose.Phase * 5 + i * 2.094;
                double x = SpriteX + 9 + Math.Cos(a) * 6, y = SpriteY - 1.5 + Math.Sin(a) * 1.2;
                DrawGlyph(ctx, Glyph.Star.Rows(), x - 0.75, y - 0.75, 0.5);
            }
        }

        foreach (var e in effects)
        {
            var fade = Math.Clamp((e.Life - e.Age) / (e.Life * 0.4), 0, 1);
            var size = e.Glyph == Glyph.Z ? 0.45 + e.Age * 0.25 : e.Glyph == Glyph.Crumb ? 0.6 : 0.5;
            ctx.SetFill((e.Color ?? e.Glyph.Color()).WithAlpha(fade));
            DrawGlyph(ctx, e.Glyph.Rows(), e.X + e.VX * e.Age, e.Y - (e.Rise ?? e.Glyph.Rise()) * e.Age, size);
        }
    }
}
