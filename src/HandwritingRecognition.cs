namespace SteamVrTranslator;

/// <summary>Locates writing, then reads source pixels with context and focused detail views.</summary>
internal static class HandwritingRecognition
{
    public static async Task<string> RecognizeAsync(byte[] png, AppSettings settings,
        Func<byte[], AppSettings, CancellationToken, Task<string>> locate,
        Func<IReadOnlyList<byte[]>, string, AppSettings, CancellationToken, Task<string>> read,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(locate);
        ArgumentNullException.ThrowIfNull(read);

        // Keep all GDI decoding/encoding off the UI thread. Only managed PNG arrays
        // cross awaits, so no Bitmap or stream survives a failed model request.
        byte[] overview = await Task.Run(() => WithImage(png, VisionImage.Prepare), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        string locations = await locate(overview, settings, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        // The locator's response is geometry input only. The builder discards other
        // fields and supplies bounded fallback crops if geometry is unusable.
        var views = await Task.Run(() => WithImage(png,
            image => HandwritingDetailImages.Create(image, locations)), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        string result = await read(views.Images, views.Description, settings, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return HandwritingPrompt.NormalizeResult(result);
    }

    private static T WithImage<T>(byte[] png, Func<Bitmap, T> action)
    {
        VisionImage.ValidatePng(png);
        using var stream = new MemoryStream(png, writable: false);
        using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        using var bitmap = new Bitmap(decoded);
        return action(bitmap);
    }
}
