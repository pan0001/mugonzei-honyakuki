using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace SteamVrTranslator;

/// <summary>Creates owned textures for a world-space selection. Callers dispose every returned bitmap.</summary>
internal static class SelectionOverlayRenderer
{
    internal const int LongestSide = 1400;
    internal const int MaxPages = 96;
    internal const int MaxTextCharacters = 64_000;
    internal const long MaxTextureBytes = 160L * 1024 * 1024;

    public static Bitmap RenderFrame(float aspectRatio, bool valid = true)
    {
        Size size = TextureSize(aspectRatio);
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            Configure(graphics);
            graphics.Clear(Color.Transparent);
            Color accent = valid ? Color.FromArgb(114, 224, 193) : Color.FromArgb(255, 191, 102);
            var bounds = new RectangleF(6, 6, size.Width - 13, size.Height - 13);
            using var border = new Pen(Color.FromArgb(145, accent), 2);
            using var corners = new Pen(Color.FromArgb(250, accent), 6) { LineJoin = LineJoin.Round };
            graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            float length = Math.Clamp(Math.Min(size.Width, size.Height) * .1f, 24, 70);
            foreach (var corner in new[]
            {
                new[] { new PointF(bounds.Left, bounds.Top + length), new PointF(bounds.Left, bounds.Top), new PointF(bounds.Left + length, bounds.Top) },
                new[] { new PointF(bounds.Right - length, bounds.Top), new PointF(bounds.Right, bounds.Top), new PointF(bounds.Right, bounds.Top + length) },
                new[] { new PointF(bounds.Left, bounds.Bottom - length), new PointF(bounds.Left, bounds.Bottom), new PointF(bounds.Left + length, bounds.Bottom) },
                new[] { new PointF(bounds.Right - length, bounds.Bottom), new PointF(bounds.Right, bounds.Bottom), new PointF(bounds.Right, bounds.Bottom - length) }
            }) graphics.DrawLines(corners, corner);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public static List<Bitmap> RenderPages(string text, float aspectRatio, string? source = null, string title = "框选翻译")
    {
        Layout layout = PlanPages(text, aspectRatio, source, title);
        // Plan and validate the entire result before allocating any full-sized textures.
        var output = new List<Bitmap>(layout.Pages.Count);
        try
        {
            for (int index = 0; index < layout.Pages.Count; index++) output.Add(RenderPage(layout, index, false));
            return output;
        }
        catch
        {
            foreach (var bitmap in output) bitmap.Dispose();
            throw;
        }
    }

    public static Bitmap RenderStatus(string text, float aspectRatio)
    {
        Layout layout = PlanPages(text, aspectRatio, title: "");
        if (layout.Pages.Count != 1)
            throw new InvalidOperationException("框选状态文字无法在一页内完整显示，请缩短提示文字。");
        return RenderPage(layout, 0, true);
    }

    internal sealed record TextPage(string Title, IReadOnlyList<string> Lines);
    internal sealed record Layout(Size Size, RectangleF Body, float FontSize, float DetailFontSize,
        float LineHeight, int LinesPerPage, int PageLimit, IReadOnlyList<TextPage> Pages);

    internal static Layout PlanPages(string? text, float aspectRatio, string? source = null, string title = "框选翻译")
    {
        Size size = TextureSize(aspectRatio);
        int pageLimit = Math.Min(MaxPages, (int)(MaxTextureBytes / (size.Width * (long)size.Height * 4)));
        if ((long)(text?.Length ?? 0) + (source?.Length ?? 0) > MaxTextCharacters) throw TextTooLong(pageLimit);
        float shortSide = Math.Min(size.Width, size.Height);
        float fontSize = Math.Clamp(shortSide * .12f, 32, 52);
        float detailFontSize = Math.Clamp(fontSize * .46f, 16, 22);
        float padding = Math.Clamp(shortSide * .06f, 18, 42);
        using var scratch = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(scratch);
        Configure(graphics);
        using var font = BodyFont(fontSize);
        using var detailFont = BodyFont(detailFontSize);
        using var format = TextFormat();
        float detailHeight = (float)Math.Ceiling(detailFont.GetHeight(graphics)) + 10;
        float lineHeight = (float)Math.Ceiling(font.GetHeight(graphics) + fontSize * .12f);
        // Extra horizontal space protects glyph overhang; footer/header never share the body area.
        var body = new RectangleF(padding + 4, padding + detailHeight,
            size.Width - 2 * padding - 8, size.Height - 2 * padding - 2 * detailHeight);
        int linesPerPage = Math.Max(1, (int)Math.Floor(body.Height / lineHeight));
        var pages = new List<TextPage>();

        void Add(string? value, string heading)
        {
            if (heading.Length > 128 || heading.Contains('\n') || heading.Contains('\r')
                || graphics.MeasureString(heading, detailFont, int.MaxValue, format).Width > body.Width)
                throw new InvalidOperationException("框选标题过长，无法完整显示，请缩短标题。");
            List<string> lines = Wrap(string.IsNullOrEmpty(value) ? "暂无文字" : value, graphics, font, format,
                body.Width, (pageLimit - pages.Count) * linesPerPage, pageLimit);
            for (int offset = 0; offset < lines.Count; offset += linesPerPage)
                pages.Add(new TextPage(heading, lines.GetRange(offset, Math.Min(linesPerPage, lines.Count - offset))));
        }

        Add(text, title ?? "");
        if (!string.IsNullOrEmpty(source)) Add(source, "原文");
        return new Layout(size, body, fontSize, detailFontSize, lineHeight, linesPerPage, pageLimit, pages);
    }

    internal static Size TextureSize(float aspectRatio)
    {
        if (!float.IsFinite(aspectRatio)) throw new ArgumentOutOfRangeException(nameof(aspectRatio), "选区长宽比必须是有限数值。");
        float ratio = Math.Clamp(aspectRatio, .2f, 5f);
        return ratio >= 1
            ? new Size(LongestSide, (int)Math.Round(LongestSide / (double)ratio))
            : new Size((int)Math.Round(LongestSide * (double)ratio), LongestSide);
    }

    private static List<string> Wrap(string text, Graphics graphics, Font font, StringFormat format,
        float width, int maxLines, int pageLimit)
    {
        var output = new List<string>();
        void Emit(string line)
        {
            if (output.Count >= maxLines) throw TextTooLong(pageLimit);
            output.Add(line);
        }
        foreach (string paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Split('\n'))
        {
            if (paragraph.Length == 0) { Emit(""); continue; }
            string line = "";
            var elements = StringInfo.GetTextElementEnumerator(paragraph);
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement();
                if (graphics.MeasureString(line + element, font, int.MaxValue, format).Width > width)
                {
                    int space = line.LastIndexOf(' ');
                    if (space > line.Length / 2 && space < line.Length - 1)
                    {
                        Emit(line[..(space + 1)]);
                        line = line[(space + 1)..];
                    }
                    if (graphics.MeasureString(line + element, font, int.MaxValue, format).Width > width && line.Length > 0)
                    {
                        Emit(line);
                        line = "";
                    }
                    if (graphics.MeasureString(line + element, font, int.MaxValue, format).Width > width)
                        throw new InvalidOperationException("原文包含无法完整排入选区的字符，请扩大选区或在桌面查看完整文字。");
                }
                line += element;
            }
            Emit(line);
        }
        return output;
    }

