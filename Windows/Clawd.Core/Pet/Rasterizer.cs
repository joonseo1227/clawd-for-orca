namespace Clawd.Core.Pet;

/// <summary>
/// A small software canvas for the sprite: filled rectangles under an affine transform, into a
/// premultiplied BGRA buffer (what UpdateLayeredWindow and WriteableBitmap take). Edges are
/// antialiased by exact area coverage, so a sprite drawn at a whole number of pixels per unit
/// comes out crisp, like nearest-neighbour scaling, while sub-pixel details (eyes looking
/// sideways, a squash) still move smoothly. Rotated rectangles are supersampled.
/// </summary>
public sealed class Rasterizer : ICanvas
{
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
        /// <summary>this ∘ m: m is applied first.</summary>
        public Matrix Then(Matrix m) => new(
            A * m.A + C * m.B, B * m.A + D * m.B,
            A * m.C + C * m.D, B * m.C + D * m.D,
            A * m.E + C * m.F + E, B * m.E + D * m.F + F);
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>Premultiplied BGRA, row by row from the top.</summary>
    public byte[] Pixels { get; }

    private Matrix _m = Matrix.Identity;
    private Rgba _fill = new(0, 0, 0);
    private readonly Stack<(Matrix, Rgba)> _saved = new();

    public Rasterizer(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public void Clear()
    {
        Array.Clear(Pixels);
        _m = Matrix.Identity;
        _saved.Clear();
    }

    /// <summary>Makes a rectangle of pixels transparent; the part outside the canvas is ignored.</summary>
    public void ClearRect(int x, int y, int width, int height)
    {
        int left = Math.Max(0, x), right = Math.Min(Width, x + width);
        int top = Math.Max(0, y), bottom = Math.Min(Height, y + height);
        if (left >= right) return;
        for (var row = top; row < bottom; row++) Array.Clear(Pixels, (row * Width + left) * 4, (right - left) * 4);
    }

    public void Save() => _saved.Push((_m, _fill));
    public void Restore() { if (_saved.Count > 0) (_m, _fill) = _saved.Pop(); }
    public void Translate(double x, double y) => _m = _m.Then(new Matrix(1, 0, 0, 1, x, y));
    public void Scale(double sx, double sy) => _m = _m.Then(new Matrix(sx, 0, 0, sy, 0, 0));
    public void Rotate(double radians)
    {
        var (s, c) = Math.SinCos(radians);
        _m = _m.Then(new Matrix(c, s, -s, c, 0, 0));
    }
    public void SetFill(Rgba color) => _fill = color;

    public void FillRect(double x, double y, double width, double height)
    {
        if (width <= 0 || height <= 0 || _fill.A <= 0) return;
        if (Math.Abs(_m.B) < 1e-9 && Math.Abs(_m.C) < 1e-9)
        {
            var (x0, y0) = _m.Apply(x, y);
            var (x1, y1) = _m.Apply(x + width, y + height);
            FillAligned(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
        }
        else
        {
            FillQuad(_m.Apply(x, y), _m.Apply(x + width, y), _m.Apply(x + width, y + height), _m.Apply(x, y + height));
        }
    }

    private void FillAligned(double x0, double y0, double x1, double y1)
    {
        var left = Math.Max(0, (int)Math.Floor(x0));
        var right = Math.Min(Width, (int)Math.Ceiling(x1));
        var top = Math.Max(0, (int)Math.Floor(y0));
        var bottom = Math.Min(Height, (int)Math.Ceiling(y1));
        for (var py = top; py < bottom; py++)
        {
            var cy = Math.Min(py + 1, y1) - Math.Max(py, y0);
            if (cy <= 0) continue;
            for (var px = left; px < right; px++)
            {
                var cx = Math.Min(px + 1, x1) - Math.Max(px, x0);
                if (cx > 0) Blend(px, py, cx * cy);
            }
        }
    }

    private void FillQuad((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3)
    {
        (double X, double Y)[] q = [p0, p1, p2, p3];
        var left = Math.Max(0, (int)Math.Floor(q.Min(p => p.X)));
        var right = Math.Min(Width, (int)Math.Ceiling(q.Max(p => p.X)));
        var top = Math.Max(0, (int)Math.Floor(q.Min(p => p.Y)));
        var bottom = Math.Min(Height, (int)Math.Ceiling(q.Max(p => p.Y)));
        // The corners keep the rectangle's winding through any rotation or flip, so a point is
        // inside when it is on the same side of all four edges.
        static double Edge((double X, double Y) a, (double X, double Y) b, double x, double y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
        const int n = 4;
        for (var py = top; py < bottom; py++)
            for (var px = left; px < right; px++)
            {
                var hits = 0;
                for (var sy = 0; sy < n; sy++)
                    for (var sx = 0; sx < n; sx++)
                    {
                        double x = px + (sx + 0.5) / n, y = py + (sy + 0.5) / n;
                        double e0 = Edge(q[0], q[1], x, y), e1 = Edge(q[1], q[2], x, y), e2 = Edge(q[2], q[3], x, y), e3 = Edge(q[3], q[0], x, y);
                        if ((e0 >= 0 && e1 >= 0 && e2 >= 0 && e3 >= 0) || (e0 <= 0 && e1 <= 0 && e2 <= 0 && e3 <= 0)) hits++;
                    }
                if (hits > 0) Blend(px, py, hits / (double)(n * n));
            }
    }

    /// <summary>Source-over with premultiplied alpha.</summary>
    private void Blend(int px, int py, double coverage)
    {
        var a = Math.Clamp(coverage, 0, 1) * _fill.A;
        if (a <= 0) return;
        var i = (py * Width + px) * 4;
        var keep = 1 - a;
        Pixels[i] = (byte)Math.Round(_fill.B * a + Pixels[i] * keep);
        Pixels[i + 1] = (byte)Math.Round(_fill.G * a + Pixels[i + 1] * keep);
        Pixels[i + 2] = (byte)Math.Round(_fill.R * a + Pixels[i + 2] * keep);
        Pixels[i + 3] = (byte)Math.Round(255 * a + Pixels[i + 3] * keep);
    }

    /// <summary>Straight-alpha RGBA, for PNG.</summary>
    public byte[] ToRgba()
    {
        var rgba = new byte[Pixels.Length];
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            var a = Pixels[i + 3];
            if (a == 0) continue;
            rgba[i] = (byte)Math.Min(255, Pixels[i + 2] * 255 / a);
            rgba[i + 1] = (byte)Math.Min(255, Pixels[i + 1] * 255 / a);
            rgba[i + 2] = (byte)Math.Min(255, Pixels[i] * 255 / a);
            rgba[i + 3] = a;
        }
        return rgba;
    }

    /// <summary>Alpha of one pixel, 0–255 (tests and hit testing).</summary>
    public byte AlphaAt(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height ? (byte)0 : Pixels[(y * Width + x) * 4 + 3];
}
