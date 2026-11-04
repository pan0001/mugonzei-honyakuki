using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text;

namespace SteamVrTranslator;

/// <summary>Creates owned bitmaps. Every text element is retained across pages, including long words.</summary>
public static class OverlayRenderer
{
    public const int Width = 1600;
    public const int Height = 250;
    private const float Left = 48;
    private const float BodyWidth = Width - Left * 2;
    private const float LineHeight = 55;
    private const int LinesPerPage = 3;
    private const int MaxPages = 96;
    private const int MaxTextCharacters = 64_000;

    public static List<Bitmap> RenderPages(string translatedText, string? originalText = null, string title = "翻译")
    {
        if ((long)(translatedText?.Length ?? 0) + (originalText?.Length ?? 0) > MaxTextCharacters)
            throw TextTooLong();
        using var scratch = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(scratch);
        using var font = BodyFont();
        using var format = TextFormat();
        var pages = new List<(string Title, List<string> Lines)>();
        AddPages(pages, title, translatedText, measure, font, format);
        if (!string.IsNullOrWhiteSpace(originalText)) AddPages(pages, "原文", originalText, measure, font, format);
        var output = new List<Bitmap>();
        try
        {
            for (int page = 0; page < pages.Count; page++)
                output.Add(RenderPage(pages[page].Title, pages[page].Lines, page + 1, pages.Count, font, format));
            return output;
        }
        catch
        {
            foreach (var bitmap in output) bitmap.Dispose();
            throw;
        }
    }

    public static Bitmap RenderStatus(string message)
    {
        using var scratch = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(scratch);
        using var font = BodyFont();
        using var format = TextFormat();
        // Status messages originate from the application and are deliberately short.
        return RenderPage("", WrapText(message, measure, font, format, BodyWidth).Take(LinesPerPage).ToList(), 1, 1, font, format);
    }

    private static Font BodyFont() => new("Microsoft YaHei UI", 43, FontStyle.Regular, GraphicsUnit.Pixel);

    private static StringFormat TextFormat() => new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
        Trimming = StringTrimming.None
    };

    private static void AddPages(List<(string Title, List<string> Lines)> pages, string title, string? text, Graphics graphics, Font font, StringFormat format)
    {
        var lines = WrapText(string.IsNullOrEmpty(text) ? "暂无文字" : text, graphics, font, format, BodyWidth);
        // Validate the complete count before allocating textures (96 pages use at most 154 MB).
        int required = (lines.Count + LinesPerPage - 1) / LinesPerPage;
        if (required > MaxPages - pages.Count) throw TextTooLong();
        for (int index = 0; index < lines.Count; index += LinesPerPage)
            pages.Add((title, lines.GetRange(index, Math.Min(LinesPerPage, lines.Count - index))));
    }

    private static InvalidOperationException TextTooLong() => new("文字过多，字幕最多显示 96 页。完整文字仍保留在桌面结果中，请缩小识别范围后重试。");

    /// <summary>Returns an owned, transparent ring. Progress starts at twelve o'clock and advances clockwise.</summary>
    public static Bitmap RenderHoldProgress(float progress, bool cancelStage)
    {
        progress = float.IsNaN(progress) ? 0 : Math.Clamp(progress, 0, 1);
        var bitmap = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(150, 10, 16, 23));
            graphics.FillEllipse(background, 29, 29, 198, 198);
            using var track = new Pen(Color.FromArgb(100, 197, 210, 221), 13);
            using var active = new Pen(cancelStage ? Color.FromArgb(255, 255, 198, 88) : Color.FromArgb(255, 83, 231, 176), 13)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            var circle = new RectangleF(22, 22, 212, 212);
            graphics.DrawEllipse(track, circle);
            if (progress > 0) graphics.DrawArc(active, circle, -90, progress * 360);
            using var ink = new SolidBrush(Color.FromArgb(255, 245, 247, 251));
            using var muted = new SolidBrush(Color.FromArgb(230, 205, 218, 229));
            using var labelFont = new Font("Microsoft YaHei UI", 32, FontStyle.Bold, GraphicsUnit.Pixel);
            using var hintFont = new Font("Microsoft YaHei UI", 20, FontStyle.Regular, GraphicsUnit.Pixel);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(cancelStage ? "取消" : "翻译", labelFont, ink, new RectangleF(32, 81, 192, 48), centered);
            graphics.DrawString("继续按住", hintFont, muted, new RectangleF(28, 131, 200, 32), centered);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    internal static List<string> WrapText(string text, Graphics graphics, Font font, StringFormat format, float maxWidth)
    {
        if (maxWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        var output = new List<string>();
        foreach (string paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Split('\n'))
        {
            if (paragraph.Length == 0) { output.Add(""); continue; }
            var current = new StringBuilder();
            var enumerator = StringInfo.GetTextElementEnumerator(paragraph);
            while (enumerator.MoveNext())
            {
                string element = enumerator.GetTextElement();
                string candidate = current + element;
                if (current.Length > 0 && graphics.MeasureString(candidate, font, int.MaxValue, format).Width > maxWidth)
                {
                    // Prefer a recent word boundary. Preserve its whitespace instead of silently dropping characters.
                    string line = current.ToString();
                    int space = line.LastIndexOf(' ');
                    if (space > line.Length / 2 && space < line.Length - 1)
                    {
                        output.Add(line[..(space + 1)]);
                        current.Clear().Append(line[(space + 1)..]);
                    }
                    else
                    {
                        output.Add(line);
                        current.Clear();
                    }
                }
                current.Append(element);
            }
            if (current.Length > 0) output.Add(current.ToString());
        }
        return output.Count > 0 ? output : [""];
    }

    private static Bitmap RenderPage(string title, IReadOnlyList<string> lines, int page, int total, Font bodyFont, StringFormat format)
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(165, 10, 16, 23));
            using var shape = RoundedRectangle(new RectangleF(0, 0, Width, Height), 28);
            graphics.FillPath(background, shape);
            using var ink = new SolidBrush(Color.FromArgb(255, 245, 247, 251));
            using var muted = new SolidBrush(Color.FromArgb(225, 205, 218, 229));
            using var detailFont = new Font("Microsoft YaHei UI", 19, FontStyle.Regular, GraphicsUnit.Pixel);
            float top = (Height - lines.Count * LineHeight) / 2;
            for (int line = 0; line < lines.Count; line++)
                graphics.DrawString(lines[line], bodyFont, ink, Left, top + line * LineHeight, format);
            // Only alternate content needs a small label; the default translation has no heading or footer.
            if (!string.IsNullOrWhiteSpace(title) && title != "翻译")
                graphics.DrawString(title, detailFont, muted, Left, 9, format);
            if (total > 1)
            {
                string pageLabel = $"{page}/{total}";
                float pageWidth = graphics.MeasureString(pageLabel, detailFont, int.MaxValue, format).Width;
                graphics.DrawString(pageLabel, detailFont, muted, Width - Left - pageWidth, Height - 31, format);
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        float diameter = radius * 2;
        var shape = new GraphicsPath();
        shape.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        shape.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        shape.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        shape.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        shape.CloseFigure();
        return shape;
    }
}
