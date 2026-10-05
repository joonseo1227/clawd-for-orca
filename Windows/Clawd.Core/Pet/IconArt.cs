using System.Buffers.Binary;
using System.IO.Compression;

namespace Clawd.Core.Pet;

/// <summary>Clawd's icons, drawn from the same sprite as the pet: the app icon (.ico) and the
/// notification-area icon.</summary>
public static class IconArt
{
    /// <summary>Clawd standing, scaled to fill <paramref name="fill"/> of the width and centred. With
    /// <paramref name="badge"/>, an orange dot in the corner (bottom right): an agent needs you.</summary>
    public static Rasterizer Sprite(int size, double fill = 1.0, bool badge = false)
    {
        var r = new Rasterizer(size, size);
        DrawCentred(r, size, size * fill / ContentW, 0);
        if (badge)
        {
            // A dot in the bottom-right corner, where Windows puts overlay badges; it covers a leg
            // rather than an eye. Ringed so it reads on a light or dark taskbar.
            var d = Math.Max(5, size * 0.42);
            Disc(r, size - d / 2, size - d / 2, d / 2, new Rgba(255, 255, 255));
            Disc(r, size - d / 2, size - d / 2, d / 2 - Math.Max(1, d * 0.15), new Rgba(242, 140, 40));
        }
        return r;
    }

    // The standing sprite's ink: arms from x 1 to 17, body and legs from y 10 to 20 (canvas units).
    private const double ContentX = 1, ContentY = Pet.Sprite.SpriteY, ContentW = 16, ContentH = 10;

    /// <summary>Clawd standing, centred, at <paramref name="unit"/> pixels per sprite pixel; whole units
    /// (where they fit) keep every edge on the pixel grid. <paramref name="drop"/> lowers it by that many units.</summary>
    private static void DrawCentred(Rasterizer r, int size, double unit, double drop)
    {
        if (unit >= 1) unit = Math.Floor(unit);
        r.Save();
        r.Translate(Math.Round((size - ContentW * unit) / 2), Math.Round((size - ContentH * unit) / 2 + drop * unit));
        r.Scale(unit, unit);
        r.Translate(-ContentX, -ContentY);
        Pet.Sprite.Render(r, new Pose(), []);
        r.Restore();
    }

    /// <summary>A filled circle in device pixels, each pixel's coverage supersampled once (separate
    /// partial fills would blend to less than their sum and leave seams).</summary>
    private static void Disc(Rasterizer r, double cx, double cy, double radius, Rgba color)
    {
        const int n = 4;
        for (var y = (int)Math.Floor(cy - radius); y < Math.Ceiling(cy + radius); y++)
            for (var x = (int)Math.Floor(cx - radius); x < Math.Ceiling(cx + radius); x++)
            {
                var hits = 0;
                for (var sy = 0; sy < n; sy++)
                    for (var sx = 0; sx < n; sx++)
                    {
                        double dx = x + (sx + 0.5) / n - cx, dy = y + (sy + 0.5) / n - cy;
                        if (dx * dx + dy * dy <= radius * radius) hits++;
                    }
                if (hits == 0) continue;
                r.SetFill(color.WithAlpha(hits / (double)(n * n)));
                r.FillRect(x, y, 1, 1);
            }
    }

    /// <summary>The app icon at one size: the cream plate of the Mac icon with Clawd on it, or just
    /// Clawd below 32 px, where a plate would leave him too small to read.</summary>
    public static Rasterizer AppIcon(int size)
    {
        if (size < 32) return Sprite(size);
        var r = new Rasterizer(size, size);
        var inset = size / 16.0;
        var radius = size * 0.2;
        Rgba top = new(250, 246, 237), bottom = new(235, 228, 213);
        // The plate: a rounded square with a soft vertical gradient, edges antialiased by distance.
        for (var y = 0; y < size; y++)
        {
            var t = y / (double)(size - 1);
            var row = new Rgba((byte)(top.R + (bottom.R - top.R) * t), (byte)(top.G + (bottom.G - top.G) * t), (byte)(top.B + (bottom.B - top.B) * t));
            for (var x = 0; x < size; x++)
            {
                var cover = RoundedCoverage(x + 0.5, y + 0.5, inset, size - inset, radius);
                if (cover <= 0) continue;
                r.SetFill(row.WithAlpha(cover));
                r.FillRect(x, y, 1, 1);
            }
        }
        DrawCentred(r, size, Math.Floor(size * 0.62 / ContentW), 0.5);
        return r;
    }

    private static double RoundedCoverage(double x, double y, double min, double max, double radius)
    {
        var cx = Math.Clamp(x, min + radius, max - radius);
        var cy = Math.Clamp(y, min + radius, max - radius);
        var dist = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - radius;
        if (x < min || x > max || y < min || y > max) dist = Math.Max(dist, Math.Max(min - x, Math.Max(x - max, Math.Max(min - y, y - max))));
        return Math.Clamp(0.5 - dist, 0, 1);
    }

    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    /// <summary>A Windows .ico with PNG-compressed images at every size Explorer and the taskbar ask for.</summary>
    public static byte[] Ico(Func<int, Rasterizer> draw)
    {
        var images = IcoSizes.Select(s => (Size: s, Png: Png(draw(s)))).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
        return ms.ToArray();
    }

    public static byte[] Png(Rasterizer image)
    {
        var rgba = image.ToRgba();
        using var raw = new MemoryStream();
        for (var y = 0; y < image.Height; y++)
        {
            raw.WriteByte(0);   // no filter
            raw.Write(rgba, y * image.Width * 4, image.Width * 4);
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) raw.WriteTo(z);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8; header[9] = 6;   // 8-bit RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(typed));
        s.Write(len);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
