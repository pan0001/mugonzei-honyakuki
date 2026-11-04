using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

internal sealed record HandwritingImageViews(IReadOnlyList<byte[]> Images, string Description, bool UsedFallback);

/// <summary>Builds bounded, overlapping detail views from the original screenshot, never from the locator's text.</summary>
internal static class HandwritingDetailImages
{
    internal const int MaxImages = 5;
    internal const int MaxTotalBytes = 24 * 1024 * 1024;
    private const int MaxLocatorCharacters = 32 * 1024;
    private const int MaxLocatorRegions = 64;
    private const int MaxDetailDimension = 2000;
    private const int EncodingAttempts = 5;

    public static HandwritingImageViews Create(Bitmap source, string localizationJson)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<Rectangle> regions = ParseRegions(source.Size, localizationJson);
        bool fallback = regions.Count == 0;
        if (fallback) regions = FallbackRegions(source.Size);

        var images = new List<byte[]>(MaxImages) { VisionImage.Prepare(source) };
        int bytesUsed = images[0].Length;
        var description = new StringBuilder(
            "All images show the same source screenshot. Image 1 is the full image. " +
            "Later images are enlarged overlapping details. Transcribe each source message once; " +
            "do not repeat text because it appears in multiple images.\n");
        description.Append(FormattableString.Invariant($"Source size: {source.Width} x {source.Height} pixels.\n"));
        for (int index = 0; index < regions.Count; index++)
        {
            Rectangle region = regions[index];
            // Reserve an equal share of the remaining allowance for every remaining view.
            // A detailed first crop cannot exhaust the budget and erase later handwriting.
            int allowance = (MaxTotalBytes - bytesUsed) / (regions.Count - index);
            byte[] detail = PrepareDetail(source, region, allowance);
            images.Add(detail);
            bytesUsed += detail.Length;
            description.Append(FormattableString.Invariant(
                $"Image {images.Count}: source pixels x={region.X} y={region.Y} width={region.Width} height={region.Height}.\n"));
        }
        return new HandwritingImageViews(images.AsReadOnly(), description.ToString(), fallback);
    }

    private static List<Rectangle> ParseRegions(Size imageSize, string? localizationJson)
    {
        var regions = new List<Rectangle>(MaxImages - 1);
        if (string.IsNullOrWhiteSpace(localizationJson) || localizationJson.Length > MaxLocatorCharacters) return regions;
        string json = localizationJson.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            int newline = json.IndexOf('\n');
            if (newline < 0 || !json.EndsWith("```", StringComparison.Ordinal)) return regions;
            string language = json[3..newline].Trim();
            if (language.Length != 0 && !language.Equals("json", StringComparison.OrdinalIgnoreCase)) return regions;
            json = json[(newline + 1)..^3].Trim();
        }
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 5 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return regions;
            var rootProperties = root.EnumerateObject().ToArray();
            if (rootProperties.Length != 1 || rootProperties[0].Name != "regions"
                || rootProperties[0].Value.ValueKind != JsonValueKind.Array) return regions;
            JsonElement entries = rootProperties[0].Value;
            if (entries.GetArrayLength() > MaxLocatorRegions) return regions;
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (!TryReadRegion(entry, imageSize, out Rectangle rectangle)) continue;
                if (regions.Any(previous => NearlySame(previous, rectangle))) continue;
                regions.Add(rectangle);
                if (regions.Count == MaxImages - 1) break;
            }
        }
        catch (JsonException) { regions.Clear(); }
        return regions;
    }

    private static bool TryReadRegion(JsonElement entry, Size imageSize, out Rectangle rectangle)
    {
        rectangle = default;
        if (entry.ValueKind != JsonValueKind.Object) return false;
        var properties = entry.EnumerateObject().ToArray();
        if (properties.Length != 4 || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 4)
            return false;
        if (!Number(entry, "x", 0, out double x) || !Number(entry, "y", 0, out double y)
            || !Number(entry, "width", 1, out double width) || !Number(entry, "height", 1, out double height)
            || x + width > 1000 || y + height > 1000) return false;
        int left = Math.Clamp((int)Math.Floor(x * imageSize.Width / 1000), 0, imageSize.Width - 1);
        int top = Math.Clamp((int)Math.Floor(y * imageSize.Height / 1000), 0, imageSize.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling((x + width) * imageSize.Width / 1000), left + 1, imageSize.Width);
        int bottom = Math.Clamp((int)Math.Ceiling((y + height) * imageSize.Height / 1000), top + 1, imageSize.Height);
        int padX = Math.Max(8, (int)Math.Ceiling((right - left) * .08));
        int padY = Math.Max(8, (int)Math.Ceiling((bottom - top) * .08));
        rectangle = Rectangle.FromLTRB(Math.Max(0, left - padX), Math.Max(0, top - padY),
            Math.Min(imageSize.Width, right + padX), Math.Min(imageSize.Height, bottom + padY));
        return true;
    }

    private static bool Number(JsonElement entry, string name, double minimum, out double value)
    {
        value = 0;
        return entry.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value) && double.IsFinite(value) && value >= minimum && value <= 1000;
    }

    private static bool NearlySame(Rectangle first, Rectangle second)
    {
        Rectangle overlap = Rectangle.Intersect(first, second);
        long shared = (long)overlap.Width * overlap.Height;
        long union = (long)first.Width * first.Height + (long)second.Width * second.Height - shared;
        return union > 0 && (double)shared / union >= .85;
    }

    private static List<Rectangle> FallbackRegions(Size size)
    {
        int leftEnd = Math.Clamp((int)Math.Ceiling(size.Width * .56), 1, size.Width);
        int rightStart = Math.Clamp((int)Math.Floor(size.Width * .44), 0, size.Width - 1);
        int topEnd = Math.Clamp((int)Math.Ceiling(size.Height * .56), 1, size.Height);
        int bottomStart = Math.Clamp((int)Math.Floor(size.Height * .44), 0, size.Height - 1);
        return new[]
        {
            Rectangle.FromLTRB(0, 0, leftEnd, topEnd),
            Rectangle.FromLTRB(rightStart, 0, size.Width, topEnd),
            Rectangle.FromLTRB(0, bottomStart, leftEnd, size.Height),
            Rectangle.FromLTRB(rightStart, bottomStart, size.Width, size.Height)
        }.Distinct().ToList();
    }

    private static byte[] PrepareDetail(Bitmap source, Rectangle region, int allowance)
    {
        double scale = Math.Min(3, (double)MaxDetailDimension / Math.Max(region.Width, region.Height));
        for (int attempt = 0; attempt < EncodingAttempts; attempt++)
        {
            // The last, small render fits even incompressible RGB data in its reserved share.
            if (attempt == EncodingAttempts - 1)
                scale = Math.Min(scale, 512.0 / Math.Max(region.Width, region.Height));
            int width = Math.Max(1, (int)Math.Floor(region.Width * scale));
            int height = Math.Max(1, (int)Math.Floor(region.Height * scale));
            using var crop = RenderCrop(source, region, width, height);
            byte[] bytes = VisionImage.Prepare(crop);
            if (bytes.Length <= allowance) return bytes;
            scale *= Math.Sqrt((double)allowance / bytes.Length) * .92;
        }
        throw new InvalidOperationException("手写细节图片超过大小限制，请缩小识别范围。");
    }

    private static Bitmap RenderCrop(Bitmap source, Rectangle region, int width, int height)
    {
        var output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(output);
            using var attributes = new ImageAttributes();
            graphics.Clear(Color.White);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(source, new Rectangle(0, 0, width, height), region.X, region.Y,
                region.Width, region.Height, GraphicsUnit.Pixel, attributes);
            return output;
        }
        catch { output.Dispose(); throw; }
    }
}
