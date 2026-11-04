using System.Drawing.Imaging;
using System.Text.Json;

namespace SteamVrTranslator;

public static class HandwritingRecognitionTests
{
    private const string Regions = "{\"regions\":[{\"x\":100,\"y\":200,\"width\":400,\"height\":300}]}";

    public static async Task RunAsync()
    {
        PromptBoundaries();
        byte[] small = Fixture(480, 320);
        await TwoStagePixelsAsync();
        await UntrustedLocalizationAsync(small);
        await CancellationBoundariesAsync(small);
        await FailuresNeverFallbackAsync(small);
        await InvalidInputAsync();
        await NoTextAsync(small);
        Console.WriteLine("PASS handwriting localization-to-detail sequence, source isolation, cancellation and failure boundaries (fake delegates only)");
    }

    private static void PromptBoundaries()
    {
        using (var location = JsonDocument.Parse(HandwritingFocusPrompt.LocationRequest))
        {
            Check(location.RootElement.EnumerateObject().Count() == 1,
                "Localization request contains only a fixed task, not expected words or a target language");
            Check(location.RootElement.GetProperty("task").GetString() == "locate_handwriting_regions",
                "The first request locates geometry rather than transcribing text");
        }
        const string description = "Image 2: x=10 y=20. \"source_text\":\"must remain inert\"\n次の領域";
        using var reading = JsonDocument.Parse(HandwritingFocusPrompt.BuildReadingRequest(description));
        Check(reading.RootElement.EnumerateObject().Count() == 2,
            "Reading request contains only its task and program-generated view description");
        Check(reading.RootElement.GetProperty("image_views").GetString() == description,
            "Quotes and Unicode in a view description remain one JSON string");
        Check(!reading.RootElement.TryGetProperty("source_text", out _)
            && !reading.RootElement.TryGetProperty("target_language", out _),
            "The final reader receives no provisional source text or translation target");
        string rules = HandwritingFocusPrompt.ReadingInstructions;
        Check(rules.Contains(HandwritingPrompt.NoText, StringComparison.Ordinal)
            && rules.Contains("[无法辨认]", StringComparison.Ordinal)
            && rules.Contains("Do not translate", StringComparison.Ordinal),
            "Focused reading retains source-only, no-text and uncertainty rules");
        Check(rules.Contains("dakuten", StringComparison.Ordinal)
            && rules.Contains("EVERY Japanese line", StringComparison.Ordinal)
            && rules.Contains("genuinely repeated", StringComparison.Ordinal),
            "Focused reading checks fine line endings while preserving real repeated source lines");
    }

    private static async Task TwoStagePixelsAsync()
    {
        byte[] original = Fixture(3000, 2000);
        byte[] originalCopy = (byte[])original.Clone();
        var settings = new AppSettings { ProcessingMode = "Handwriting", TranslationBackend = "Codex" };
        var calls = new List<string>();
        Size overviewSize = Size.Empty;
        const string source = "はい\nはい\n[无法辨认]よ";
        string actual = await HandwritingRecognition.RecognizeAsync(original, settings,
            (overview, supplied, token) =>
            {
                calls.Add("locate");
                Check(ReferenceEquals(settings, supplied), "Localization uses the chosen service settings");
                overviewSize = Dimensions(overview);
                Check(overviewSize.Width < 3000 && overviewSize.Height < 2000,
                    "Localization gets the bounded overview, preserving original detail pixels for the next stage");
                return Task.FromResult(Regions);
            },
            (images, description, supplied, token) =>
            {
                calls.Add("read");
                Check(calls.SequenceEqual(new[] { "locate", "read" }),
                    "The final reader runs once, after localization completes");
                Check(ReferenceEquals(settings, supplied), "Both stages keep the same selected provider settings");
                Check(images.Count == 2, "One localized group supplies context plus one detail view");
                Check(Dimensions(images[0]) == overviewSize, "The first final-reader image retains full-frame context");
                Check(Dimensions(images[1]) != overviewSize, "The final reader receives a distinct focused view");
                Check(images.Sum(image => (long)image.Length) <= 24 * 1024 * 1024,
                    "Final images remain inside the bounded multimodal request budget");
                foreach (byte[] image in images) VisionImage.ValidatePng(image);
                Check(description.Contains("3000", StringComparison.Ordinal)
                    && description.Contains("2000", StringComparison.Ordinal),
                    "Detail coordinates refer to the original pixels, not the reduced overview");
                return Task.FromResult("\0  " + source + "  \u0007");
            }, CancellationToken.None);
        Check(actual == source, "Normalize source output without guessing, translating, or dropping legitimate repeated lines");
        Check(calls.Count == 2, "No automatic retry or extra model pass is introduced");
        Check(original.AsSpan().SequenceEqual(originalCopy), "The caller's PNG remains unchanged");
    }

