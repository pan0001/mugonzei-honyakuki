using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SteamVrTranslator;

internal static class CaptureService
{
    public static Bitmap CaptureDesktop(IntPtr workbench, AppSettings settings)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("没有可用的主显示器。");
        var region = CropBounds(bounds.Size, settings.CropX, settings.CropY, settings.CropWidth, settings.CropHeight);
        region.Offset(bounds.Location);
        bool hideWorkbench = IsWindowVisible(workbench);
        if (hideWorkbench) SetWorkbenchVisibility(workbench, false);
        try
        {
            // Let the compositor present the hidden workbench and previous subtitle before sampling.
            Marshal.ThrowExceptionForHR(DwmFlush());
            var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(region.Location, Point.Empty, region.Size, CopyPixelOperation.SourceCopy);
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        finally
        {
            // Keep the original Z-order and foreground application, including when another
            // program covers the visible workbench. A normal Form.Show() may raise it.
            if (hideWorkbench) SetWorkbenchVisibility(workbench, true);
        }
    }

    private static void SetWorkbenchVisibility(IntPtr window, bool visible)
    {
        const uint keepPlacement = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0200; // NOSIZE | NOMOVE | NOZORDER | NOACTIVATE | NOOWNERZORDER
        if (!SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, keepPlacement | (visible ? 0x0040u : 0x0080u)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    public static Bitmap Crop(Bitmap source, float x, float y, float width, float height) =>
        source.Clone(CropBounds(source.Size, x, y, width, height), PixelFormat.Format32bppArgb);

    private static Rectangle CropBounds(Size source, float x, float y, float width, float height)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(width) || !float.IsFinite(height) ||
            x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1.0001 || y + height > 1.0001)
            throw new ArgumentException("识别范围必须位于图像内，宽高必须大于 0。");
        var left = Math.Clamp((int)(source.Width * x), 0, source.Width - 1);
        var top = Math.Clamp((int)(source.Height * y), 0, source.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(source.Width * (x + width)), left + 1, source.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(source.Height * (y + height)), top + 1, source.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }
}
