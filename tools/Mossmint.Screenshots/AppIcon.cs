using System.Buffers.Binary;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

// Renders the app icon from the geometry in docs/brand/app-icon.svg and packs
// a multi-size .ico (PNG entries, supported since Windows Vista).
internal static class AppIcon
{
    private const string Tile = "#111A17";
    private const string Mark = "#96CCA7";

    // Keep in sync with docs/brand/app-icon.svg: the mark at 80% on the tile.
    private const string MarkPath =
        "F0 M 8 2 A 6 6 0 1 1 8 14 A 6 6 0 1 1 8 2 Z M 7.2 12 V 9.6 C 5.44 9.6 4 8.16 4 6.4 " +
        "C 6.24 6.4 8 7.28 8 8.4 C 8 6.4 9.44 4.8 11.2 4.8 C 11.2 7.2 10.16 8.8 8.8 8.8 V 12 Z";

    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static IEnumerable<string> Write(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var pngs = Sizes.Select(size => (size, png: Render(size))).ToList();

        var largest = Path.Combine(outDir, "mossmint-256.png");
        File.WriteAllBytes(largest, pngs[^1].png);
        yield return largest;

        var ico = Path.Combine(outDir, "mossmint.ico");
        File.WriteAllBytes(ico, PackIco(pngs));
        yield return ico;
    }

    private static byte[] Render(int size)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(size / 16.0, size / 16.0)))
        {
            context.DrawRectangle(new SolidColorBrush(Color.Parse(Tile)), null, new RoundedRect(new Rect(0, 0, 16, 16), 3.2));
            context.DrawGeometry(new SolidColorBrush(Color.Parse(Mark)), null, Geometry.Parse(MarkPath));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
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
