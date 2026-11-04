namespace SteamVrTranslator;

public static class LongPressTests
{
    public static void Run()
    {
        InitialReleaseAndStageBoundaries();
        BothEdgesAreOneShot();
        ReleaseAfterTranslationDoesNotCancel();
        ShortTapsDoNotAccumulate();
        InactiveAndResetDisarm();
        StalledPollCancelsWithoutTranslating();
        ClockRegressionDisarmsWithoutReusingIds();
        CustomAndVeryLargeThresholds();
        Console.WriteLine("PASS: two-stage hold progress, translation/cancellation edges, release, reconnect, gesture IDs, stalls and clock regression");
    }

    private static void InitialReleaseAndStageBoundaries()
    {
        var trigger = new LongPressTrigger();
        Check(!trigger.Update(true, true, 0), "Initial held button does not arm");
        Check(!trigger.Update(true, true, 5000), "Initial held button remains disarmed");
        Check(trigger.GestureId == 0 && !trigger.IsHolding && !trigger.CancelRequested, "No gesture before a valid release");
        trigger.Update(true, false, 5010);
        Check(!trigger.Update(true, true, 5100), "First pressed sample begins the first stage");
        Check(trigger.GestureId == 1 && trigger.IsHolding && !trigger.CancelStage, "Fresh press has a new gesture and first circle");
        Near(trigger.Progress, 0, "First circle starts empty");
        Check(!trigger.Update(true, true, 6599) && !trigger.CancelRequested, "1499 ms triggers neither edge");
        Near(trigger.Progress, 1499f / 1500, "First circle just before completion");
        Check(trigger.Update(true, true, 6600) && !trigger.CancelRequested, "1500 ms translates once");
        Check(trigger.IsHolding && trigger.CancelStage && trigger.GestureId == 1, "Second circle belongs to the same gesture");
        Near(trigger.Progress, 0, "Second circle restarts empty at 1500 ms");
        Check(!trigger.Update(true, true, 8099) && !trigger.CancelRequested, "2999 ms does not yet cancel");
        Near(trigger.Progress, 1499f / 1500, "Second circle just before completion");
        Check(!trigger.Update(true, true, 8100) && trigger.CancelRequested, "3000 ms cancels without another translation");
        Check(!trigger.IsHolding && trigger.CancelStage && trigger.GestureId == 1, "Ring disappears after cancellation");
        Near(trigger.Progress, 1, "Completed cancellation circle is full");
    }

    private static void BothEdgesAreOneShot()
    {
        var trigger = new LongPressTrigger();
        trigger.Update(true, false, 0);
        trigger.Update(true, true, 100);
        Check(trigger.Update(true, true, 1600), "First-stage edge");
        Check(!trigger.Update(true, true, 1600) && !trigger.CancelRequested, "Repeated threshold sample does not translate twice");
        Check(!trigger.Update(true, true, 3100) && trigger.CancelRequested, "Second-stage edge");
        Check(!trigger.Update(true, true, 3100) && !trigger.CancelRequested, "Repeated cancellation sample does not cancel twice");
        Check(!trigger.Update(true, true, 10000) && !trigger.CancelRequested && !trigger.IsHolding, "Lingering hold is latched");
        trigger.Update(true, false, 10010);
        Check(!trigger.CancelStage && !trigger.IsHolding, "Release clears the completed circle");
        Near(trigger.Progress, 0, "Release clears progress");
        trigger.Update(true, true, 10020);
        Check(trigger.GestureId == 2, "Repress creates another gesture");
        Check(!trigger.Update(true, true, 11519), "Repress needs its own full first stage");
        Check(trigger.Update(true, true, 11520), "Repress translates after another 1500 ms");
        Check(!trigger.Update(true, true, 13020) && trigger.CancelRequested, "Repress has its own cancellation edge");
    }

    private static void ReleaseAfterTranslationDoesNotCancel()
    {
        var trigger = new LongPressTrigger();
        trigger.Update(true, false, 0);
        trigger.Update(true, true, 100);
        Check(trigger.Update(true, true, 1600), "Translation starts");
        Check(!trigger.Update(true, false, 1700) && !trigger.CancelRequested, "Release after translation never cancels it");
        Check(!trigger.IsHolding && !trigger.CancelStage, "Release hides the second circle");
        Check(!trigger.Update(true, false, 5000) && !trigger.CancelRequested, "Elapsed time after release cannot cause cancellation");
        trigger.Update(true, true, 5100);
        Check(trigger.GestureId == 2 && trigger.IsHolding && !trigger.CancelStage, "New press starts at the first stage");
        Check(trigger.Update(true, true, 6600), "New press translates independently");
        Check(!trigger.Update(true, false, 8100) && !trigger.CancelRequested, "A released sample at 3000 ms does not cancel");
    }

    private static void ShortTapsDoNotAccumulate()
    {
        var trigger = new LongPressTrigger();
        trigger.Update(true, false, 0);
        for (int tap = 0; tap < 4; tap++)
        {
            long start = 100 + tap * 2000;
            Check(!trigger.Update(true, true, start), "Short tap starts without an event");
            Check(!trigger.Update(true, true, start + 1499) && !trigger.CancelRequested, "Short tap remains below the first threshold");
            Check(!trigger.Update(true, false, start + 1500) && !trigger.CancelRequested, "Release at threshold does not fire");
        }
        Check(trigger.GestureId == 4, "Every eligible tap has its own ID");
        trigger.Update(true, true, 9000);
        Check(!trigger.Update(true, true, 10499), "Earlier taps do not count toward a new hold");
        Check(trigger.Update(true, true, 10500), "Full first stage after taps triggers normally");
    }

