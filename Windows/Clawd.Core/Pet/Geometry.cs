namespace Clawd.Core.Pet;

/// <summary>A point or vector in the pet's world: device-independent pixels with y pointing up,
/// so the physics reads as in the Mac app (gravity pulls toward smaller y).</summary>
public record struct Vec(double X, double Y)
{
    public static readonly Vec Zero = new(0, 0);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>A rectangle in the pet's world (y up): MinY is the bottom edge.</summary>
public readonly record struct Box(double MinX, double MinY, double Width, double Height)
{
    public double MaxX => MinX + Width;
    public double MaxY => MinY + Height;
    public double MidX => MinX + Width / 2;
    public bool Contains(Vec p) => p.X >= MinX && p.X < MaxX && p.Y >= MinY && p.Y < MaxY;
    public Box Inset(double dx, double dy) => new(MinX + dx, MinY + dy, Width - 2 * dx, Height - 2 * dy);
}

/// <summary>An sRGB colour with straight alpha.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, double A = 1)
{
    public Rgba WithAlpha(double a) => this with { A = a };
}

/// <summary>Where the sprite draws: a y-down surface where 1 unit is 1 sprite pixel. The app's
/// rasterizer implements it; anything else (a test recorder, an icon writer) can too.</summary>
public interface ICanvas
{
    void Save();
    void Restore();
    void Translate(double x, double y);
    void Scale(double sx, double sy);
    void Rotate(double radians);
    void SetFill(Rgba color);
    void FillRect(double x, double y, double width, double height);
}
