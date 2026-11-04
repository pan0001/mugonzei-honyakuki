using Valve.VR;

namespace SteamVrTranslator;

public static class VrFrameGuardTests
{
    public static void Run()
    {
        int reports = 0, inputFrames = 0, rendered = 0;
        bool connected = true;
        var visuals = new VrFrameGuard();
        // Reproduce the old failure: a ring update faults during a held button.
        // The old combined catch disconnected here, preventing every subsequent input frame.
        for (int frame = 0; frame < 100; frame++)
        {
            bool healthy = VrFrameGuard.PollInput(() =>
            {
                if (!connected) throw new InvalidOperationException("Input connection was lost");
                inputFrames++;
            }, _ => reports++);
            if (!healthy) connected = false;
            visuals.Draw(frame * 16, () =>
            {
                if (frame == 2) throw new VrOverlayException("更新长按进度", EVROverlayError.InvalidHandle);
                rendered++;
            }, _ => reports++);
        }
        Check(connected && inputFrames == 100 && rendered > 50 && reports == 1,
            "One failed ring upload cannot stop later input or visual updates");
        Check(VrFrameGuard.PollInput(() => throw new VrOverlayException("隐藏框选边框", EVROverlayError.InvalidHandle), _ => reports++),
            "An overlay error inside input event dispatch does not invalidate the input connection");
        Check(!VrFrameGuard.PollInput(() => throw new InvalidOperationException("Input runtime fault"), _ => reports++),
            "A non-visual input failure is reported for runtime recovery");
        var persistent = new VrFrameGuard();
        int attempts = 0;
        for (int frame = 0; frame < 100; frame++)
            persistent.Draw(frame * 16, () => { attempts++; throw new InvalidOperationException("draw failed"); }, _ => { });
        Check(attempts is >= 5 and <= 7, "Persistent visual failures retry at a bounded rate, without hiding the previous frame");
        bool recovered = false;
        persistent.Draw(2000, () => recovered = true, _ => { });
        Check(recovered, "A later successful render recovers automatically");
        Console.WriteLine("PASS injected overlay failures preserve controller polling, throttle retries and recover rendering");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
