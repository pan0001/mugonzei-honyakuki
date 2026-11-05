namespace SteamVrTranslator;

internal sealed partial class MainForm
{
    private Task PublishResultPagesAsync(string text, long gestureId, CancellationToken token,
        string? source = null, string title = "翻译")
    {
        // Capture geometry on the input thread. Text layout and up to 96 bitmap
        // allocations must not stop controller polling or the Cancel button.
        SpatialAnchor? anchor = activeAnchor;
        return RenderAndPublishPagesAsync(() => anchor is { } fixedAnchor
            ? SelectionOverlayRenderer.RenderPages(text, fixedAnchor.Aspect, source, title)
            : OverlayRenderer.RenderPages(text, source, title), gestureId, anchor, token);
    }

    internal async Task RenderAndPublishPagesAsync(Func<List<Bitmap>> render, long gestureId,
        SpatialAnchor? anchor, CancellationToken token)
    {
        List<Bitmap>? rendered = await Task.Run(render, token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (closing || IsDisposed) throw new OperationCanceledException(token);
            // SetPages always takes ownership, including its invalid-anchor path.
            var owned = rendered;
            rendered = null;
            SetPages(owned, gestureId, anchor);
        }
        finally
        {
            if (rendered is not null)
                foreach (var page in rendered) page.Dispose();
        }
    }
}
