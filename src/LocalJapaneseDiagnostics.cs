using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace SteamVrTranslator;

internal static class LocalJapaneseDiagnostics
{
    // Only runs when explicitly requested. This does not capture the user's desktop or VR view.
    public static async Task RunAsync(string? imagePath, bool translate)
    {
        var report = new Dictionary<string, object?>
        {
            ["success"] = false,
            ["createdAtUtc"] = DateTimeOffset.UtcNow,
            ["processingMode"] = "LocalJapanese",
            ["engine"] = "NDLOCR-Lite / CPU",
            ["translationBackend"] = translate ? "Codex" : "None",
            ["accuracyDisclaimer"] = "单张样例用于验证本机流程，不代表真实 VR 手写识别准确率。"
        };
        Directory.CreateDirectory(SettingsStore.DirectoryPath);
        var total = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                throw new ArgumentException("本地 OCR 测试需要 --image 后提供一张本机图片路径。");
            report["imagePath"] = Path.GetFullPath(imagePath);
            using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(LocalJapaneseOcrService.RuntimeDirectory, "runtime-manifest.json"))))
            {
                report["engineVersion"] = manifest.RootElement.GetProperty("version").GetString();
                report["engineCommit"] = manifest.RootElement.GetProperty("commit").GetString();
            }
            byte[] png;
            using (var image = new Bitmap(imagePath)) png = VisionImage.Prepare(image);
            using var service = new LocalJapaneseOcrService();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var timer = Stopwatch.StartNew();
            string text = await service.RecognizeAsync(png, timeout.Token);
            report["recognized"] = text;
            report["coldSeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 2);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("样例没有识别到文字。");

            byte[] blank;
            using (var image = new Bitmap(640, 400))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);
                blank = VisionImage.Prepare(image);
            }
            timer.Restart();
            string blankText = await service.RecognizeAsync(blank, timeout.Token);
            report["blankSeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 2);
            report["blankResult"] = blankText;
            if (!string.IsNullOrWhiteSpace(blankText)) throw new InvalidOperationException("空白图识别出了文字，需检查误识别或旧结果复用。");

            timer.Restart();
            string repeated = await service.RecognizeAsync(png, timeout.Token);
            report["warmSeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 2);
            report["repeatMatches"] = repeated == text;
            if (repeated != text) throw new InvalidOperationException("重复识别结果不一致，请检查模型输出。");

            string display = text;
            if (translate)
            {
                using var translator = new TranslationService();
                timer.Restart();
                display = await translator.TranslateAsync(text,
                    new AppSettings { ProcessingMode = "LocalJapanese", TranslationBackend = "Codex" }, timeout.Token);
                report["translation"] = display;
                report["translationSeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 2);
                if (string.IsNullOrWhiteSpace(display)) throw new InvalidOperationException("翻译结果为空。");
            }
            var pages = OverlayRenderer.RenderPages(display, title: translate ? "手写翻译" : "本地手写原文");
            try
            {
                string path = Path.Combine(SettingsStore.DirectoryPath, "local-japanese-smoke-preview.png");
                pages[0].Save(path, ImageFormat.Png);
                report["previewPath"] = path;
                report["previewPages"] = pages.Count;
            }
            finally { foreach (var page in pages) page.Dispose(); }
            report["success"] = true;
        }
        catch (Exception ex)
        {
            report["error"] = ex.Message;
            Environment.ExitCode = 1;
        }
        report["totalSeconds"] = Math.Round(total.Elapsed.TotalSeconds, 2);
        File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "local-japanese-diagnostics.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
