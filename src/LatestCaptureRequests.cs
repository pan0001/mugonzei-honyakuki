namespace SteamVrTranslator;

internal sealed class SelectionCaptureSnapshot
{
    private readonly byte[] png;
    public SpatialAnchor Anchor { get; }

    public SelectionCaptureSnapshot(byte[] png, SpatialAnchor anchor)
    {
        VisionImage.ValidatePng(png);
        this.png = (byte[])png.Clone();
        Anchor = anchor;
    }

    public Bitmap DecodeImage()
    {
        using var stream = new MemoryStream(png, writable: false);
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded);
    }
}

internal readonly record struct CaptureRequest(bool OcrOnly, string Source, long GestureId = 0,
    SelectionCaptureSnapshot? Selection = null, Bitmap? DesktopImage = null, DateTime? CapturedAt = null);

/// <summary>
/// UI-thread request ownership with one pending slot. New requests replace queued
/// work while the current owner cancels or finishes its active operation.
/// </summary>
internal sealed class LatestCaptureRequests
{
    private bool running;
    private CaptureRequest? pending;

    public bool HasPending => pending.HasValue;

    public bool Enqueue(CaptureRequest request)
    {
        // The slot owns its desktop bitmap until TryTake transfers ownership to the pipeline.
        CancelPending();
        pending = request;
        if (running) return false;
        running = true;
        return true;
    }

    public bool TryTake(out CaptureRequest request)
    {
        if (pending is not { } next)
        {
            request = default;
            return false;
        }
        request = next;
        pending = null;
        return true;
    }

    public void CancelPending()
    {
        pending?.DesktopImage?.Dispose();
        pending = null;
    }

    public bool CancelPending(long gestureId)
    {
        if (gestureId == 0 || pending?.GestureId != gestureId) return false;
        CancelPending();
        return true;
    }

    public bool CancelSpatialPending()
    {
        if (pending?.Selection is null) return false;
        CancelPending();
        return true;
    }

    public void Finish()
    {
        CancelPending();
        running = false;
    }
}
