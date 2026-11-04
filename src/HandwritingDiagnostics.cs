using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

internal static class HandwritingDiagnostics
{
    public static async Task RunAsync()
    {
        var results = new Dictionary<string, object>
        {
            ["success"] = false,
            ["createdAtUtc"] = DateTimeOffset.UtcNow,
            ["backend"] = "Codex",
            ["processingMode"] = "Handwriting",
            ["syntheticInput"] = true,
            ["input"] = "Programmatically drawn polylines and Bezier strokes; no user screenshot or generated AI image",
            ["accuracyDisclaimer"] = "这是程序绘制的合成笔画测试，只验证识别与翻译链路，不代表真实笔迹准确率。",
            ["localOcrUsed"] = false,
            ["expectedMarkers"] = new[] { "Gate", "42", "入口 or ここ" }
        };
        var total = Stopwatch.StartNew();
        Directory.CreateDirectory(SettingsStore.DirectoryPath);
        string inputPath = Path.Combine(SettingsStore.DirectoryPath, "handwriting-smoke-input.png");
        string previewPath = Path.Combine(SettingsStore.DirectoryPath, "handwriting-smoke-preview.png");
        string reportPath = Path.Combine(SettingsStore.DirectoryPath, "handwriting-diagnostics.json");
        try
        {
            byte[] png;
            using (var sample = CreateFixture())
            {
                results["imageWidth"] = sample.Width;
                results["imageHeight"] = sample.Height;
                png = VisionImage.Prepare(sample);
            }
            File.WriteAllBytes(inputPath, png);
            results["inputPath"] = inputPath;
            results["imageBytes"] = png.Length;

            var settings = new AppSettings { TranslationBackend = "Codex", ProcessingMode = "Handwriting" };
            results["targetLanguage"] = settings.TargetLanguage;
            using var translator = new TranslationService();
            var stage = Stopwatch.StartNew();
            // Each Codex request owns its normal 90-second deadline. Expected markers are
            // diagnostics metadata only and are never included in the recognition request.
            string source = await translator.RecognizeHandwritingAsync(png, settings, CancellationToken.None);
            results["recognitionSeconds"] = Math.Round(stage.Elapsed.TotalSeconds, 2);
            results["recognized"] = source;
            if (string.IsNullOrWhiteSpace(source))
                throw new InvalidOperationException("手写测试没有识别到原文，尚未进行翻译。");

            string compact = string.Concat(source.Normalize(NormalizationForm.FormKC)
                .Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));
            bool gate = compact.Contains("Gate", StringComparison.OrdinalIgnoreCase);
            bool number = compact.Contains("42", StringComparison.Ordinal);
            bool chinese = compact.Contains("入口", StringComparison.Ordinal);
            bool japanese = compact.Contains("ここ", StringComparison.Ordinal);
            bool verified = gate && number && (chinese || japanese);
            results["recognitionChecks"] = new { englishGate = gate, number42 = number, chineseEntry = chinese, japaneseHere = japanese };
            results["imageTextVerified"] = verified;

            stage.Restart();
            string translation = await translator.TranslateAsync(source, settings, CancellationToken.None);
            results["translationSeconds"] = Math.Round(stage.Elapsed.TotalSeconds, 2);
            results["translation"] = translation;
            if (string.IsNullOrWhiteSpace(translation))
                throw new InvalidOperationException("手写原文已识别，但翻译返回空结果。");
            var pages = OverlayRenderer.RenderPages(translation, source, title: "手写翻译");
            try
            {
                pages[0].Save(previewPath, ImageFormat.Png);
                results["previewPath"] = previewPath;
                results["previewPages"] = pages.Count;
            }
            finally { foreach (var page in pages) page.Dispose(); }

            if (!verified)
                throw new InvalidOperationException("合成笔画识别缺少测试关键字：需要 Gate、42，以及入口或ここ。原文、译文和预览已保留供检查。");
            results["success"] = true;
        }
        catch (Exception ex)
        {
            results["success"] = false;
            results["error"] = ex.Message;
            Environment.ExitCode = 1;
        }
        results["seconds"] = Math.Round(total.Elapsed.TotalSeconds, 2);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    /// <summary>A deterministic stroke sample, not a real handwriting accuracy benchmark.</summary>
    internal static Bitmap CreateFixture()
    {
        var bitmap = new Bitmap(1200, 760, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(255, 254, 250));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var ink = new Pen(Color.FromArgb(26, 37, 57), 6.3f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            DrawGlyph(graphics, ink, 90, 57, 1.3f, 1, LetterH);
            DrawGlyph(graphics, ink, 198, 62, 1.3f, -1, LetterE);
            DrawGlyph(graphics, ink, 293, 56, 1.3f, 2, LetterL);
            DrawGlyph(graphics, ink, 352, 61, 1.3f, -1.5f, LetterL);
            DrawGlyph(graphics, ink, 412, 59, 1.3f, 1, LetterO);

            DrawGlyph(graphics, ink, 89, 278, 1.3f, -1, LetterG);
            DrawGlyph(graphics, ink, 221, 282, 1.3f, 1.5f, LetterA);
            DrawGlyph(graphics, ink, 321, 275, 1.3f, -1, LetterT);
            DrawGlyph(graphics, ink, 403, 281, 1.3f, 1, LetterE);
            DrawGlyph(graphics, ink, 556, 279, 1.3f, -1.5f, NumberFour);
            DrawGlyph(graphics, ink, 668, 282, 1.3f, 1, NumberTwo);

            DrawGlyph(graphics, ink, 86, 527, 1.1f, -1, ChineseEntry);
            DrawGlyph(graphics, ink, 264, 524, 1.1f, 1, ChineseMouth);
            DrawGlyph(graphics, ink, 501, 525, 1.1f, -1.5f, HiraganaKo);
            DrawGlyph(graphics, ink, 671, 528, 1.1f, 1.5f, HiraganaKo);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static void DrawGlyph(Graphics graphics, Pen pen, float x, float y, float scale, float rotation,
        Action<Graphics, Pen> draw)
    {
        var state = graphics.Save();
        try
        {
            graphics.TranslateTransform(x, y);
            graphics.RotateTransform(rotation);
            graphics.ScaleTransform(scale, scale);
            draw(graphics, pen);
        }
        finally { graphics.Restore(state); }
    }

    private static void Line(Graphics graphics, Pen pen, params PointF[] points) => graphics.DrawLines(pen, points);

    private static void Curve(Graphics graphics, Pen pen, params PointF[] points) => graphics.DrawBeziers(pen, points);

    private static void LetterH(Graphics g, Pen p)
    {
        Line(g, p, new(12, 7), new(5, 111));
        Line(g, p, new(75, 4), new(68, 112));
        Curve(g, p, new(9, 60), new(28, 57), new(51, 62), new(71, 57));
    }

    private static void LetterE(Graphics g, Pen p)
    {
        Curve(g, p, new(11, 69), new(25, 66), new(45, 62), new(61, 62),
            new(67, 31), new(19, 31), new(11, 68), new(5, 106), new(44, 117), new(66, 94));
    }

    private static void LetterL(Graphics g, Pen p)
    {
        Curve(g, p, new(25, 6), new(18, 41), new(12, 83), new(20, 105), new(23, 111), new(31, 113), new(37, 108));
    }

    private static void LetterO(Graphics g, Pen p)
    {
        Curve(g, p, new(41, 41), new(14, 34), new(2, 62), new(12, 89),
            new(24, 120), new(63, 104), new(65, 72), new(68, 48), new(55, 37), new(41, 41));
    }

    private static void LetterG(Graphics g, Pen p)
    {
        Curve(g, p, new(88, 22), new(57, -5), new(11, 12), new(10, 61),
            new(6, 110), new(58, 130), new(89, 93));
        Line(g, p, new(89, 93), new(89, 66), new(64, 67));
    }

    private static void LetterA(Graphics g, Pen p)
    {
        Curve(g, p, new(57, 51), new(41, 28), new(12, 43), new(11, 75),
            new(10, 109), new(41, 117), new(58, 78));
        Curve(g, p, new(60, 42), new(57, 66), new(54, 88), new(59, 108), new(62, 112), new(68, 111), new(73, 105));
    }

    private static void LetterT(Graphics g, Pen p)
    {
        Curve(g, p, new(37, 10), new(31, 42), new(23, 80), new(30, 104), new(34, 114), new(48, 112), new(57, 101));
        Curve(g, p, new(8, 47), new(25, 44), new(47, 47), new(61, 43));
    }

    private static void NumberFour(Graphics g, Pen p)
    {
        Line(g, p, new(46, 8), new(8, 72), new(73, 70));
        Line(g, p, new(61, 8), new(57, 112));
    }

    private static void NumberTwo(Graphics g, Pen p)
    {
        Curve(g, p, new(10, 28), new(23, -6), new(69, 2), new(69, 32),
            new(70, 54), new(27, 83), new(10, 108));
        Curve(g, p, new(10, 108), new(31, 109), new(53, 107), new(76, 109));
    }

    private static void ChineseEntry(Graphics g, Pen p)
    {
        Curve(g, p, new(82, 21), new(68, 58), new(51, 94), new(13, 120));
        Curve(g, p, new(48, 5), new(69, 10), new(82, 26), new(88, 48),
            new(101, 89), new(121, 110), new(142, 120));
    }

    private static void ChineseMouth(Graphics g, Pen p)
    {
        Line(g, p, new(12, 14), new(8, 119));
        Line(g, p, new(12, 14), new(116, 10), new(111, 119));
        Curve(g, p, new(8, 117), new(40, 115), new(80, 121), new(111, 119));
    }

    private static void HiraganaKo(Graphics g, Pen p)
    {
        Curve(g, p, new(12, 26), new(37, 17), new(73, 21), new(102, 26));
        Curve(g, p, new(18, 83), new(-2, 110), new(18, 125), new(52, 126),
            new(70, 127), new(90, 124), new(109, 117));
    }
}
