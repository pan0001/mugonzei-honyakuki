using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace SteamVrTranslator;

internal static class GoogleWebDiagnostics
{
    public static async Task RunAsync()
    {
        string directory = SettingsStore.DirectoryPath;
        Directory.CreateDirectory(directory);
        var timer = Stopwatch.StartNew();
        var report = new Dictionary<string, object?>
        {
            ["createdAtUtc"] = DateTimeOffset.UtcNow,
            ["backend"] = "Google Translate background webpage / WebView2",
            ["usesUserScreenshot"] = false,
            ["success"] = false
        };
        string stage = "startupCancellation";
        try
        {
            using var browser = new GoogleTranslateWebClient();
            var settings = new AppSettings { TargetLanguage = "简体中文" };
            bool cancelled = false;
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
            {
                try { await browser.TranslateAsync("Cancelled diagnostic request.", settings, cancellation.Token); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
            }
            report["startupCancellationPassed"] = cancelled;
            if (!cancelled) throw new InvalidOperationException("启动时取消的任务没有取消，已停止后续诊断。");
            stage = "firstTranslation";
            string first = await browser.TranslateAsync("Hello world.", settings, CancellationToken.None);
            report["first"] = new { source = "Hello world.", translation = first, seconds = Math.Round(timer.Elapsed.TotalSeconds, 2) };
            if (!first.Contains("世界", StringComparison.Ordinal))
                throw new InvalidOperationException("固定测试文字的网页译文未包含预期词，请核对网页预览。");

            stage = "secondTranslation";
            var secondTimer = Stopwatch.StartNew();
            string second = await browser.TranslateAsync("Good night, see you tomorrow.", settings, CancellationToken.None);
            report["second"] = new { source = "Good night, see you tomorrow.", translation = second, seconds = Math.Round(secondTimer.Elapsed.TotalSeconds, 2) };
            report["resultChanged"] = second != first;
            if (second == first || !second.Contains("明天", StringComparison.Ordinal))
                throw new InvalidOperationException("第二次网页译文未通过新结果核对，请检查旧结果隔离。");

            stage = "preview";
            string webpage = Path.Combine(directory, "google-web-smoke-page.png");
            await browser.SavePreviewAsync(webpage);
            report["webpagePreview"] = webpage;
            var pages = OverlayRenderer.RenderPages(second, title: "Google 网页翻译");
            try
            {
                string subtitle = Path.Combine(directory, "google-web-smoke-subtitle.png");
                pages[0].Save(subtitle, ImageFormat.Png);
                report["subtitlePreview"] = subtitle;
                report["subtitlePages"] = pages.Count;
            }
            finally { foreach (var page in pages) page.Dispose(); }
            report["success"] = true;
        }
        catch (Exception error)
        {
            report["failedStage"] = stage;
            report["error"] = error.Message;
            Environment.ExitCode = 1;
        }
        finally
        {
            report["seconds"] = Math.Round(timer.Elapsed.TotalSeconds, 2);
            await File.WriteAllTextAsync(Path.Combine(directory, "google-web-diagnostics.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