    private static void InactiveAndResetDisarm()
    {
        var trigger = new LongPressTrigger();
        trigger.Update(true, false, 0);
        trigger.Update(true, true, 100);
        Check(!trigger.Update(false, true, 1000), "Input inactivity interrupts the first stage");
        Check(!trigger.IsHolding && !trigger.CancelRequested, "Inactive input hides the ring without a cancellation edge");
        trigger.Update(false, false, 1100);
        Check(!trigger.Update(true, true, 5000) && trigger.GestureId == 1, "An inactive release cannot rearm");
        trigger.Update(true, false, 5010);
        trigger.Update(true, true, 5020);
        Check(trigger.GestureId == 2 && trigger.Update(true, true, 6520), "Fresh released/held input after reconnect translates");
        trigger.Reset();
        Check(trigger.GestureId == 2 && !trigger.IsHolding && !trigger.CancelRequested && !trigger.CancelStage, "Rebind/reset clears state but preserves the ID");
        Near(trigger.Progress, 0, "Reset clears circle progress");
        Check(!trigger.Update(true, true, 10000) && !trigger.CancelRequested, "Reset while second stage held requires a new release");
        trigger.Update(true, false, 10010);
        trigger.Update(true, true, 10020);
        Check(trigger.GestureId == 3, "Reset never reuses a prior gesture ID");
        Check(trigger.Update(true, true, 11520), "Fresh post-reset hold translates");
        Check(!trigger.Update(false, true, 12000) && !trigger.CancelRequested, "Disconnect during the second stage does not synthesize cancellation");
        Check(!trigger.Update(true, true, 15000) && !trigger.CancelRequested && !trigger.IsHolding, "Held reconnect cannot cancel the old gesture");
    }

    private static void StalledPollCancelsWithoutTranslating()
    {
        var trigger = new LongPressTrigger();
        trigger.Update(true, false, 0);
        trigger.Update(true, true, 100);
        Check(!trigger.Update(true, true, 3100) && trigger.CancelRequested, "A poll skipping both thresholds emits only cancellation");
        Check(trigger.GestureId == 1 && !trigger.IsHolding, "Skipped translation does not restart the gesture");
        Check(!trigger.Update(true, true, 8000) && !trigger.CancelRequested, "No replay after a stalled cancellation");
        trigger.Update(true, false, 8010);
        trigger.Update(true, true, 8020);
        Check(!trigger.Update(true, true, 20000) && trigger.CancelRequested, "A longer stall also emits only cancellation");

        trigger.Update(true, false, 20010);
        trigger.Update(true, true, 20020);
        Check(trigger.Update(true, true, 22270) && !trigger.CancelRequested, "A poll in the second stage still starts translation once");
        Near(trigger.Progress, 0.5f, "Progress reflects elapsed second-stage time after a shorter stall");
    }

    private static void ClockRegressionDisarmsWithoutReusingIds()
    {
        var trigger = new LongPressTrigger();
        trigger.Update(true, false, 1000);
        trigger.Update(true, true, 1100);
        Check(!trigger.Update(true, true, 900) && !trigger.CancelRequested && !trigger.IsHolding, "Clock regression interrupts the first stage");
        Check(!trigger.Update(true, true, 5000) && trigger.GestureId == 1, "Clock catching up cannot resume a disarmed hold");
        trigger.Update(true, false, 5010);
        trigger.Update(true, true, 5020);
        Check(trigger.GestureId == 2 && trigger.Update(true, true, 6520), "New post-regression gesture translates");
        Check(!trigger.Update(true, false, 6500) && !trigger.CancelRequested, "A regressing released sample is not valid arming input");
        Check(!trigger.Update(true, true, 9000) && trigger.GestureId == 2, "Regressing release did not rearm");
        trigger.Update(true, false, 9010);
        trigger.Update(true, true, 9020);
        Check(trigger.GestureId == 3 && trigger.Update(true, true, 10520), "Later valid release yields a fresh ID");
    }

    private static void CustomAndVeryLargeThresholds()
    {
        var trigger = new LongPressTrigger(20);
        trigger.Update(true, false, -100);
        trigger.Update(true, true, -90);
        Check(!trigger.Update(true, true, -71), "Negative-origin clock stays below a custom threshold");
        Check(trigger.Update(true, true, -70), "Custom first threshold");
        Check(!trigger.Update(true, true, -51) && !trigger.CancelRequested, "Custom second threshold not reached");
        Check(!trigger.Update(true, true, -50) && trigger.CancelRequested, "Custom second threshold");

        var huge = new LongPressTrigger(long.MaxValue);
        huge.Update(true, false, long.MinValue);
        huge.Update(true, true, long.MinValue);
        Check(huge.Update(true, true, -1), "Very large first threshold does not overflow");
        Check(!huge.Update(true, true, long.MaxValue - 2) && !huge.CancelRequested, "Very large second threshold remains pending until its boundary");
        Check(!huge.Update(true, true, long.MaxValue - 1) && huge.CancelRequested, "Second threshold comparison avoids multiplying a signed long");

        foreach (long invalid in new long[] { 0, -1 })
        {
            bool rejected = false;
            try { _ = new LongPressTrigger(invalid); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Non-positive thresholds are rejected");
        }
    }

    private static void Near(float actual, float expected, string message) => Check(Math.Abs(actual - expected) < 0.00001f, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("LongPressTrigger test failed: " + message);
    }
}
