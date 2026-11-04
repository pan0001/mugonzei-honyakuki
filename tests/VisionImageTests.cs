using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SteamVrTranslator;

public static class VisionImageTests
{
    public static void Run()
    {
        using (var small = new Bitmap(320, 180, PixelFormat.Format24bppRgb))
        {
            using (var graphics = Graphics.FromImage(small)) graphics.Clear(Color.CornflowerBlue);
            byte[] bytes = VisionImage.Prepare(small);
            VisionImage.ValidatePng(bytes);
            Check(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG signature");
            Check(bytes.Length <= VisionImage.MaxBytes, "Encoded bytes bounded");
            using var stream = new MemoryStream(bytes);
            using var decoded = new Bitmap(stream);
            Check(decoded.Width == 320 && decoded.Height == 180, "Small screenshots are not upscaled");
            Check(decoded.GetPixel(160, 90).ToArgb() == Color.CornflowerBlue.ToArgb(), "Opaque color preserved");
            Check(bytes[25] == 2, "Output PNG uses RGB color type");
        }

        CheckResize(4000, 1000, 2560, 640, "Longest-side limit");
        CheckResize(3000, 3000, 2000, 2000, "Pixel-count limit");
        CheckResize(3000, 2000, 2449, 1632, "Aspect ratio after pixel-count limit");
        CheckResize(3096, 3312, 1933, 2068, "Ordinary image mode retains its four-megapixel limit");

        CheckRecognitionPreservesFineStrokes();
        CheckRecognitionResize(6000, 1000, 4096, 682, "Recognition longest-side limit");
        CheckRecognitionResize(4000, 4000, 3464, 3464, "Recognition pixel-count limit");
        CheckRecognitionByteFallback();

        using (var transparent = new Bitmap(8, 4, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(transparent))
            {
                graphics.Clear(Color.Transparent);
                graphics.FillRectangle(Brushes.Red, 4, 0, 4, 4);
            }
            using var stream = new MemoryStream(VisionImage.Prepare(transparent));
            using var decoded = new Bitmap(stream);
            Check(decoded.GetPixel(1, 1).ToArgb() == Color.White.ToArgb(), "Transparency composited on white");
            Check(decoded.GetPixel(6, 1).ToArgb() == Color.Red.ToArgb(), "Opaque pixels survive alpha flattening");
            using var recognitionStream = new MemoryStream(VisionImage.PrepareForRecognition(transparent));
            using var recognition = new Bitmap(recognitionStream);
            Check(recognition.Size == transparent.Size, "Recognition does not upscale small screenshots");
            Check(recognition.GetPixel(1, 1).ToArgb() == Color.White.ToArgb(), "Recognition transparency is composited on white");
            Check(recognition.GetPixel(6, 1).ToArgb() == Color.Red.ToArgb(), "Recognition keeps opaque colors");
        }

        using (var pixel = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
        {
            pixel.SetPixel(0, 0, Color.Transparent);
            byte[] fixture = VisionImage.Prepare(pixel);
            VisionImage.ValidatePng(fixture);
            using var stream = new MemoryStream(fixture);
            using var decoded = new Bitmap(stream);
            Check(decoded.Size == new Size(1, 1), "Valid one-pixel PNG accepted");
            Check(decoded.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(), "One-pixel alpha compositing");
            byte[] invalidSignature = (byte[])fixture.Clone(); invalidSignature[0] = 0;
            Reject(invalidSignature, "PNG");
            byte[] invalidDimension = (byte[])fixture.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(invalidDimension.AsSpan(16, 4), 0);
            Reject(invalidDimension, "尺寸");
            BinaryPrimitives.WriteUInt32BigEndian(invalidDimension.AsSpan(16, 4), 5000);
            Reject(invalidDimension, "尺寸");
            BinaryPrimitives.WriteUInt32BigEndian(invalidDimension.AsSpan(16, 4), 4097);
            Reject(invalidDimension, "尺寸");
            BinaryPrimitives.WriteUInt32BigEndian(invalidDimension.AsSpan(16, 4), 4000);
            BinaryPrimitives.WriteUInt32BigEndian(invalidDimension.AsSpan(20, 4), 4000);
            Reject(invalidDimension, "尺寸");
            Reject(fixture[..^5], "PNG");
            Reject(fixture[..33], "PNG");
            byte[] invalidChunk = (byte[])fixture.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(invalidChunk.AsSpan(33, 4), uint.MaxValue);
            Reject(invalidChunk, "PNG");
        }
        Reject(null!, "为空");
        Reject([], "为空");
        Reject([1, 2, 3, 4], "PNG");
        Reject(new byte[VisionImage.MaxBytes + 1], "12 MB");
        VisionImage.ValidatePng(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXxQAAAAASUVORK5CYII="));
        bool nullSourceRejected = false;
        try { VisionImage.Prepare(null!); }
        catch (ArgumentNullException) { nullSourceRejected = true; }
        Check(nullSourceRejected, "Null source bitmap rejected");
        bool nullRecognitionSourceRejected = false;
        try { VisionImage.PrepareForRecognition(null!); }
        catch (ArgumentNullException) { nullRecognitionSourceRejected = true; }
        Check(nullRecognitionSourceRejected, "Null recognition bitmap rejected");
        Console.WriteLine("PASS screenshot PNG preparation, high-resolution fine strokes, byte fallback, resize/aspect, alpha, bounds and validation");
    }

    private static void CheckRecognitionPreservesFineStrokes()
    {
        using var source = new Bitmap(3096, 3312, PixelFormat.Format24bppRgb);
        Color background = Color.FromArgb(19, 23, 29);
        Color ink = Color.FromArgb(241, 117, 219);
        using (var graphics = Graphics.FromImage(source)) graphics.Clear(background);
        // One-pixel strokes, including alternating pixels and dots, must survive untouched.
        // This checks input detail rather than expecting a particular OCR transcription.
        for (int x = 0; x < 128; x++)
        {
            source.SetPixel(101 + x, 159 + x % 7, ink);
            if (x % 2 == 0) source.SetPixel(101 + x, 180, Color.White);
        }
        source.SetPixel(0, 0, Color.Lime);
        source.SetPixel(source.Width - 1, source.Height - 1, Color.Cyan);
        byte[] bytes = VisionImage.PrepareForRecognition(source);
        VisionImage.ValidatePng(bytes);
        Check(bytes.Length <= VisionImage.MaxBytes && bytes[25] == 2, "Full-eye recognition PNG is bounded RGB");
        using var stream = new MemoryStream(bytes);
        using var decoded = new Bitmap(stream);
        Check(decoded.Size == source.Size, "3096x3312 eye capture is preserved at native resolution for recognition");
        for (int y = 157; y <= 182; y++)
            for (int x = 99; x <= 230; x++)
                Check(decoded.GetPixel(x, y).ToArgb() == source.GetPixel(x, y).ToArgb(), "One-pixel handwriting strokes and their gaps are preserved exactly");
        Check(decoded.GetPixel(0, 0).ToArgb() == Color.Lime.ToArgb()
            && decoded.GetPixel(decoded.Width - 1, decoded.Height - 1).ToArgb() == Color.Cyan.ToArgb(), "Recognition retains full image boundaries");
    }

    private static void CheckRecognitionResize(int sourceWidth, int sourceHeight, int expectedWidth, int expectedHeight, string description)
    {
        using var source = new Bitmap(sourceWidth, sourceHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.White);
        byte[] bytes = VisionImage.PrepareForRecognition(source);
        VisionImage.ValidatePng(bytes);
        using var stream = new MemoryStream(bytes);
        using var decoded = new Bitmap(stream);
        Check(decoded.Size == new Size(expectedWidth, expectedHeight), description);
        Check(Math.Max(decoded.Width, decoded.Height) <= 4096 && (long)decoded.Width * decoded.Height <= 12_000_000, "Recognition dimensions bounded");
        Check(Math.Abs((double)decoded.Width / decoded.Height - (double)sourceWidth / sourceHeight) < .01, "Recognition preserves aspect ratio within integer rounding");
    }

    private static void CheckRecognitionByteFallback()
    {
        using var source = new Bitmap(3096, 3312, PixelFormat.Format24bppRgb);
        var data = source.LockBits(new Rectangle(Point.Empty, source.Size), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var random = new Random(749);
            byte[] row = new byte[source.Width * 3];
            for (int y = 0; y < source.Height; y++)
            {
                random.NextBytes(row);
                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
            }
        }
        finally { source.UnlockBits(data); }
        using (var original = new MemoryStream())
        {
            source.Save(original, ImageFormat.Png);
            Check(original.Length > VisionImage.MaxBytes, "Noise fixture actually exceeds the encoded-byte limit before fallback");
        }
        Color originalMarker = source.GetPixel(63, 51);
        byte[] bytes = VisionImage.PrepareForRecognition(source);
        VisionImage.ValidatePng(bytes);
        Check(bytes.Length <= VisionImage.MaxBytes, "Recognition fallback never returns oversized PNG bytes");
        using var stream = new MemoryStream(bytes);
        using var decoded = new Bitmap(stream);
        Check(decoded.Width < source.Width && decoded.Height < source.Height, "High-entropy image is reduced to meet encoded-byte limit");
        Check(Math.Abs((double)decoded.Width / decoded.Height - (double)source.Width / source.Height) < .002, "Byte fallback preserves the screenshot aspect ratio");
        Check(source.GetPixel(63, 51).ToArgb() == originalMarker.ToArgb(), "Byte fallback leaves original screenshot unmodified");
    }

    private static void CheckResize(int sourceWidth, int sourceHeight, int expectedWidth, int expectedHeight, string description)
    {
        using var source = new Bitmap(sourceWidth, sourceHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.White);
        byte[] bytes = VisionImage.Prepare(source);
        using var stream = new MemoryStream(bytes);
        using var decoded = new Bitmap(stream);
        Check(decoded.Width == expectedWidth && decoded.Height == expectedHeight, description);
        Check(Math.Max(decoded.Width, decoded.Height) <= 2560 && (long)decoded.Width * decoded.Height <= 4_000_000, "Prepared dimensions bounded");
        Check(Math.Abs((double)decoded.Width / decoded.Height - (double)sourceWidth / sourceHeight) < .002, "Aspect ratio preserved within integer rounding");
    }

    private static void Reject(byte[] bytes, string message)
    {
        try { VisionImage.ValidatePng(bytes); }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains(message, StringComparison.Ordinal), "Localized validation reason: " + message);
            return;
        }
        throw new Exception("VisionImageTests: Invalid image was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("VisionImageTests: " + message);
    }
}
