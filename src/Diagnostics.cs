using System.Text.Json;

namespace SteamVrTranslator;

internal static class Diagnostics
{
    public static async Task RunVisionAsync()
    {
        var results = new Dictionary<string, object>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(SettingsStore.DirectoryPath);
        try
        {
            var settings = new AppSettings { TranslationBackend = "Codex", ProcessingMode = "Image" };
            using var sample = new Bitmap(1200, 520);
            using (var graphics = Graphics.FromImage(sample))
            using (var font = new Font("Arial", 58, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("Welcome to SteamVR!", font, Brushes.Black, 36, 30);
                graphics.DrawString("Gate 42", font, Brushes.Black, 36, 150);
                graphics.FillEllipse(Brushes.RoyalBlue, 46, 290, 100, 100);
                graphics.DrawString("Ready to enter", font, Brushes.Black, 180, 300);
            }
            byte[] png = VisionImage.Prepare(sample);
            results["input"] = "Generated test image; no user screenshot";
            results["localOcrUsed"] = false;
            results["imageBytes"] = png.Length;
            using var translator = new TranslationService();
            string result = await translator.AnalyzeImageAsync(png, settings, CancellationToken.None);
            results["result"] = result;
            results["imageTextVerified"] = result.Contains("42", StringComparison.Ordinal);
            if (!result.Contains("42", StringComparison.Ordinal))
                throw new InvalidOperationException("图片请求已完成，但返回内容未包含测试图片中的编号 42，请检查实际识图结果。");
            var pages = OverlayRenderer.RenderPages(result, title: "识图结果");
            try { pages[0].Save(Path.Combine(SettingsStore.DirectoryPath, "vision-smoke-preview.png"), System.Drawing.Imaging.ImageFormat.Png); }
            finally { foreach (var page in pages) page.Dispose(); }
            results["success"] = true;
        }
        catch (Exception ex) { results["success"] = false; results["error"] = ex.Message; Environment.ExitCode = 1; }
        results["seconds"] = Math.Round(timer.Elapsed.TotalSeconds, 2);
        File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "vision-diagnostics.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task RunCodexAsync()
    {
        var results = new Dictionary<string, object>();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(SettingsStore.DirectoryPath);
        try
        {
            var settings = new AppSettings { TranslationBackend = "Codex" };
            results["login"] = await CodexTranslationClient.CheckLoginAsync(settings, CancellationToken.None);
            using var sample = new Bitmap(1300, 240);
            using (var graphics = Graphics.FromImage(sample))
            using (var font = new Font("Arial", 52, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("Welcome to SteamVR!", font, Brushes.Black, 30, 25);
                graphics.DrawString("Press the button to translate.", font, Brushes.Black, 30, 120);
            }
            using var ocr = new OcrService();
            string source = ocr.Recognize(sample, "eng");
            results["recognized"] = source;
            using var translator = new TranslationService();
            string translated = await translator.TranslateAsync(source, settings, CancellationToken.None);
            results["translation"] = translated;
            var pages = OverlayRenderer.RenderPages(translated);
            try { pages[0].Save(Path.Combine(SettingsStore.DirectoryPath, "codex-smoke-preview.png"), System.Drawing.Imaging.ImageFormat.Png); }
            finally { foreach (var page in pages) page.Dispose(); }
            results["success"] = true;
        }
        catch (Exception ex) { results["success"] = false; results["error"] = ex.Message; Environment.ExitCode = 1; }
        results["seconds"] = Math.Round(timer.Elapsed.TotalSeconds, 2);
        File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "codex-diagnostics.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Run()
    {
        var results = new Dictionary<string, object>();
        try
        {
            using var vr = new VrService();
            results["connected"] = vr.Connect();
            results["status"] = vr.Status;
            if (vr.IsConnected)
            {
                vr.Poll();
                vr.PrepareEyeCapture();
                Thread.Sleep(80);
                using var eye = vr.CaptureEye();
                results["captureWidth"] = eye.Width;
                results["captureHeight"] = eye.Height;
                using var status = OverlayRenderer.RenderStatus("字幕显示测试：半透明文字条位于视野下方。");
                vr.ShowOverlay(status, .9f, 1.2f, -.35f);
                results["subtitleWidth"] = status.Width;
                results["subtitleHeight"] = status.Height;
                var start = Environment.TickCount64;
                while (vr.IsConnected && Environment.TickCount64 - start < 3000)
                {
                    long elapsed = Environment.TickCount64 - start;
                    using var ring = OverlayRenderer.RenderHoldProgress((elapsed % 1500) / 1500f, elapsed >= 1500);
                    vr.ShowHoldOverlay(ring, 1.2f, -.20f);
                    vr.Poll(); Thread.Sleep(33);
                }
                vr.HideHoldOverlay();
                vr.HideOverlay();
                results["overlaySubmitted"] = true;
                results["progressOverlaySubmitted"] = true;
                results["finalConnected"] = vr.IsConnected;
                results["finalStatus"] = vr.Status;
                if (vr.IsConnected) results["translationBinding"] = vr.GetTranslationBindingDiagnostics();
            }
        }
        catch (Exception ex) { results["error"] = ex.ToString(); }
        Directory.CreateDirectory(SettingsStore.DirectoryPath);
        File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "diagnostics.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
