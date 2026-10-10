using System.Buffers.Binary;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

// Renders the app icon and packs a multi-size .ico (PNG entries, supported since
// Windows Vista). Two drawings, kept in sync with their SVG masters:
// docs/brand/app-icon.svg (rich, 1024 grid) and app-icon-small.svg (16 grid).
// A blur radius R here is feGaussianBlur stdDeviation 0.29R + 0.5 in the SVG.
internal static class AppIcon
{
    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    private static readonly int[] PreviewSizes = [16, 24, 32, 48, 64, 128, 256];

    // Taskbar and Explorer sizes get the simplified drawing; gradients and
    // shadows turn to mush below about 40px.
    private const int SmallMax = 32;

    // A continuous-corner tile at the macOS icon grid (824 of 1024).
    private const string Tile =
        "M 627.4 100 C 731.2 100 783.1 100 822.8 120.2 A 185.4 185.4 0 0 1 903.8 201.2 C 924 240.9 924 292.8 924 396.6 " +
        "L 924 627.4 C 924 731.2 924 783.1 903.8 822.8 A 185.4 185.4 0 0 1 822.8 903.8 C 783.1 924 731.2 924 627.4 924 " +
        "L 396.6 924 C 292.8 924 240.9 924 201.2 903.8 A 185.4 185.4 0 0 1 120.2 822.8 C 100 783.1 100 731.2 100 627.4 " +
        "L 100 396.6 C 100 292.8 100 240.9 120.2 201.2 A 185.4 185.4 0 0 1 201.2 120.2 C 240.9 100 292.8 100 396.6 100 Z";

    private const string Stem = "M 493 624 C 486 555.2 498 495 503 452 L 529 452 C 524 495 518 555.2 531 624 Q 512 637.3 493 624 Z";
    private const string BigLeaf = "M 522 458 C 444.9 345.3 528.9 208.4 748 198 C 738 390.2 614.1 492.4 522 458 Z";
    private const string BigLeafFold = "M 522 458 C 582.2 358.2 657.6 271.5 748 198 C 738 390.2 614.1 492.4 522 458 Z";
    private const string BigLeafVein = "M 526.8 440.2 C 567.1 375.5 626.5 305.4 695.6 243.7 C 629.6 308.1 573.3 380.9 534.6 447 Z";
    private const string SmallLeaf = "M 506 466 C 450.6 506.9 350.2 462.6 310 336 C 457.7 300.5 537.7 375.7 506 466 Z";
    private const string SmallLeafFold = "M 506 466 C 448.4 411 383.1 367.7 310 336 C 350.2 462.6 450.6 506.9 506 466 Z";
    private const string SmallLeafVein = "M 495.8 461 C 458.4 424.4 408.1 386.6 352.9 356.4 C 409.7 384.3 461.5 419.6 499.7 455.1 Z";
    private const string RimGlint = "M 304.3 520.4 A 221 221 0 0 1 436.4 388.3";
    private const string Reeding = "M 738.8 619.8 V 651.8 M 736.2 637.5 V 669.5 M 732.2 655 V 687 M 726.9 672.1 V 704.1 M 720.3 688.7 V 720.7 M 712.4 704.8 V 736.8 M 703.2 720.2 V 752.2 M 692.9 734.8 V 766.8 M 681.4 748.6 V 780.6 M 668.9 761.4 V 793.4 M 655.5 773.2 V 805.2 M 641.1 783.9 V 815.9 M 626 793.5 V 825.5 M 610.2 801.8 V 833.8 M 593.7 808.9 V 840.9 M 576.8 814.6 V 846.6 M 559.4 819 V 851 M 541.8 822 V 854 M 523.9 823.7 V 855.7 M 506 823.9 V 855.9 M 488.2 822.8 V 854.8 M 470.5 820.2 V 852.2 M 453 816.2 V 848.2 M 435.9 810.9 V 842.9 M 419.3 804.3 V 836.3 M 403.2 796.4 V 828.4 M 387.8 787.2 V 819.2 M 373.2 776.9 V 808.9 M 359.4 765.4 V 797.4 M 346.6 752.9 V 784.9 M 334.8 739.5 V 771.5 M 324.1 725.1 V 757.1 M 314.5 710 V 742 M 306.2 694.2 V 726.2 M 299.1 677.7 V 709.7 M 293.4 660.8 V 692.8 M 289 643.4 V 675.4 M 286 625.8 V 657.8";

