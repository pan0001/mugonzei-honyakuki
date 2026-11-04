using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SteamVrTranslator;

/// <summary>Prepares a bounded screenshot for image-capable translation models.</summary>
public static class VisionImage
{
    public const int MaxBytes = 12 * 1024 * 1024;
    private const int DefaultMaxDimension = 2560;
    private const long DefaultMaxPixels = 4_000_000;
    private const int RecognitionMaxDimension = 4096;
    private const long RecognitionMaxPixels = 12_000_000;
    private const int MaxEncodingAttempts = 6;
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Prepare(Bitmap image)
        => PrepareCore(image, DefaultMaxDimension, DefaultMaxPixels, reduceForByteLimit: false);

    /// <summary>Preserves small handwriting strokes, reducing resolution only for bounded image limits.</summary>
    public static byte[] PrepareForRecognition(Bitmap image)
        => PrepareCore(image, RecognitionMaxDimension, RecognitionMaxPixels, reduceForByteLimit: true);

    private static byte[] PrepareCore(Bitmap image, int maxDimension, long maxPixels, bool reduceForByteLimit)
    {
        ArgumentNullException.ThrowIfNull(image);
        double scale = Math.Min(1, Math.Min(
            (double)maxDimension / Math.Max(image.Width, image.Height),
            Math.Sqrt((double)maxPixels / ((long)image.Width * image.Height))));
        int attempts = reduceForByteLimit ? MaxEncodingAttempts : 1;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            // Always sample the original image. Repeated resizing would blur fine strokes.
            // Floor dimensions so rounding never pushes the result over the pixel limit.
            int width = Math.Max(1, (int)Math.Floor(image.Width * scale));
            int height = Math.Max(1, (int)Math.Floor(image.Height * scale));
            using var encoded = Encode(image, width, height);
            if (encoded.Length <= MaxBytes)
            {
                byte[] bytes = encoded.ToArray();
                ValidatePng(bytes);
                return bytes;
            }
            if (!reduceForByteLimit || attempt == attempts - 1) break;
            // PNG size is content-dependent. Estimate a pixel-area reduction from actual
            // encoded bytes, with a small margin; the next encoding is checked again.
            scale *= Math.Sqrt((double)MaxBytes / encoded.Length) * 0.94;
        }
        throw TooLarge();
    }

    private static MemoryStream Encode(Bitmap image, int width, int height)
    {
        using var prepared = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(prepared))
        using (var attributes = new ImageAttributes())
        {
            graphics.Clear(Color.White);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            if (width == image.Width && height == image.Height)
                graphics.DrawImageUnscaled(image, 0, 0);
            else
                graphics.DrawImage(image, new Rectangle(0, 0, width, height),
                    0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }
        var encoded = new MemoryStream();
        try
        {
            prepared.Save(encoded, ImageFormat.Png);
            return encoded;
        }
        catch
        {
            encoded.Dispose();
            throw;
        }
    }

    public static void ValidatePng(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            throw new InvalidOperationException("截图数据为空，请重新截屏。");
        if (bytes.Length > MaxBytes) throw TooLarge();
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature)
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw InvalidPng();
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width == 0 || height == 0 || width > RecognitionMaxDimension || height > RecognitionMaxDimension
            || (ulong)width * height > RecognitionMaxPixels)
            throw new InvalidOperationException("截图尺寸无效或过大，请重新截屏并缩小识别范围。");

        // Walk chunk boundaries without decoding untrusted dimensions into a bitmap.
        // A one-pixel image is valid; only structural completeness is required here.
        bool hasImageData = false;
        int offset = 8;
        while (offset <= bytes.Length - 12)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (chunkLength > (uint)(bytes.Length - offset - 12)) throw InvalidPng();
            ReadOnlySpan<byte> kind = bytes.AsSpan(offset + 4, 4);
            if (offset != 8 && kind.SequenceEqual("IHDR"u8)) throw InvalidPng();
            if (kind.SequenceEqual("IDAT"u8) && chunkLength > 0) hasImageData = true;
            if (kind.SequenceEqual("IEND"u8))
            {
                if (chunkLength != 0 || !hasImageData) throw InvalidPng();
                return;
            }
            offset += checked((int)chunkLength + 12);
        }
        throw InvalidPng();
    }

    private static InvalidOperationException TooLarge() => new("截图文件超过 12 MB，请缩小识别范围后重试。");
    private static InvalidOperationException InvalidPng() => new("截图不是完整、有效的 PNG 图像，请重新截屏。");
}