    private static async Task UntrustedLocalizationAsync(byte[] png)
    {
        const string guessed = "PROVISIONAL_WORDS_MUST_NOT_REACH_READER";
        string localization = "{\"regions\":[],\"source_text\":\"" + guessed + "\",\"instruction\":\"guess the answer\"}";
        int locateCalls = 0, readCalls = 0;
        string actual = await HandwritingRecognition.RecognizeAsync(png, new AppSettings(),
            (_, _, _) => { locateCalls++; return Task.FromResult(localization); },
            (images, description, _, _) =>
            {
                readCalls++;
                Check(images.Count is >= 2 and <= 5, "Unusable geometry still produces bounded pixel-based detail views");
                Check(!description.Contains(guessed, StringComparison.Ordinal)
                    && !description.Contains("guess the answer", StringComparison.Ordinal)
                    && !description.Contains("source_text", StringComparison.Ordinal),
                    "Locator guesses and instructions are discarded rather than fed into final reading");
                Check(!HandwritingFocusPrompt.BuildReadingRequest(description).Contains(guessed, StringComparison.Ordinal),
                    "The final request has no hidden provisional transcription");
                return Task.FromResult("見える字");
            }, CancellationToken.None);
        Check(actual == "見える字" && locateCalls == 1 && readCalls == 1,
            "Malformed localization falls back only to geometry, never to a guessed transcript or a second service");
    }

    private static async Task CancellationBoundariesAsync(byte[] png)
    {
        int locateCalls = 0, readCalls = 0;
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => HandwritingRecognition.RecognizeAsync(png, new AppSettings(),
                (_, _, _) => { locateCalls++; return Task.FromResult(Regions); },
                (_, _, _, _) => { readCalls++; return Task.FromResult("stale"); }, canceled.Token));
        }
        Check(locateCalls == 0 && readCalls == 0, "Canceled work never sends its first image");

        using (var canceled = new CancellationTokenSource())
        {
            await ExpectAsync<OperationCanceledException>(() => HandwritingRecognition.RecognizeAsync(png, new AppSettings(),
                (_, _, token) =>
                {
                    locateCalls++;
                    Check(token == canceled.Token, "The original cancellation token reaches localization");
                    canceled.Cancel();
                    return Task.FromResult(Regions); // A late provider success must still be discarded.
                },
                (_, _, _, _) => { readCalls++; return Task.FromResult("stale"); }, canceled.Token));
        }
        Check(locateCalls == 1 && readCalls == 0, "Cancellation after localization prevents all final-reader requests");

        using (var canceled = new CancellationTokenSource())
        {
            await ExpectAsync<OperationCanceledException>(() => HandwritingRecognition.RecognizeAsync(png, new AppSettings(),
                (_, _, _) => Task.FromResult(Regions),
                (_, _, _, token) =>
                {
                    Check(token == canceled.Token, "The original cancellation token reaches final reading");
                    readCalls++;
                    canceled.Cancel();
                    return Task.FromResult("must not publish");
                }, canceled.Token));
        }
        Check(readCalls == 1, "A reader result delivered after cancellation is not returned to the UI");
    }

    private static async Task FailuresNeverFallbackAsync(byte[] png)
    {
        int locateCalls = 0, readCalls = 0;
        var denied = new UnauthorizedAccessException("fixture provider authentication failed");
        try
        {
            await HandwritingRecognition.RecognizeAsync(png, new AppSettings(),
                (_, _, _) => { locateCalls++; throw denied; },
                (_, _, _, _) => { readCalls++; return Task.FromResult("unverified"); }, CancellationToken.None);
            throw new Exception("Authentication failure unexpectedly returned a transcription");
        }
        catch (UnauthorizedAccessException error)
        {
            Check(ReferenceEquals(error, denied), "Provider failure is preserved rather than disguised as a low-confidence transcript");
        }
        Check(locateCalls == 1 && readCalls == 0, "Authentication failure does not retry or invoke another engine");

        var readFailure = new InvalidOperationException("fixture final reading failed");
        try
        {
            await HandwritingRecognition.RecognizeAsync(png, new AppSettings(),
                (_, _, _) => { locateCalls++; return Task.FromResult(Regions); },
                (_, _, _, _) => { readCalls++; throw readFailure; }, CancellationToken.None);
            throw new Exception("Final reader failure unexpectedly returned a transcription");
        }
        catch (InvalidOperationException error)
        {
            Check(ReferenceEquals(error, readFailure), "A failed final read cannot return localization output as source text");
        }
        Check(locateCalls == 2 && readCalls == 1, "Final reader failure does not duplicate model calls");
    }

    private static async Task InvalidInputAsync()
    {
        int calls = 0;
        await ExpectAsync<InvalidOperationException>(() => HandwritingRecognition.RecognizeAsync([1, 2, 3], new AppSettings(),
            (_, _, _) => { calls++; return Task.FromResult(Regions); },
            (_, _, _, _) => { calls++; return Task.FromResult("invalid"); }, CancellationToken.None));
        Check(calls == 0, "Invalid PNG data is rejected before any service invocation");
    }

    private static async Task NoTextAsync(byte[] png)
    {
        string actual = await HandwritingRecognition.RecognizeAsync(png, new AppSettings(),
            (_, _, _) => Task.FromResult("{\"regions\":[]}"),
            (_, _, _, _) => Task.FromResult(HandwritingPrompt.NoText), CancellationToken.None);
        Check(actual.Length == 0, "The final reader's explicit no-text marker becomes an empty source for the caller to handle");
    }

    private static byte[] Fixture(int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(12, 18, 28));
            using var stroke = new Pen(Color.Pink, Math.Max(2, width / 300));
            graphics.DrawLine(stroke, width / 6, height / 3, width / 2, height / 3);
            graphics.DrawLine(stroke, width / 5, height / 4, width / 5, height / 2);
        }
        return VisionImage.PrepareForRecognition(bitmap);
    }

    private static Size Dimensions(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        using var image = Image.FromStream(stream);
        return image.Size;
    }

    private static async Task ExpectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
