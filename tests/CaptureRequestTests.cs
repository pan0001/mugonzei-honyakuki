namespace SteamVrTranslator;

public static class CaptureRequestTests
{
    public static void Run()
    {
        IdleRequestOwnsAndDrains();
        LatestQueuedRequestWins();
        CancelPendingPreservesOwner();
        FinishReleasesOwnerAndDiscardsQueue();
        GestureCancellationIsScoped();
        Console.WriteLine("PASS: capture ownership, latest request mode/source, pending cancellation and no backlog replay");
    }

    private static void GestureCancellationIsScoped()
    {
        var requests = new LatestCaptureRequests();
        requests.Enqueue(new CaptureRequest(false, "VR", 10));
        requests.TryTake(out _);
        var keyboard = new CaptureRequest(false, "keyboard");
        requests.Enqueue(keyboard);
        Check(!requests.CancelPending(10), "Finishing an old second circle must not cancel a newer keyboard request");
        Check(requests.TryTake(out var next) && next == keyboard, "Keyboard request survives unrelated gesture cancellation");
        requests.Enqueue(new CaptureRequest(false, "VR", 11));
        Check(!requests.CancelPending(10), "Earlier gesture cannot remove a newer hold");
        Check(requests.CancelPending(11) && !requests.HasPending, "Second circle removes its own queued request");
        Check(!requests.CancelPending(0), "Non-gesture sentinel cannot cancel arbitrary queued work");
    }

    private static void IdleRequestOwnsAndDrains()
    {
        var requests = new LatestCaptureRequests();
        Check(!requests.HasPending && !requests.TryTake(out _), "New queue is empty");
        var first = new CaptureRequest(false, "左手 X 长按");
        Check(requests.Enqueue(first), "Idle enqueue acquires ownership");
        Check(requests.HasPending, "Owner's first request is pending until taken");
        Check(requests.TryTake(out var active) && active == first, "Owner takes the requested mode and source");
        Check(!requests.HasPending && !requests.TryTake(out _), "A taken request is not replayed");
        Check(!requests.Enqueue(new CaptureRequest(true, "仅截屏识别")), "Taking a request does not release ownership");
    }

    private static void LatestQueuedRequestWins()
    {
        var requests = new LatestCaptureRequests();
        requests.Enqueue(new CaptureRequest(false, "first"));
        requests.TryTake(out _);
        Check(!requests.Enqueue(new CaptureRequest(false, "controller")), "Queued request does not become a second owner");
        Check(!requests.Enqueue(new CaptureRequest(false, "hotkey")), "Another request replaces the pending slot");
        var latest = new CaptureRequest(true, "desktop OCR button");
        Check(!requests.Enqueue(latest), "Latest request still belongs to the original owner");
        Check(requests.TryTake(out var next) && next == latest, "Latest OCR mode and source win together");
        Check(!requests.TryTake(out _) && !requests.HasPending, "Superseded requests never replay");

        var translation = new CaptureRequest(false, "controller again");
        Check(!requests.Enqueue(translation), "Owner remains active across multiple drained requests");
        Check(requests.TryTake(out next) && next == translation, "Mode can switch back to translation");
        Check(!requests.TryTake(out _), "No hidden backlog after another capture");
    }

    private static void CancelPendingPreservesOwner()
    {
        var requests = new LatestCaptureRequests();
        requests.Enqueue(new CaptureRequest(false, "active"));
        requests.TryTake(out _);
        requests.Enqueue(new CaptureRequest(false, "queued"));
        requests.CancelPending();
        Check(!requests.HasPending && !requests.TryTake(out _), "Cancel removes pending work");
        requests.CancelPending();
        var afterCancel = new CaptureRequest(true, "new request after cancel");
        Check(!requests.Enqueue(afterCancel), "CancelPending does not release the active owner");
        Check(requests.TryTake(out var next) && next == afterCancel, "A fresh request may queue after cancellation");
        requests.Finish();
        Check(requests.Enqueue(new CaptureRequest(false, "next owner")), "Finish finally releases ownership");
    }

    private static void FinishReleasesOwnerAndDiscardsQueue()
    {
        var requests = new LatestCaptureRequests();
        requests.Enqueue(new CaptureRequest(false, "discard me"));
        requests.Finish();
        Check(!requests.HasPending && !requests.TryTake(out _), "Finish clears a request not yet taken");
        requests.Finish();
        var newOwner = new CaptureRequest(true, "fresh owner");
        Check(requests.Enqueue(newOwner), "Repeated Finish leaves the queue idle");
        Check(requests.TryTake(out var next) && next == newOwner, "Only the fresh request is delivered");
        requests.Enqueue(new CaptureRequest(false, "queued behind active"));
        requests.Finish();
        Check(!requests.TryTake(out _), "Finish drops a pending replacement as well");
        Check(requests.Enqueue(new CaptureRequest(false, "restart")), "Next enqueue owns after active-and-pending cleanup");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Capture request test failed: " + message);
    }
}
