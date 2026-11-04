using System.Drawing.Imaging;
using System.Globalization;
using SteamVrTranslator;

public static class SelectionOverlayRendererTests
{
    public static void Run()
    {
        CheckFramesAndDimensions();
        CheckPaginationAndBounds();
        CheckLimits();
        SavePreviews();
        Console.WriteLine("PASS selection frame transparency, aspect bounds, lossless Unicode pagination and bounded texture allocation");
    }

    private static void CheckFramesAndDimensions()
    {
        foreach (float aspect in new[] { -10f, 0f, .2f, .5f, 1f, 2f, 5f, 100f })
        {
            using var frame = SelectionOverlayRenderer.RenderFrame(aspect);
            Check(frame.Width >= 280 && frame.Height >= 280 && Math.Max(frame.Width, frame.Height) == 1400,
                "The longest texture edge is 1400 and the shortest is bounded");
            Check((long)frame.Width * frame.Height <= 4_000_000, "Texture stays below four million pixels");
            Check(Math.Abs((double)frame.Width / frame.Height - Math.Clamp(aspect, .2f, 5f)) < .005,
                "Texture follows the bounded selection aspect ratio");
            Check(frame.GetPixel(frame.Width / 2, frame.Height / 2).A == 0
                && frame.GetPixel(frame.Width / 4, frame.Height / 4).A == 0,
                "Selection frame has a transparent, unfilled interior");
            Check(frame.GetPixel(0, 0).A == 0 && frame.GetPixel(frame.Width - 1, frame.Height - 1).A == 0,
                "Frame strokes stay inside the texture edges");
            Color edge = frame.GetPixel(frame.Width / 2, 6), corner = frame.GetPixel(20, 6);
            Check(edge.A > 0 && corner.A > edge.A, "Thin border and brighter corner markers are distinct");
        }
        using var valid = SelectionOverlayRenderer.RenderFrame(1);
        using var invalid = SelectionOverlayRenderer.RenderFrame(1, false);
        Check(valid.GetPixel(20, 6).G > valid.GetPixel(20, 6).R
            && invalid.GetPixel(20, 6).R > invalid.GetPixel(20, 6).G, "Valid mint and invalid amber frames differ");
        foreach (float invalidAspect in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Expect<ArgumentOutOfRangeException>(() => { using var unexpected = SelectionOverlayRenderer.RenderFrame(invalidAspect); });
    }

    private static void CheckPaginationAndBounds()
    {
        string paragraph = "这里有中文与日本語。 A_long_word_" + new string('W', 80) + " e\u0301 👩‍💻 👨‍👩‍👧‍👦 完整结尾。";
        string text = string.Join("\r\n", Enumerable.Repeat(paragraph, 8)) + "\n\n最终标记 END";
        string source = "Original source\n\n原文不应丢失。\t最後の文字。";
        foreach (float aspect in new[] { .2f, .5f, 1f, 2f, 5f })
        {
            var plan = SelectionOverlayRenderer.PlanPages(text, aspect, source);
            Check(plan.Pages.Count > 1, "Long translated/source content paginates at every supported orientation");
            var translatedLines = plan.Pages.Where(page => page.Title != "原文").SelectMany(page => page.Lines).ToList();
            var originalLines = plan.Pages.Where(page => page.Title == "原文").SelectMany(page => page.Lines).ToList();
            Check(string.Concat(translatedLines) == Flatten(text), "Pagination retains every translated text element");
            Check(string.Concat(originalLines) == Flatten(source), "Source text remains complete on its own labeled pages");
            Check(translatedLines.Contains("") && originalLines.Contains(""), "Explicit blank lines survive pagination");
            CheckTextElementBoundaries(translatedLines, Flatten(text));
            CheckTextElementBoundaries(originalLines, Flatten(source));
            Check(plan.Body.Left > 0 && plan.Body.Top > 0 && plan.Body.Right < plan.Size.Width && plan.Body.Bottom < plan.Size.Height,
                "Body has separate header/footer margins inside the bitmap");
            using var scratch = new Bitmap(1, 1);
            using var graphics = Graphics.FromImage(scratch);
            using var font = new Font("Microsoft YaHei UI", plan.FontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
                Trimming = StringTrimming.None
            };
            foreach (var page in plan.Pages)
            {
                Check(page.Lines.Count <= plan.LinesPerPage && page.Lines.Count * plan.LineHeight <= plan.Body.Height,
                    "No page puts a text line below the body bounds");
                foreach (string line in page.Lines)
                    Check(graphics.MeasureString(line, font, int.MaxValue, format).Width <= plan.Body.Width + .1f,
                        "Every line, including long Latin words and emoji, fits horizontally");
            }
            var bitmaps = SelectionOverlayRenderer.RenderPages(text, aspect, source);
            try
            {
                Check(bitmaps.Count == plan.Pages.Count, "Every planned page is rendered, including the last text segment");
                foreach (var bitmap in bitmaps)
                {
                    Check(bitmap.Size == plan.Size, "All pages retain the original selection's texture aspect");
                    Check(bitmap.GetPixel(0, 0).A == 0 && bitmap.GetPixel(10, bitmap.Height / 2).A is >= 170 and <= 178,
                        "Page has transparent corners and a semitransparent dark panel");
                }
            }
            finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
        }
    }

    private static void CheckLimits()
    {
        foreach (float aspect in new[] { .2f, 1f, 5f })
        {
            var plan = SelectionOverlayRenderer.PlanPages("正文", aspect);
            Check(plan.PageLimit <= SelectionOverlayRenderer.MaxPages
                && plan.PageLimit * (long)plan.Size.Width * plan.Size.Height * 4 <= SelectionOverlayRenderer.MaxTextureBytes,
                "Page cap respects both count and total texture memory");
            int maximumLines = plan.LinesPerPage * plan.PageLimit;
            var exact = SelectionOverlayRenderer.PlanPages(string.Join('\n', Enumerable.Repeat("A", maximumLines)), aspect);
            Check(exact.Pages.Count == plan.PageLimit, "Text exactly at the page limit remains available");
            ExpectTextLimit(() => SelectionOverlayRenderer.RenderPages(string.Join('\n', Enumerable.Repeat("A", maximumLines + 1)), aspect));
            ExpectTextLimit(() => SelectionOverlayRenderer.RenderPages("译文", aspect, string.Join('\n', Enumerable.Repeat("A", maximumLines))));
            using var status = SelectionOverlayRenderer.RenderStatus("正在识别文字…", aspect);
            Check(status.Size == plan.Size && status.GetPixel(0, 0).A == 0, "Status uses the same selection dimensions");
            Expect<InvalidOperationException>(() =>
            {
                using var unexpected = SelectionOverlayRenderer.RenderStatus(string.Join('\n', Enumerable.Repeat("A", plan.LinesPerPage + 1)), aspect);
            });
        }
        ExpectTextLimit(() => SelectionOverlayRenderer.RenderPages(new string('字', 64_001), 1));
        ExpectTextLimit(() => SelectionOverlayRenderer.RenderPages(new string('字', 32_001), 1, new string('字', 32_000)));
        Expect<InvalidOperationException>(() => SelectionOverlayRenderer.PlanPages("正文", .2f, title: new string('长', 128)));
    }

    private static void SavePreviews()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);
        using var frame = SelectionOverlayRenderer.RenderFrame(1.8f);
        using var invalid = SelectionOverlayRenderer.RenderFrame(1.8f, false);
        using var status = SelectionOverlayRenderer.RenderStatus("正在翻译选区内的文字…", 1.8f);
        frame.Save(Path.Combine(directory, "selection-frame-preview.png"), ImageFormat.Png);
        invalid.Save(Path.Combine(directory, "selection-invalid-preview.png"), ImageFormat.Png);
        status.Save(Path.Combine(directory, "selection-status-preview.png"), ImageFormat.Png);
        foreach (var sample in new[] { (Ratio: 1.8f, Name: "wide"), (Ratio: .2f, Name: "portrait"), (Ratio: 1f, Name: "square") })
        {
            var pages = SelectionOverlayRenderer.RenderPages(
                "欢迎来到这个世界。\n手柄拉出矩形后，译文显示在原来的选区中。\n按翻页键阅读下一页。\n日本語と English も表示できます。", sample.Ratio,
                "Welcome to this world.\n選択した範囲の文章を翻訳します。");
            try { pages[0].Save(Path.Combine(directory, "selection-" + sample.Name + "-preview.png"), ImageFormat.Png); }
            finally { foreach (var page in pages) page.Dispose(); }
        }
    }

    private static string Flatten(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Replace("\n", "");

    private static void CheckTextElementBoundaries(IEnumerable<string> lines, string original)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(original).ToHashSet();
        boundaries.Add(original.Length);
        int offset = 0;
        foreach (string line in lines)
        {
            Check(boundaries.Contains(offset), "A line never starts inside a grapheme cluster");
            offset += line.Length;
            Check(boundaries.Contains(offset), "A line never ends inside a grapheme cluster");
        }
    }

    private static void ExpectTextLimit(Func<List<Bitmap>> action)
    {
        try
        {
            var unexpected = action();
            foreach (var bitmap in unexpected) bitmap.Dispose();
        }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains("桌面", StringComparison.Ordinal) && error.Message.Contains("缩小", StringComparison.Ordinal),
                "Limits explain where full text remains and how to retry");
            return;
        }
        throw new Exception("Selection overlay accepted content beyond its resource bounds");
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("SelectionOverlayRendererTests: " + message);
    }
}