    private const double CoinX = 512;
    private const double CoinY = 596;
    private const double CoinR = 228;
    private const double CoinEdge = 28;

    private const string MiniTile = "M 10.24 0 C 12.26 0 13.26 0 14.03 0.39 A 3.6 3.6 0 0 1 15.61 1.97 C 16 2.74 16 3.74 16 5.76 L 16 10.24 C 16 12.26 16 13.26 15.61 14.03 A 3.6 3.6 0 0 1 14.03 15.61 C 13.26 16 12.26 16 10.24 16 L 5.76 16 C 3.74 16 2.74 16 1.97 15.61 A 3.6 3.6 0 0 1 0.39 14.03 C 0 13.26 0 12.26 0 10.24 L 0 5.76 C 0 3.74 0 2.74 0.39 1.97 A 3.6 3.6 0 0 1 1.97 0.39 C 2.74 0 3.74 0 5.76 0 Z";
    private const string MiniStem = "M 7.5 6 H 8.5 V 11 H 7.5 Z";
    private const string MiniBigLeaf = "M 8.5 6.2 C 7.27 3.83 9.34 1.15 13.8 1.2 C 13.18 5.22 10.38 7.13 8.5 6.2 Z";
    private const string MiniBigLeafFold = "M 8.5 6.2 C 10.06 4.32 11.83 2.65 13.8 1.2 C 13.18 5.22 10.38 7.13 8.5 6.2 Z";
    private const string MiniSmallLeaf = "M 7.5 6.6 C 6.19 7.68 3.54 6.67 2.2 3.6 C 5.72 2.82 7.95 4.57 7.5 6.6 Z";
    private const string MiniSmallLeafFold = "M 7.5 6.6 C 5.83 5.43 4.07 4.43 2.2 3.6 C 3.54 6.67 6.19 7.68 7.5 6.6 Z";

    public static IEnumerable<string> Write(string assetsDir, string? brandDir)
    {
        Directory.CreateDirectory(assetsDir);
        var pngs = IcoSizes.Select(size => (size, png: Render(size))).ToList();

        var largest = Path.Combine(assetsDir, "mossmint-256.png");
        File.WriteAllBytes(largest, pngs[^1].png);
        yield return largest;

        var ico = Path.Combine(assetsDir, "mossmint.ico");
        File.WriteAllBytes(ico, PackIco(pngs));
        yield return ico;

        if (brandDir is null)
        {
            yield break;
        }

        Directory.CreateDirectory(brandDir);
        foreach (var size in new[] { 512, 1024 })
        {
            var file = Path.Combine(brandDir, $"app-icon-{size}.png");
            File.WriteAllBytes(file, Render(size));
            yield return file;
        }

        var preview = Path.Combine(brandDir, "app-icon-preview.png");
        File.WriteAllBytes(preview, Preview());
        yield return preview;
    }

    private static byte[] Render(int size) =>
        size <= SmallMax ? Draw(size, 16, DrawSmall) : Draw(size, 1024, DrawRich);

