using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace SteamVrTranslator;

public static class OcrRenderTests
{
    public static void Run()
    {
        Check(OcrService.NormalizeLanguages("eng + jpn + eng") == "eng+jpn", "OCR language normalization");
        Check(OcrService.NormalizeText(" Hello\tworld\r\n\n日本語\f ") == "Hello world\n日本語", "OCR text normalization preserves CJK");
        try { OcrService.NormalizeLanguages("../../bad"); throw new Exception("Invalid OCR language was accepted"); }
        catch (ArgumentException) { }
        using var engine = new OcrService();
        using var input = new Bitmap(1300, 240, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(input))
        using (var font = new Font("Arial", 52, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString("Welcome to SteamVR", font, Brushes.Black, 45, 40);
            graphics.DrawString("Press the button to translate.", font, Brushes.Black, 45, 120);
        }
        string recognized = engine.Recognize(input, "eng");
        Check(recognized.Contains("Welcome", StringComparison.OrdinalIgnoreCase) && recognized.Contains("translate", StringComparison.OrdinalIgnoreCase), "Actual English OCR: " + recognized);
        using var sample = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(sample);
        using var body = new Font("Microsoft YaHei UI", 43, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };
        string raw = "这里有中文和日本語。 A long-word: " + new string('W', 180) + " 👩‍💻 e\u0301 👨‍👩‍👧‍👦 end.";
        var lines = OverlayRenderer.WrapText(raw, measure, body, format, 1504);
        Check(string.Concat(lines) == raw, "Wrapping retains every text element");
        foreach (var line in lines)
            Check(measure.MeasureString(line, body, int.MaxValue, format).Width <= 1504.5f, "Wrapped line fits overlay");
        string longText = string.Join('\n', Enumerable.Repeat(raw, 12));
        var pages = OverlayRenderer.RenderPages(longText, "Original source text");
        try
        {
            Check(pages.Count > 3, "Long translations paginate");
            Check(pages.All(bitmap => bitmap.Width == 1600 && bitmap.Height == 250), "Wide subtitle dimensions bounded");
            string artifacts = Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(artifacts);
            pages[0].Save(Path.Combine(artifacts, "overlay-preview.png"), ImageFormat.Png);
            input.Save(Path.Combine(artifacts, "ocr-fixture.png"), ImageFormat.Png);
        }
        finally { foreach (var bitmap in pages) bitmap.Dispose(); }
        ExpectRenderLimit(string.Join('\n', Enumerable.Repeat("一行文字", 1000)), null);
        ExpectRenderLimit("简短译文", string.Join('\n', Enumerable.Repeat("原文", 1000)));
        ExpectRenderLimit(new string('字', 64_001), null);
        using var status = OverlayRenderer.RenderStatus("正在识别文字，请稍候…");
        Check(status.Width == 1600 && status.Height == 250 && status.GetPixel(0, 0).A == 0, "Status subtitle renders with transparent corners");
        CheckSubtitleAndHoldRing();
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception("OcrRenderTests: " + message);
    }

    private static void ExpectRenderLimit(string translation, string? original)
    {
        try
        {
            var unexpected = OverlayRenderer.RenderPages(translation, original);
            foreach (var bitmap in unexpected) bitmap.Dispose();
            throw new Exception("OcrRenderTests: Oversized overlay content was accepted");
        }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("96 页", StringComparison.Ordinal) && ex.Message.Contains("桌面", StringComparison.Ordinal), "Overlay size limit has actionable localized error");
        }
    }

    private static void CheckSubtitleAndHoldRing()
    {
        string artifacts = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(artifacts);
        var subtitles = OverlayRenderer.RenderPages("欢迎来到这个世界。\n长按左手 X，第一圈满开始翻译。\n继续按到第二圈满，取消本次字幕。\n这一行应完整显示在第二页。");
        try
        {
            Check(subtitles.Count == 2, "At most three subtitle lines per page");
            Check((double)subtitles[0].Width / subtitles[0].Height >= 6, "Subtitle is a flat, wide bar");
            Check(subtitles[0].GetPixel(0, 0).A == 0 && subtitles[0].GetPixel(1599, 249).A == 0, "Rounded corners stay transparent");
            Check(subtitles[0].GetPixel(20, 125).A is >= 160 and <= 170, "Subtitle background is semitransparent");
            subtitles[0].Save(Path.Combine(artifacts, "subtitle-preview.png"), ImageFormat.Png);
            subtitles[1].Save(Path.Combine(artifacts, "subtitle-page-2.png"), ImageFormat.Png);
        }
        finally { foreach (var subtitle in subtitles) subtitle.Dispose(); }
        using var start = OverlayRenderer.RenderHoldProgress(0, false);
        using var quarter = OverlayRenderer.RenderHoldProgress(.25f, false);
        using var green = OverlayRenderer.RenderHoldProgress(.65f, false);
        using var amber = OverlayRenderer.RenderHoldProgress(.65f, true);
        using var complete = OverlayRenderer.RenderHoldProgress(1, false);
        using var clampedLow = OverlayRenderer.RenderHoldProgress(-20, false);
        using var clampedHigh = OverlayRenderer.RenderHoldProgress(20, false);
        using var nan = OverlayRenderer.RenderHoldProgress(float.NaN, false);
        Check(green.Size == new Size(256, 256), "Hold ring dimensions");
        Check(green.GetPixel(0, 0).A == 0 && green.GetPixel(255, 255).A == 0 && green.GetPixel(128, 0).A == 0, "Outside ring stays transparent");
        Color top = green.GetPixel(128, 22), cancelTop = amber.GetPixel(128, 22);
        Check(top.G > top.R && top.G > top.B, "First-stage ring is green");
        Check(cancelTop.R > cancelTop.G && cancelTop.G > cancelTop.B, "Second-stage ring is amber");
        Check(quarter.GetPixel(234, 128).G > quarter.GetPixel(234, 128).R, "Quarter progress advances clockwise to three o'clock");
        Check(quarter.GetPixel(128, 234).ToArgb() == start.GetPixel(128, 234).ToArgb(), "Quarter progress has not reached six o'clock");
        Check(BitmapHash(start) == BitmapHash(clampedLow) && BitmapHash(start) == BitmapHash(nan), "Negative/NaN progress clamps to empty");
        Check(BitmapHash(complete) == BitmapHash(clampedHigh), "Overflow progress clamps to full");
        Check(BitmapHash(green) != BitmapHash(amber), "Stage visuals are distinct");
        green.Save(Path.Combine(artifacts, "hold-translate.png"), ImageFormat.Png);
        amber.Save(Path.Combine(artifacts, "hold-cancel.png"), ImageFormat.Png);
    }

    private static string BitmapHash(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray()));
    }
}