    private static Bitmap RenderPage(Layout layout, int index, bool centered)
    {
        var bitmap = new Bitmap(layout.Size.Width, layout.Size.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            Configure(graphics);
            graphics.Clear(Color.Transparent);
            using var shape = RoundedRectangle(new RectangleF(2, 2, bitmap.Width - 5, bitmap.Height - 5), 14);
            using var background = new SolidBrush(Color.FromArgb(174, 10, 16, 23));
            using var border = new Pen(Color.FromArgb(175, 114, 224, 193), 2);
            graphics.FillPath(background, shape);
            graphics.DrawPath(border, shape);
            using var ink = new SolidBrush(Color.FromArgb(255, 245, 247, 251));
            using var muted = new SolidBrush(Color.FromArgb(230, 183, 219, 218));
            using var font = BodyFont(layout.FontSize);
            using var detailFont = BodyFont(layout.DetailFontSize);
            using var format = TextFormat();
            TextPage page = layout.Pages[index];
            if (page.Title.Length > 0)
                graphics.DrawString(page.Title, detailFont, muted, layout.Body.Left, layout.Body.Top - detailFont.GetHeight(graphics) - 10, format);
            var state = graphics.Save();
            graphics.SetClip(layout.Body);
            float top = layout.Body.Top + (centered ? (layout.Body.Height - page.Lines.Count * layout.LineHeight) / 2 : 0);
            for (int line = 0; line < page.Lines.Count; line++)
                graphics.DrawString(page.Lines[line], font, ink, layout.Body.Left, top + line * layout.LineHeight, format);
            graphics.Restore(state);
            if (layout.Pages.Count > 1)
            {
                string label = $"{index + 1}/{layout.Pages.Count}";
                float width = graphics.MeasureString(label, detailFont, int.MaxValue, format).Width;
                graphics.DrawString(label, detailFont, muted, layout.Body.Right - width, layout.Body.Bottom + 9, format);
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static Font BodyFont(float size) => new("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
    private static StringFormat TextFormat() => new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
        Trimming = StringTrimming.None
    };
    private static void Configure(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }
    private static InvalidOperationException TextTooLong(int limit) => new(
        $"框选文字过多，此选区最多显示 {limit} 页（总文字上限 64,000 字符）。完整文字仍保留在桌面结果中，请缩小识别范围后重试。");
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
