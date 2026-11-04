using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamVrTranslator;

/// <summary>Compares the existing direct-image path with source-first recognition on one supplied image.</summary>
internal static class RecognitionQualityDiagnostics
{
    public static async Task RunAsync(string? imagePath)
    {
        var total = Stopwatch.StartNew();
        var stages = new Dictionary<string, Dictionary<string, object?>>();
        foreach (string name in new[] { "input", "directBaseline", "recognition", "sourcePreview", "translation", "translationPreview" })
            stages[name] = new() { ["status"] = "not_started", ["seconds"] = 0d };
        var report = new Dictionary<string, object?>
        {
            ["success"] = false,
            ["createdAtUtc"] = DateTimeOffset.UtcNow,
            ["backend"] = "Codex",
            ["targetLanguage"] = "简体中文",
            ["accuracyDisclaimer"] = "success 仅表示诊断流程完成，不代表识别正确；请将原文与指定图片逐字核对。",
            ["capturesImages"] = false,
            ["originalImageCopied"] = false,
            ["readsSavedSettings"] = false,
            ["directBaseline"] = "",
            ["source"] = "",
            ["translation"] = "",
            ["stages"] = stages
        };
        string directory = SettingsStore.DirectoryPath;
        string reportPath = Path.Combine(directory, "recognition-quality-diagnostics.json");
        string run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        string temporaryReport = Path.Combine(directory, "recognition-quality-" + run + ".tmp");
        var settings = new AppSettings { TranslationBackend = "Codex", ProcessingMode = "Handwriting" };
        bool failed = false;
        string current = "input";
        var stageTimer = Stopwatch.StartNew();

        void Flush()
        {
            report["seconds"] = Math.Round(total.Elapsed.TotalSeconds, 3);
            File.WriteAllText(temporaryReport, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temporaryReport, reportPath, true);
        }

        void Begin(string name)
        {
            current = name;
            stages[name]["status"] = "running";
            stageTimer.Restart();
            Flush();
        }

        void End(string status = "success")
        {
            stages[current]["status"] = status;
            stages[current]["seconds"] = Math.Round(stageTimer.Elapsed.TotalSeconds, 3);
            Flush();
        }

        void ImageMetadata(string name, byte[] png)
        {
            stages[name]["imageWidth"] = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
            stages[name]["imageHeight"] = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
            stages[name]["imageBytes"] = png.Length;
            stages[name]["imageSha256"] = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        }

        async Task<bool> ModelStage(string name, Func<Task<string>> action, string outputField)
        {
            Begin(name);
            try
            {
                string text = await action().ConfigureAwait(false);
                report[outputField] = text;
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("模型未返回可读文字。");
                End();
                return true;
            }
            catch (Exception error)
            {
                stages[name]["error"] = error.Message;
                failed = true;
                End("error");
                return false;
            }
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
                    string path = Path.Combine(directory, $"recognition-quality-{run}-{name}.png");
                    pages[0].Save(path, ImageFormat.Png);
                    stages[name]["path"] = path;
                    stages[name]["pages"] = pages.Count;
                    stages[name]["savedPage"] = 1;
                }
                finally { foreach (var page in pages) page.Dispose(); }
                End();
            }
            catch (Exception error)
            {
                stages[name]["error"] = error.Message;
                failed = true;
                End("error");
            }
        }

        try
        {
            Directory.CreateDirectory(directory);
            Begin("input");
            if (string.IsNullOrWhiteSpace(imagePath)) throw new InvalidOperationException("请使用 --image 指定要诊断的本机截图。");
            string fullPath = Path.GetFullPath(imagePath);
            byte[] baselinePng, recognitionPng;
            await using (var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length == 0 || file.Length > 64 * 1024 * 1024)
                    throw new InvalidOperationException("截图文件为空或超过 64 MiB，请选择有效的截图。");
                report["imageFileName"] = Path.GetFileName(fullPath);
                report["imageFileBytes"] = file.Length;
                report["imageSha256"] = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
                file.Position = 0;
                using var decoded = Image.FromStream(file, useEmbeddedColorManagement: false, validateImageData: true);
                if (decoded.Width < 1 || decoded.Height < 1 || (long)decoded.Width * decoded.Height > 64_000_000)
                    throw new InvalidOperationException("截图尺寸无效或超过 6400 万像素，请缩小图片后诊断。");
                report["imageWidth"] = decoded.Width;
                report["imageHeight"] = decoded.Height;
                using var bitmap = new Bitmap(decoded);
                baselinePng = VisionImage.Prepare(bitmap);
                recognitionPng = VisionImage.PrepareForRecognition(bitmap);
            }
            ImageMetadata("directBaseline", baselinePng);
            ImageMetadata("recognition", recognitionPng);
            End();

            using var translator = new TranslationService();
            await ModelStage("directBaseline", () => translator.AnalyzeImageAsync(baselinePng, settings, CancellationToken.None), "directBaseline");
            if (await ModelStage("recognition", () => translator.RecognizeHandwritingAsync(recognitionPng, settings, CancellationToken.None), "source"))
            {
                string source = (string)report["source"]!;
                SavePreview("sourcePreview", source, "AI 手写识别原文");
                stages["translation"]["sourceCharacters"] = source.Length;
                if (await ModelStage("translation", () => translator.TranslateAsync(source, settings, CancellationToken.None), "translation"))
                    SavePreview("translationPreview", (string)report["translation"]!, "手写翻译结果");
            }
            else
            {
                stages["sourcePreview"]["status"] = "skipped_no_source";
                stages["translation"]["status"] = "skipped_no_source";
                stages["translationPreview"]["status"] = "skipped_no_translation";
                Flush();
            }
        }
        catch (Exception error)
        {
            failed = true;
            stages[current]["status"] = "error";
            stages[current]["seconds"] = Math.Round(stageTimer.Elapsed.TotalSeconds, 3);
            stages[current]["error"] = error.Message;
            report["error"] = error.Message;
        }
        finally
        {
            foreach (var stage in stages.Values)
                if (Equals(stage["status"], "not_started")) stage["status"] = "skipped_after_failure";
            report["success"] = !failed;
            if (failed) Environment.ExitCode = 1;
            try
            {
                Directory.CreateDirectory(directory);
                Flush();
            }
            catch
            {
                Environment.ExitCode = 1;
                Console.Error.WriteLine("无法保存识别质量诊断报告，请检查配置目录写入权限。");
            }
            finally
            {
                try { File.Delete(temporaryReport); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