    private static byte[] Draw(int size, double grid, Action<DrawingContext> draw)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(size / grid, size / grid)))
        {
            draw(context);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static void DrawRich(DrawingContext dc)
    {
        var tile = Geometry.Parse(Tile);
        Shadow(dc, tile, 0, 10, 20, "#40000000");
        dc.DrawGeometry(Linear(512, 100, 512, 924, (0, "#2C5947"), (1, "#12231C")), null, tile);
        dc.DrawGeometry(Radial(300, 150, 720, (0, "#40A8DDB8"), (1, "#00A8DDB8")), null, tile);

        // A lit top edge, and a faint rim so the dark tile still separates from dark taskbars.
        using (dc.PushGeometryClip(tile))
        {
            dc.DrawGeometry(null, new Pen(Linear(512, 100, 512, 924, (0, "#4DFFFFFF"), (0.3, "#0FFFFFFF"), (1, "#1AFFFFFF")), 10), tile);
        }

        const double cx = CoinX, cy = CoinY, r = CoinR;
        Shadow(dc, Circle(cx, cy + CoinEdge + 16, r), 0, 0, 44, "#73000000");
        Shadow(dc, Circle(cx, cy + CoinEdge, r - 4), 0, 0, 10, "#59000000");

        // The coin lies on the tile, so its reeded edge shows below the face.
        var edge = Circle(cx, cy + CoinEdge, r);
        dc.DrawGeometry(Linear(0, cy, 0, cy + r + CoinEdge, (0, "#B08A45"), (1, "#5A4219")), null, edge);
        using (dc.PushGeometryClip(edge))
        {
            dc.DrawGeometry(null, new Pen(Solid("#4D000000"), 4), Geometry.Parse(Reeding));
        }

        dc.DrawGeometry(Diagonal((0, "#F8EBC2"), (0.5, "#DDBE7A"), (1, "#9A7535")), null, Circle(cx, cy, r));
        dc.DrawGeometry(null, new Pen(Solid("#B3FFFFFF"), 6, lineCap: PenLineCap.Round), Geometry.Parse(RimGlint));
        dc.DrawGeometry(Diagonal((0, "#8C6A2E"), (1, "#F3E0AA")), null, Circle(cx, cy, r * 0.84));
        dc.DrawGeometry(Diagonal((0, "#C2E6CE"), (1, "#78B391")), null, Circle(cx, cy, r * 0.8));
        dc.DrawGeometry(Radial(cx - 80, cy - 100, 240, (0, "#4DFFFFFF"), (1, "#00FFFFFF")), null, Circle(cx, cy, r * 0.8));
        dc.DrawGeometry(Diagonal((0.3, "#00FFFFFF"), (0.42, "#33FFFFFF"), (0.54, "#00FFFFFF")), null, Circle(cx, cy, r * 0.8));
        dc.DrawGeometry(null, new Pen(Solid("#80FFFFFF"), 3), Circle(cx + 2, cy + 2, r * 0.7));
        dc.DrawGeometry(null, new Pen(Solid("#4D2E6B4A"), 3), Circle(cx, cy, r * 0.7));
        dc.DrawGeometry(Radial(512, 628, 38, (0, "#B3143A27"), (0.7, "#80143A27"), (1, "#00143A27")), null, new EllipseGeometry(new Rect(474, 617, 76, 22)));

        var stem = Geometry.Parse(Stem);
        var big = Geometry.Parse(BigLeaf);
        var small = Geometry.Parse(SmallLeaf);
        Shadow(dc, new GeometryGroup { FillRule = FillRule.NonZero, Children = { stem, big, small } }, 14, 20, 18, "#66000000");

        dc.DrawGeometry(Linear(490, 0, 534, 0, (0, "#4A9A68"), (1, "#1D5A39")), null, stem);
        DrawLeaf(dc, small, SmallLeafFold, SmallLeafVein, Linear(330, 330, 500, 490, (0, "#A3E6AE"), (0.55, "#4DB06E"), (1, "#2E8752")));
        DrawLeaf(dc, big, BigLeafFold, BigLeafVein, Linear(480, 210, 740, 470, (0, "#A3E6AE"), (0.55, "#4DB06E"), (1, "#2E8752")));
    }

    // Light falls from the top left, so every coin bevel runs along that diagonal.
    private static LinearGradientBrush Diagonal(params (double Offset, string Color)[] stops) =>
        Linear(CoinX - CoinR, CoinY - CoinR, CoinX + CoinR, CoinY + CoinR, stops);

    private static void DrawLeaf(DrawingContext dc, Geometry body, string fold, string vein, IBrush fill)
    {
        dc.DrawGeometry(fill, null, body);
        dc.DrawGeometry(Solid("#590E3F26"), null, Geometry.Parse(fold));
        using (dc.PushGeometryClip(body))
        {
            var b = body.Bounds;
            dc.DrawGeometry(null, new Pen(Linear(b.Left, b.Top, b.Right, b.Bottom, (0, "#80FFFFFF"), (0.45, "#00FFFFFF")), 8), body);
        }

        dc.DrawGeometry(Solid("#B3F0FBF2"), null, Geometry.Parse(vein));
    }

    // Full-bleed tile, 1px gold rim, and a stem on whole pixels at 32px: small
    // Windows icons fill their canvas and need hard edges more than depth.
    private static void DrawSmall(DrawingContext dc)
    {
        dc.DrawGeometry(Linear(0, 0, 0, 16, (0, "#2C5947"), (1, "#13251E")), null, Geometry.Parse(MiniTile));
        dc.DrawGeometry(Solid("#7A5C26"), null, Circle(8, 10.75, 5));
        dc.DrawGeometry(Linear(3, 5, 13, 15, (0, "#F2DEA6"), (1, "#B8924A")), null, Circle(8, 10, 5));
        dc.DrawGeometry(Linear(4, 6, 12, 14, (0, "#C9EAD3"), (1, "#8DC5A1")), null, Circle(8, 10, 4));
        dc.DrawGeometry(Solid("#2A7449"), null, Geometry.Parse(MiniStem));
        dc.DrawGeometry(Solid("#5DBE7C"), null, Geometry.Parse(MiniSmallLeaf));
        dc.DrawGeometry(Solid("#590E3F26"), null, Geometry.Parse(MiniSmallLeafFold));
        dc.DrawGeometry(Solid("#74CF8F"), null, Geometry.Parse(MiniBigLeaf));
        dc.DrawGeometry(Solid("#590E3F26"), null, Geometry.Parse(MiniBigLeafFold));
    }

    private static byte[] Preview()
    {
        const int pad = 32;
        const int gap = 28;
        var width = (pad * 2) + PreviewSizes.Sum() + (gap * (PreviewSizes.Length - 1));
        const int strip = 256 + (pad * 2);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, strip * 2), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            foreach (var (background, top) in new[] { ("#1F1F1F", 0), ("#F3F3F3", strip) })
            {
                context.FillRectangle(Solid(background), new Rect(0, top, width, strip));
                var x = pad;
                foreach (var size in PreviewSizes)
                {
                    using var icon = new Bitmap(new MemoryStream(Render(size)));
                    context.DrawImage(icon, new Rect(x, top + ((strip - size) / 2), size, size));
                    x += size + gap;
                }
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static void Shadow(DrawingContext dc, Geometry geometry, double dx, double dy, double blur, string color)
    {
        var bounds = geometry.Bounds.Translate(new Vector(dx, dy));
        using (dc.PushEffect(new BlurEffect { Radius = blur }, bounds))
        using (dc.PushTransform(Matrix.CreateTranslation(dx, dy)))
        {
            dc.DrawGeometry(Solid(color), null, geometry);
        }
    }

    private static EllipseGeometry Circle(double cx, double cy, double r) => new(new Rect(cx - r, cy - r, r * 2, r * 2));

    private static SolidColorBrush Solid(string color) => new(Color.Parse(color));

    private static LinearGradientBrush Linear(double x1, double y1, double x2, double y2, params (double Offset, string Color)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(x1, y1, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(x2, y2, RelativeUnit.Absolute),
        };
        AddStops(brush, stops);
        return brush;
    }

    private static RadialGradientBrush Radial(double cx, double cy, double r, params (double Offset, string Color)[] stops)
    {
        var centre = new RelativePoint(cx, cy, RelativeUnit.Absolute);
        var brush = new RadialGradientBrush
        {
            Center = centre,
            GradientOrigin = centre,
            RadiusX = new RelativeScalar(r, RelativeUnit.Absolute),
            RadiusY = new RelativeScalar(r, RelativeUnit.Absolute),
        };
        AddStops(brush, stops);
        return brush;
    }

    private static void AddStops(GradientBrush brush, (double Offset, string Color)[] stops)
    {
        foreach (var (offset, color) in stops)
        {
            brush.GradientStops.Add(new GradientStop(Color.Parse(color), offset));
        }
    }

    private static byte[] PackIco(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        const int headerSize = 6;
        const int entrySize = 16;
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[headerSize];
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1); // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)images.Count);
        stream.Write(header);

        var offset = headerSize + (entrySize * images.Count);
        Span<byte> entry = stackalloc byte[entrySize];
        foreach (var (size, png) in images)
        {
            entry.Clear();
            entry[0] = (byte)(size >= 256 ? 0 : size); // 0 means 256
            entry[1] = (byte)(size >= 256 ? 0 : size);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1); // colour planes
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32); // bits per pixel
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)png.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);
            stream.Write(entry);
            offset += png.Length;
        }

        foreach (var (_, png) in images)
        {
            stream.Write(png);
        }

        return stream.ToArray();
    }
}
