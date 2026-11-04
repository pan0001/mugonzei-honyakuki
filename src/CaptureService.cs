using System.Drawing.Imaging;

namespace SteamVrTranslator;

internal static class CaptureService
{
    public static Bitmap CaptureDesktop()
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("没有可用的主显示器。");
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public static Bitmap Crop(Bitmap source, float x, float y, float width, float height)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(width) || !float.IsFinite(height) ||
            x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1.0001 || y + height > 1.0001)
            throw new ArgumentException("识别范围必须位于图像内，宽高必须大于 0。");
        var left = Math.Clamp((int)(source.Width * x), 0, source.Width - 1);
        var top = Math.Clamp((int)(source.Height * y), 0, source.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(source.Width * (x + width)), left + 1, source.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(source.Height * (y + height)), top + 1, source.Height);
        return source.Clone(Rectangle.FromLTRB(left, top, right, bottom), PixelFormat.Format32bppArgb);
    }
}
