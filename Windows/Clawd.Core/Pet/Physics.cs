namespace Clawd.Core.Pet;

public static class Physics
{
    /// <summary>DIPs per second squared.</summary>
    public const double Gravity = 1800;
}

/// <summary>Turns the last few drag positions into a throw velocity.</summary>
public sealed class DragTracker
{
    private readonly List<(double T, Vec P)> _samples = [];

    public void Add(Vec p)
    {
        var now = Clock.Uptime;
        _samples.Add((now, p));
        _samples.RemoveAll(s => now - s.T > 0.1);
    }

    public Vec Velocity()
    {
        if (_samples.Count == 0) return Vec.Zero;
        var (ta, a) = _samples[0];
        var (tb, b) = _samples[^1];
        if (tb - ta <= 0.01 || Clock.Uptime - tb >= 0.08) return Vec.Zero;
        var dt = tb - ta;
        return new Vec((b.X - a.X) / dt, (b.Y - a.Y) / dt);
    }
}

/// <summary>A cookie dropped from the top of the screen for Clawd to chase and eat.</summary>
public sealed class Snack
{
    public const double Scale = 5;
    public const double Width = 8 * Scale, Height = 7 * Scale;
    public static readonly string[] Art = [".####.", "##o###", "###o##", "#o####", ".####."];

    public Vec Pos;          // window origin (bottom-left), world DIPs
    public Vec Vel;
    public bool Held;
    public bool Grounded;
    public int Bites;
    public bool Gone;

    public Snack(Vec pos) => Pos = pos;

    public double CenterX => Pos.X + Width / 2;

    public void Step(double dt, Box bounds)
    {
        if (Held) { Grounded = false; return; }
        var ground = bounds.MinY - Scale;
        if (Pos.Y > ground || Vel.Y > 0)
        {
            Vel.Y -= Physics.Gravity * dt;
            Pos.X = Math.Clamp(Pos.X + Vel.X * dt, bounds.MinX, Math.Max(bounds.MinX, bounds.MaxX - Width));
            Pos.Y += Vel.Y * dt;
        }
        Grounded = Pos.Y <= ground;
        if (Grounded) { Pos.Y = ground; Vel = Vec.Zero; }
    }

    public void Consume() => Gone = true;

    /// <summary>Draws the cookie into a y-down canvas of 8 x 7 units; bites come off the right side.</summary>
    public void Render(ICanvas ctx)
    {
        var visibleCols = 6 - Bites * 2;
        for (var ry = 0; ry < Art.Length; ry++)
            for (var rx = 0; rx < Art[ry].Length; rx++)
            {
                var ch = Art[ry][rx];
                if (ch == '.' || rx >= visibleCols) continue;
                ctx.SetFill(ch == 'o' ? Sprite.ChipColor : Sprite.CookieColor);
                ctx.FillRect(1 + rx, 1 + ry, 1, 1);
            }
    }
}
