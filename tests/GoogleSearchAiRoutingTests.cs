using System.Drawing.Imaging;
using System.Drawing.Text;

namespace SteamVrTranslator;

internal static class GoogleSearchAiRoutingTests
{
    private static byte[] Sign(string text)
    {
        using var bitmap = new Bitmap(680, 155);
        using (var g = Graphics.FromImage(bitmap))
        using (var font = new Font("Microsoft YaHei UI", 50, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString(text, font, Brushes.Black, new PointF(28, 34));
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    internal static async Task RunAsync()
    {
        var settings = new AppSettings { TranslationBackend = "GoogleSearchAI", ProcessingMode = "Image", TargetLanguage = "日本語",
            ApiKey = "must-not-leave-client", ApiBaseUrl = "https://unused.invalid", ApiModel = "never-transmit-this-model" };
        var image = Sign("Next train 2749");
        int calls = 0;
        using var cancel = new CancellationTokenSource();
        using var service = new TranslationService((bytes, prompt, _) =>
        {
            calls++;
            if (!ReferenceEquals(bytes, image) || !prompt.Contains(VisionPrompt.BuildRequest(settings))
                || !prompt.Contains(VisionPrompt.NoText) || prompt.Contains(settings.ApiKey) || prompt.Contains(settings.ApiModel))
                throw new Exception("Unexpected image, target or provider credentials in Google search submission");
            if (calls == 3) cancel.Cancel();
            return Task.FromResult(calls == 2 ? VisionPrompt.NoText : "次の列車は2749号です。");
        });
        if (await service.AnalyzeImageAsync(image, settings, default) != "次の列車は2749号です。"
            || await service.AnalyzeImageAsync(image, settings, default) != "" || calls != 2)
            throw new Exception("Google search image results must use one submission per capture");
        try { await service.AnalyzeImageAsync(image, settings, cancel.Token); throw new Exception("Late cancelled answer accepted"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        try { await service.TranslateAsync("text", settings, default); throw new Exception("Accepted an unsupported text request"); }
        catch (InvalidOperationException) { }
        try { await service.RecognizeHandwritingAsync(image, settings, default); throw new Exception("Unexpected second OCR pass"); }
        catch (InvalidOperationException) { }
        try { await service.AnalyzeImageAsync([], settings, default); throw new Exception("Empty screenshot was sent"); }
        catch (InvalidOperationException) { }
        settings.TargetLanguage = "";
        try { await service.AnalyzeImageAsync(image, settings, default); throw new Exception("Empty target language accepted"); }
        catch (InvalidOperationException) { }
        if (calls != 3) throw new Exception("Rejected tasks must not submit, retry or fall back");
        Console.WriteLine("PASS Google Search AI screenshot routing, target, single submit, no-text and late cancellation");
    }

    // Explicit diagnostic only. No screen capture, saved credentials, or credit account.
    internal static async Task RunLiveAsync()
    {
        var scratch = Directory.CreateTempSubdirectory("Mugonzei-Google-Search-AI-Smoke-");
        try
        {
            using var browser = new GoogleSearchAiWebClient(scratch.FullName);
            using var service = new TranslationService(browser.TranslateImageAsync);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(130));
            string number = Random.Shared.Next(2000, 9000).ToString();
            string answer = await service.AnalyzeImageAsync(Sign("Train " + number), new AppSettings
            {
                TranslationBackend = "GoogleSearchAI", ProcessingMode = "Image", TargetLanguage = "简体中文"
            }, limit.Token);
            if (!answer.Contains(number, StringComparison.Ordinal) || !answer.Contains('车') && !answer.Contains('車'))
                throw new Exception("The live result did not translate the unique train sign; no answer was logged.");
            Console.WriteLine("PASS live Google AI Mode translated the train sign and preserved its random number.");
        }
        finally
        {
            try { scratch.Delete(true); }
            catch (IOException) { /* The runtime may hold its disposable profile briefly after exit. */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
