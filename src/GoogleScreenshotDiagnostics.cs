using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;

namespace SteamVrTranslator;

internal static class GoogleScreenshotDiagnostics
{
    public static async Task RunAsync(string? imagePath, bool ocrOnly = false)
    {
        var total = Stopwatch.StartNew();
        var stages = new Dictionary<string, Dictionary<string, object?>>();
        foreach (string name in new[] { "input", "recognition", "sourcePreview", "translation", "translationPreview" })
            stages[name] = new() { ["status"] = "not_started", ["seconds"] = 0d };
        var report = new Dictionary<string, object?>
        {
            ["success"] = false,
            ["accuracyDisclaimer"] = "success 仅表示所选诊断步骤执行完成，不代表 OCR 文字正确；需与原图核对。ocrOnly 为 true 时未验证翻译。",
            ["createdAtUtc"] = DateTimeOffset.UtcNow,
            ["recognitionBackend"] = "NDLOCR-Lite CPU",
            ["translationBackend"] = "Google Translate background webpage (WebView2, no API key)",
            ["ocrOnly"] = ocrOnly,
            ["originalImageCopied"] = false,
            ["targetLanguage"] = "简体中文",
            ["source"] = "",
            ["translation"] = "",
            ["stages"] = stages
        };
        string directory = SettingsStore.DirectoryPath;
        string reportPath = Path.Combine(directory, "google-screenshot-diagnostics.json");
        string run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        bool failed = false;
        string current = "input";
        var stageTimer = Stopwatch.StartNew();

        void Begin(string name)
        {
            current = name;
            stages[name]["status"] = "running";
            stageTimer.Restart();
        }
        void End(string status = "success")
        {
            stages[current]["status"] = status;
            stages[current]["seconds"] = Math.Round(stageTimer.Elapsed.TotalSeconds, 3);
        }
        void SavePreview(string name, string text, string title)
        {
            Begin(name);
            try
            {
                var pages = OverlayRenderer.RenderPages(text, title: title);
                try
                {
                    if (pages.Count == 0) throw new InvalidOperationException("未能生成文字预览。");
                    string path = Path.Combine(directory, $"google-screenshot-{run}-{name}.png");
                    pages[0].Save(path, ImageFormat.Png);
                    stages[name]["path"] = path;
                    stages[name]["pages"] = pages.Count;
                    stages[name]["savedPage"] = 1;
                    End();
                }
                finally { foreach (var page in pages) page.Dispose(); }
            }
            catch (Exception error)
            {
                End("error"); stages[name]["error"] = error.Message; failed = true;
            }
        }

        try
        {
            Directory.CreateDirectory(directory);
            Begin("input");
            if (string.IsNullOrWhiteSpace(imagePath)) throw new InvalidOperationException("请使用 --image 指定要诊断的本机截图。");
            string fullPath = Path.GetFullPath(imagePath);
            await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length == 0 || file.Length > 64 * 1024 * 1024)
                throw new InvalidOperationException("截图文件为空或超过 64 MiB，请选择有效的截图。");
            report["imageFileName"] = Path.GetFileName(fullPath);
            report["imageFileBytes"] = file.Length;
            report["imageSha256"] = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
            file.Position = 0;
            byte[] png;
            using (var decoded = Image.FromStream(file, useEmbeddedColorManagement: false, validateImageData: true))
            {
                if (decoded.Width < 1 || decoded.Height < 1 || (long)decoded.Width * decoded.Height > 64_000_000)
                    throw new InvalidOperationException("截图尺寸无效或超过 6400 万像素，请缩小图片后诊断。");
                report["imageWidth"] = decoded.Width;
                report["imageHeight"] = decoded.Height;
                using var bitmap = new Bitmap(decoded);
                png = VisionImage.Prepare(bitmap);
            }
            report["preparedPngBytes"] = png.Length;
            End();

            Begin("recognition");
            string source;
            using (var recognizer = new LocalJapaneseOcrService())
                source = await recognizer.RecognizeAsync(png, CancellationToken.None);
            report["source"] = source;
            End(string.IsNullOrWhiteSpace(source) ? "empty" : "success");
            if (string.IsNullOrWhiteSpace(source))
            {
                stages["recognition"]["error"] = "没有识别到可读原文；未向谷歌发送请求。";
                stages["translation"]["status"] = "skipped_empty_source";
                stages["translationPreview"]["status"] = "skipped_no_translation";
                SavePreview("sourcePreview", "没有识别到可读原文。", "本地 OCR 诊断");
                failed = true;
            }
            else
            {
                SavePreview("sourcePreview", source, "本地日语 OCR 原文");
                if (ocrOnly)
                {
                    stages["translation"]["status"] = "skipped_by_request";
                    stages["translationPreview"]["status"] = "skipped_by_request";
                }
                else
                {
                    Begin("translation");
                    using var google = new GoogleTranslateWebClient();
                    string translation = await google.TranslateAsync(source, new AppSettings
                    {
                        TargetLanguage = "简体中文"
                    }, CancellationToken.None);
                    if (string.IsNullOrWhiteSpace(translation)) throw new InvalidOperationException("谷歌返回了空译文。");
                    report["translation"] = translation;
                    End();
                    SavePreview("translationPreview", translation, "Google 翻译结果");
                }
            }
        }
        catch (Exception error)
        {
            End(current == "translation" && error.Message == GoogleTranslateWebClient.BlockedMessage ? "blocked" : "error");
            stages[current]["error"] = error.Message;
            report["error"] = error.Message;
            failed = true;
        }
        finally
        {
            foreach (var stage in stages.Values)
                if (Equals(stage["status"], "not_started")) stage["status"] = "skipped_after_failure";
            report["success"] = !failed;
            report["seconds"] = Math.Round(total.Elapsed.TotalSeconds, 3);
            if (failed) Environment.ExitCode = 1;
            try
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                Environment.ExitCode = 1;
                Console.Error.WriteLine("无法保存 Google 截图诊断报告，请检查配置目录写入权限。");
            }
        }
    }
}
