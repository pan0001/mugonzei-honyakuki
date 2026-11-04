namespace SteamVrTranslator;

/// <summary>Visual failures must not take down the input connection. Retry a failed visual at a bounded rate.</summary>
internal sealed class VrFrameGuard
{
    private long retryAt;

    public static bool PollInput(Action poll, Action<Exception> report)
    {
        try { poll(); return true; }
        catch (VrOverlayException error) { report(error); return true; }
        catch (Exception error) { report(error); return false; }
    }

    public void Draw(long now, Action draw, Action<Exception> report)
    {
        if (now < retryAt) return;
        try { draw(); retryAt = 0; }
        catch (Exception error) { retryAt = now + 250; report(error); }
    }
}
