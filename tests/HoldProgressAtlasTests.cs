using System.Drawing.Imaging;
using Valve.VR;

namespace SteamVrTranslator;

public static class HoldProgressAtlasTests
{
    public static void Run()
    {
        CheckProgressAndBounds();
        using var atlas = HoldProgressAtlas.Create();
        Bitmap texture = atlas.GetTexture();
        Check(ReferenceEquals(texture, atlas.GetTexture()), "Both stages reuse one stable bitmap without replacing its texture");
        Check(texture.Size == new Size(1536, 1536) && texture.PixelFormat == PixelFormat.Format32bppArgb,
            "Combined atlas is a bounded 1536-square RGBA texture");
        CheckTransparentMargins(texture);
        Check(PixelBytes(texture) == 9L * 1024 * 1024, "The single persistent pixel buffer occupies exactly nine MiB");
        CheckRingFrames(texture);
        SavePreviews(texture);
        atlas.Dispose();
        atlas.Dispose();
        bool rejected = false;
        try { atlas.GetTexture(); }
        catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "Disposed atlas cannot hand out an invalid borrowed bitmap");
        Console.WriteLine("PASS one hold atlas, 122 frames, RGBA transparency, stage/progress bounds and nine-MiB pixel budget");
    }

    private static void CheckProgressAndBounds()
    {
        Check(HoldProgressAtlas.Buckets == 60 && HoldProgressAtlas.TileSize == 128
            && HoldProgressAtlas.GridSize == 12 && HoldProgressAtlas.AtlasSize == 1536, "Combined atlas geometry contract");
        foreach (float progress in new[] { float.NaN, float.NegativeInfinity, -1f, 0f })
            Check(HoldProgressAtlas.FrameIndex(progress) == 0, "Invalid/negative progress selects the empty ring");
        foreach (float progress in new[] { 1f, 2f, float.PositiveInfinity })
            Check(HoldProgressAtlas.FrameIndex(progress) == 60, "Complete/overflow progress selects the full ring");
        Check(HoldProgressAtlas.FrameIndex(.25f) == 15 && HoldProgressAtlas.FrameIndex(.5f) == 30
            && HoldProgressAtlas.FrameIndex(.999f) == 60, "Progress selects the nearest bounded frame");
        for (int bucket = 0; bucket <= HoldProgressAtlas.Buckets; bucket++)
            Check(HoldProgressAtlas.FrameIndex(bucket / (float)HoldProgressAtlas.Buckets) == bucket,
                "Reusing a bucket as normalized progress never rounds down to its preceding frame");
        int previous = -1;
        for (int sample = 0; sample <= 1200; sample++)
        {
            int frame = HoldProgressAtlas.FrameIndex(sample / 1200f);
            Check(frame >= previous && frame is >= 0 and <= 60, "Progress-to-frame mapping is monotonic and bounded");
            previous = frame;
        }
        var occupiedTiles = new HashSet<(int Left, int Top)>();
        foreach (bool cancelStage in new[] { false, true })
        for (int bucket = 0; bucket <= HoldProgressAtlas.Buckets; bucket++)
        {
            VRTextureBounds_t bounds = HoldProgressAtlas.Bounds(bucket, cancelStage);
            int index = (cancelStage ? 61 : 0) + bucket;
            int left = index % 12 * 128, top = index / 12 * 128;
            Check(occupiedTiles.Add((left, top)), "The two stages have 122 distinct frame tiles");
            Check(bounds.uMin > 0 && bounds.vMin > 0 && bounds.uMax < 1 && bounds.vMax < 1,
                "Sampling bounds stay inside the atlas");
            Check(bounds.uMin < bounds.uMax && bounds.vMin < bounds.vMax, "Every frame has positive sampling area");
            Check(Math.Abs(bounds.uMin * 1536 - (left + .5f)) < .001f && Math.Abs(bounds.vMin * 1536 - (top + .5f)) < .001f
                && Math.Abs(bounds.uMax * 1536 - (left + 127.5f)) < .001f && Math.Abs(bounds.vMax * 1536 - (top + 127.5f)) < .001f,
                "Half-texel insets never select neighboring tile pixels");
        }
        Check(occupiedTiles.Count == 122, "Both complete 61-frame stages fit in a single atlas");
        foreach (bool cancelStage in new[] { false, true })
            Check(SameBounds(HoldProgressAtlas.Bounds(int.MinValue, cancelStage), HoldProgressAtlas.Bounds(0, cancelStage))
                && SameBounds(HoldProgressAtlas.Bounds(int.MaxValue, cancelStage), HoldProgressAtlas.Bounds(60, cancelStage)),
                "Out-of-range bucket indices clamp within their own stage");
    }

    private static void CheckTransparentMargins(Bitmap bitmap)
    {
        for (int tile = 0; tile < 144; tile++)
        {
            int left = tile % 12 * 128, top = tile / 12 * 128;
            for (int offset = 0; offset < 128; offset++)
                Check(bitmap.GetPixel(left + offset, top).A == 0 && bitmap.GetPixel(left + offset, top + 127).A == 0
                    && bitmap.GetPixel(left, top + offset).A == 0 && bitmap.GetPixel(left + 127, top + offset).A == 0,
                    "Every frame has transparent outer rows and columns, preventing visible seams");
            if (tile >= 122)
                for (int y = 1; y < 127; y++)
                    for (int x = 1; x < 127; x++)
                        Check(bitmap.GetPixel(left + x, top + y).A == 0, "Unused tiles remain entirely transparent");
        }
    }

    private static void CheckRingFrames(Bitmap texture)
    {
        Color greenTop = Pixel(texture, 60, false, 64, 11), amberTop = Pixel(texture, 60, true, 64, 11);
        Check(greenTop.A > 220 && greenTop.G > greenTop.R + 60 && greenTop.G > greenTop.B + 20,
            "Full translation frame has an opaque green progress arc");
        Check(amberTop.A > 220 && amberTop.R > amberTop.G + 30 && amberTop.G > amberTop.B + 50,
            "Full cancellation frame has an opaque amber progress arc");
        Check(Pixel(texture, 0, false, 64, 11).A < greenTop.A && Pixel(texture, 0, true, 64, 11).A < amberTop.A,
            "Both stages start with an inactive track at twelve o'clock");
        Color quarterRight = Pixel(texture, 15, false, 117, 64);
        Check(quarterRight.G > quarterRight.R + 60, "Quarter progress reaches three o'clock clockwise");
        Check(Pixel(texture, 15, false, 64, 117).ToArgb() == Pixel(texture, 0, false, 64, 117).ToArgb(),
            "Quarter progress does not advance to six o'clock");
        Check(Pixel(texture, 30, false, 64, 25).A is >= 145 and <= 155,
            "Downsampling retains semitransparent interior rather than making a black opaque tile");
        int empty = CountGreenArc(texture, 0), quarter = CountGreenArc(texture, 15), half = CountGreenArc(texture, 30), full = CountGreenArc(texture, 60);
        Check(empty == 0 && quarter > empty && half > quarter && full > half,
            "Empty, quarter, half and full frames contain increasing colored arc coverage");
    }

    private static Color Pixel(Bitmap bitmap, int bucket, bool cancelStage, int x, int y)
    {
        int index = (cancelStage ? 61 : 0) + bucket;
        return bitmap.GetPixel(index % 12 * 128 + x, index / 12 * 128 + y);
    }

    private static int CountGreenArc(Bitmap bitmap, int bucket)
    {
        int count = 0;
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                Color color = Pixel(bitmap, bucket, false, x, y);
                if (color.A > 100 && color.G > color.R + 60 && color.G > color.B + 20) count++;
            }
        return count;
    }

    private static long PixelBytes(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { return Math.Abs((long)data.Stride) * bitmap.Height; }
        finally { bitmap.UnlockBits(data); }
    }

    private static void SavePreviews(Bitmap texture)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);
        texture.Save(Path.Combine(directory, "hold-atlas-combined.png"), ImageFormat.Png);
        foreach (var sample in new[]
        {
            (Frame: 0, Name: "green-start"), (Frame: 15, Name: "green-quarter"),
            (Frame: 60, Name: "green-full"), (Frame: 61, Name: "amber-start"),
            (Frame: 91, Name: "amber-half"), (Frame: 121, Name: "amber-full")
        })
        {
            var rectangle = new Rectangle(sample.Frame % 12 * 128, sample.Frame / 12 * 128, 128, 128);
            using var tile = texture.Clone(rectangle, PixelFormat.Format32bppArgb);
            tile.Save(Path.Combine(directory, "hold-atlas-" + sample.Name + ".png"), ImageFormat.Png);
        }
    }

    private static bool SameBounds(VRTextureBounds_t a, VRTextureBounds_t b) =>
        a.uMin == b.uMin && a.vMin == b.vMin && a.uMax == b.uMax && a.vMax == b.vMax;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("HoldProgressAtlasTests: " + message);
    }
}
