using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SteamVrTranslator;

internal static class HandwritingBenchmark
{
    private sealed record Sample(string Name, string Image, string Reference, string Kind);
    public static async Task RunAsync(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath)) throw new ArgumentException("请使用 --manifest 指定基准测试清单。");
        string fullManifest = Path.GetFullPath(manifestPath);
        string directory = Path.GetDirectoryName(fullManifest)!;
        string reportPath = Path.Combine(directory, "benchmark-results.json");
        var cases = new List<Dictionary<string, object?>>();
        var report = new Dictionary<string, object?>
        {
            ["startedAtUtc"] = DateTimeOffset.UtcNow, ["success"] = false,
            ["referenceAnswersSentToModel"] = false, ["backend"] = "Codex default model",
            ["baseline"] = "Single image transcription, low reasoning",
            ["focused"] = "Automatic localization, enlarged detail views and high-reasoning transcription",
            ["characterScoring"] = "Unicode NFC, whitespace ignored; strict line/text matching is reported separately.",
            ["comparisonScope"] = "Whole strategy comparison, not an ablation isolating cropping from reasoning effort or extra model calls.",
            ["limitation"] = "Small development set. Derived stress images are not independent real-world handwriting samples.",
            ["cases"] = cases
        };
        void Save() => File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            if (new FileInfo(fullManifest).Length > 128 * 1024) throw new ArgumentException("测试清单超过上限。");
            var samples = JsonSerializer.Deserialize<List<Sample>>(File.ReadAllText(fullManifest),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("测试清单为空。");
            if (samples.Count is < 1 or > 12) throw new ArgumentException("每次基准测试需包含 1 到 12 张图片。");
            var client = new CodexTranslationClient();
            var settings = new AppSettings { TranslationBackend = "Codex", ProcessingMode = "Handwriting" };
            int baselineErrors = 0, focusedErrors = 0, referenceCharacters = 0;
            for (int index = 0; index < samples.Count; index++)
            {
                var sample = samples[index];
                if (sample.Reference.Length > 4000) throw new ArgumentException("单个参考原文过长。");
                string path = Path.GetFullPath(Path.Combine(directory, sample.Image));
                var row = new Dictionary<string, object?> { ["name"] = sample.Name, ["kind"] = sample.Kind,
                    ["imageFileName"] = Path.GetFileName(path), ["reference"] = sample.Reference, ["status"] = "preparing" };
                cases.Add(row); Save();
                byte[] png;
                using (var stream = File.OpenRead(path))
                {
                    if (stream.Length > 64 * 1024 * 1024) throw new ArgumentException("测试图片文件过大。");
                    row["imageSha256"] = Convert.ToHexString(SHA256.HashData(stream));
                    stream.Position = 0;
                    using var decoded = Image.FromStream(stream, false, true);
                    if ((long)decoded.Width * decoded.Height > 64_000_000) throw new ArgumentException("测试图片像素过多。");
                    using var bitmap = new Bitmap(decoded);
                    png = VisionImage.PrepareForRecognition(bitmap);
                }
                row["inputWidth"] = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
                row["inputHeight"] = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
                row["status"] = "baseline"; Save();
                var timer = Stopwatch.StartNew();
                // Only pixels and the normal task prompt enter either request. The reference
                // lives exclusively in this offline scorer, never in settings or client prompts.
                string baseline = await client.RecognizeHandwritingAsync(png, settings, CancellationToken.None);
                row["baselineSeconds"] = timer.Elapsed.TotalSeconds;
                row["baselineText"] = baseline;
                var baselineScore = OcrAccuracy.Score(sample.Reference, baseline);
                row["baselineScore"] = baselineScore;
                row["baselineStrictTextMatch"] = StrictMatch(sample.Reference, baseline);
                row["status"] = "localizing"; Save();
                timer.Restart();
                double exportSeconds = 0;
                string focused = await HandwritingRecognition.RecognizeAsync(png, settings,
                    async (overview, config, cancellation) =>
                    {
                        var stage = Stopwatch.StartNew();
                        string location = await client.LocateHandwritingAsync(overview, config, cancellation);
                        row["localizationSeconds"] = stage.Elapsed.TotalSeconds;
                        var export = Stopwatch.StartNew();
                        row["localization"] = location;
                        row["status"] = "reading-details"; Save();
                        exportSeconds += export.Elapsed.TotalSeconds;
                        return location;
                    },
                    async (images, description, config, cancellation) =>
                    {
                        var export = Stopwatch.StartNew();
                        row["imageViews"] = images.Select(bytes => new
                        {
                            width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)),
                            height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)), bytes = bytes.Length
                        }).ToArray();
                        row["viewDescription"] = description;
                        string viewsDirectory = Path.Combine(directory, "views-" + index);
                        Directory.CreateDirectory(viewsDirectory);
                        for (int imageIndex = 0; imageIndex < images.Count; imageIndex++)
                            File.WriteAllBytes(Path.Combine(viewsDirectory, imageIndex + ".png"), images[imageIndex]);
                        Save();
                        exportSeconds += export.Elapsed.TotalSeconds;
                        return await client.RecognizeHandwritingViewsAsync(images, description, config, cancellation);
                    }, CancellationToken.None);
                row["focusedSeconds"] = timer.Elapsed.TotalSeconds;
                row["focusedDiagnosticExportSeconds"] = exportSeconds;
                row["focusedProcessingAndModelSeconds"] = timer.Elapsed.TotalSeconds - exportSeconds;
                row["focusedText"] = focused;
                var focusedScore = OcrAccuracy.Score(sample.Reference, focused);
                row["focusedScore"] = focusedScore;
                row["focusedStrictTextMatch"] = StrictMatch(sample.Reference, focused);
                row["status"] = "complete";
                baselineErrors += baselineScore.Errors;
                focusedErrors += focusedScore.Errors;
                referenceCharacters += baselineScore.ReferenceCharacters;
                report["aggregate"] = new { referenceCharacters, baselineErrors, focusedErrors,
                    baselineCer = (double)baselineErrors / Math.Max(1, referenceCharacters),
                    focusedCer = (double)focusedErrors / Math.Max(1, referenceCharacters) };
                Save();
            }
            report["success"] = true;
        }
        catch (Exception error)
        {
            report["error"] = error.Message;
            Environment.ExitCode = 1;
        }
        finally { report["finishedAtUtc"] = DateTimeOffset.UtcNow; Save(); }
    }

    private static bool StrictMatch(string expected, string actual) =>
        expected.Replace("\r\n", "\n").Trim().Normalize().Equals(
            actual.Replace("\r\n", "\n").Trim().Normalize(), StringComparison.Ordinal);
}
