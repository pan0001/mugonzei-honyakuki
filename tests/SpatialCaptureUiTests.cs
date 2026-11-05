using System.Reflection;
using Valve.VR;

namespace SteamVrTranslator;

public static class SpatialCaptureUiTests
{
    public static void Run()
    {
        SnapshotAndQueueOwnership();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainForm? form = null;
            try
            {
                form = new MainForm(true);
                DraftControlsAndSavedInstructions(form);
                SelectionSupersedesCapture(form);
                FixedExpiryAndVisibility(form);
                InvalidatedAnchorsPreserveKeyboardWork(form);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (form is not null) { Invoke(form, "DisposeServices"); form.Dispose(); }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Spatial capture UI integration failed", failure);
        Console.WriteLine("PASS spatial capture snapshot ownership, latest queue, mode controls, cancellation scope and fixed placement expiry (no VR/network)");
    }

    private static SpatialAnchor Anchor(long epoch = 10) => new(new HmdMatrix34_t
    { m0 = 1, m5 = 1, m10 = 1, m11 = -1.2f }, 1, .5f, ETrackingUniverseOrigin.TrackingUniverseStanding, epoch);

    private static SelectionCaptureSnapshot Snapshot(Color color, SpatialAnchor anchor)
    {
        using var image = new Bitmap(32, 16);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
        byte[] png = VisionImage.Prepare(image);
        var snapshot = new SelectionCaptureSnapshot(png, anchor);
        Array.Fill(png, (byte)0); // The producer relinquishes this buffer after committing.
        return snapshot;
    }

    private static void SnapshotAndQueueOwnership()
    {
        var first = Snapshot(Color.Red, Anchor());
        using (var decoded = first.DecodeImage())
        {
            Check(decoded.Width == 32 && decoded.Height == 16 && decoded.GetPixel(8, 8).R > 250,
                "A committed image survives mutation of its producer buffer");
            decoded.SetPixel(8, 8, Color.Black);
        }
        using (var again = first.DecodeImage())
            Check(again.GetPixel(8, 8).R > 250, "Preview mutation cannot alter the queued OCR snapshot");
        bool rejected = false;
        try { _ = new SelectionCaptureSnapshot([1, 2, 3], Anchor()); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Only bounded, complete PNG captures can enter the queue");

        var second = Snapshot(Color.Blue, Anchor(11));
        var queue = new LatestCaptureRequests();
        Check(queue.Enqueue(new CaptureRequest(false, "first", -1, first)), "First selection owns the queue");
        Check(queue.TryTake(out var active) && ReferenceEquals(active.Selection, first), "Active request keeps its own pixels and anchor");
        queue.Enqueue(new CaptureRequest(false, "superseded", -2, first));
        queue.Enqueue(new CaptureRequest(false, "latest", -3, second));
        Check(queue.TryTake(out var latest) && latest.GestureId == -3 && latest.Selection?.Anchor.Epoch == 11
            && ReferenceEquals(latest.Selection, second), "Latest-wins preserves matching gesture, frame and world placement");
        Check(!queue.TryTake(out _), "Superseded image snapshots do not replay");
        queue.Enqueue(new CaptureRequest(false, "keyboard"));
        Check(!queue.CancelSpatialPending() && queue.HasPending, "Tracking changes preserve unrelated keyboard requests");
        queue.TryTake(out _);
        queue.Enqueue(new CaptureRequest(false, "selection", -4, first));
        Check(queue.CancelSpatialPending() && !queue.HasPending, "Tracking changes remove queued fixed selections");
        queue.Finish();
    }

    private static void DraftControlsAndSavedInstructions(MainForm form)
    {
        var enabled = Get<CheckBox>(form, "spatialSelection");
        var distance = Get<NumericUpDown>(form, "selectionDistance");
        var help = Get<Label>(form, "workspaceInputHelp");
        var vr = Get<VrService>(form, "vr");
        Check(!enabled.Checked && !distance.Enabled && Get<Label>(form, "selectionHelp").Text.Contains("1.5")
            && help.Text.Contains(Get<AppSettings>(form, "settings").Hotkey), "Offline automatic mode shows the desktop shortcut while preserving the default long-press VR gesture");
        // Simulate a connected runtime for these UI-only checks without calling OpenVR.
        var connection = typeof(VrService).GetProperty(nameof(VrService.IsConnected))!;
        connection.SetValue(vr, true);
        try
        {
            Invoke(form, "LoadSettings");
            Check(!vr.SelectionMode && help.Text.Contains("1.5"), "The connected workspace explains the saved long-press gesture");
            enabled.Checked = true;
            Check(distance.Enabled && Get<Label>(form, "selectionHelp").Text.Contains("双手柄"), "Draft selection mode explains its controller gesture");
            Check(help.Text.Contains("1.5"), "An unsaved interaction draft cannot claim to change the active VR gesture");
            Get<AppSettings>(form, "settings").SpatialSelectionEnabled = true;
            Invoke(form, "LoadSettings");
            Check(help.Text.Contains("松开") && vr.SelectionMode,
                "Loaded saved mode changes the active VR mode and workspace instructions together");
        }
        finally { connection.SetValue(vr, false); }
        Check(distance.Minimum == .4m && distance.Maximum == 4m, "Selection distance is constrained to supported geometry");
    }

    private static void SelectionSupersedesCapture(MainForm form)
    {
        using var oldCapture = new CancellationTokenSource();
        using var oldInference = new CancellationTokenSource();
        Set(form, "selectionCapture", oldCapture);
        Set(form, "selectionGestureId", -1L);
        Set(form, "currentRequest", oldInference);
        var queue = Get<LatestCaptureRequests>(form, "captureRequests");
        queue.Enqueue(new CaptureRequest(false, "queued old", -1, Snapshot(Color.Red, Anchor())));
        Invoke(form, "BeginSelection", -2L);
        Check(oldCapture.IsCancellationRequested && oldInference.IsCancellationRequested && !queue.HasPending,
            "A new drag cancels older snapshot capture, inference and pending work");
        Check(Get<bool>(form, "selectionActive") && Get<long>(form, "selectionGestureId") == -2,
            "The new drag alone owns selection state");
        Task stale = (Task)Invoke(form, "CommitSelectionAsync", new SpatialSelection(Anchor(), -1))!;
        Check(stale.IsCompletedSuccessfully && Get<long>(form, "selectionGestureId") == -2,
            "An old release cannot capture the new selection or start a model request");
        string status = Get<Label>(form, "status").Text;
        form.ReportCaptureFailure(new InvalidOperationException("late failure"), oldInference.Token);
        Check(Get<Label>(form, "status").Text == status, "An old cancelled task cannot replace current selection guidance");
        Invoke(form, "CancelSelectionCapture");
        Set(form, "currentRequest", null);
        queue.Finish();
    }

    private static void FixedExpiryAndVisibility(MainForm form)
    {
        SpatialAnchor anchor = Anchor();
        Get<AppSettings>(form, "settings").DisplaySeconds = 20;
        Set(form, "pagesAnchor", anchor); Set(form, "overlayAnchor", anchor);
        Invoke(form, "ResetExpiry");
        Check(Get<long>(form, "hideAt") == 0, "Fixed selections do not inherit ordinary subtitle expiry");
        Invoke(form, "HideOverlay");
        Check(Get<SpatialAnchor?>(form, "pagesAnchor") == anchor && Get<SpatialAnchor?>(form, "overlayAnchor") is null,
            "Hiding keeps the saved page placement for later restoration without attaching to the head");
        Invoke(form, "ResetExpiry");
        Check(Get<long>(form, "hideAt") > Environment.TickCount64, "Ordinary view-relative subtitles retain their timeout");
        Set(form, "hideAt", 0L);
        Set(form, "pagesAnchor", null);
    }

    private static void InvalidatedAnchorsPreserveKeyboardWork(MainForm form)
    {
        using var spatial = new CancellationTokenSource();
        Set(form, "currentRequest", spatial); Set(form, "activeAnchor", Anchor());
        Set(form, "pagesAnchor", Anchor()); Set(form, "overlayAnchor", Anchor());
        Get<List<Bitmap>>(form, "pages").Add(new Bitmap(4, 4));
        var queue = Get<LatestCaptureRequests>(form, "captureRequests");
        queue.Enqueue(new CaptureRequest(false, "keyboard"));
        form.InvalidateSpatialAnchors();
        Check(spatial.IsCancellationRequested && Get<List<Bitmap>>(form, "pages").Count == 0
            && Get<SpatialAnchor?>(form, "pagesAnchor") is null && Get<SpatialAnchor?>(form, "overlayAnchor") is null,
            "Invalid coordinates cancel inference and clear fixed results instead of falling back to head placement");
        Check(queue.TryTake(out var keyboard) && keyboard.Source == "keyboard", "Unrelated queued keyboard capture survives tracking reset");
        queue.Finish();
        using var ordinary = new CancellationTokenSource();
        Set(form, "currentRequest", ordinary); Set(form, "activeAnchor", null);
        Get<List<Bitmap>>(form, "pages").Add(new Bitmap(4, 4));
        form.InvalidateSpatialAnchors();
        Check(!ordinary.IsCancellationRequested && Get<List<Bitmap>>(form, "pages").Count == 1,
            "A tracking-space reset does not cancel a normal screenshot translation or erase its cached pages");
        Invoke(form, "AbortSelection", "无法读取双手柄指向");
        Check(!ordinary.IsCancellationRequested && Get<Label>(form, "status").Text.Contains("无法读取双手柄指向"),
            "Missing initial pose bindings produce a visible explanation without cancelling unrelated text work");
        Set(form, "currentRequest", null);
    }

    private static object? Invoke(MainForm form, string name, params object?[] args) =>
        typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);
    private static void Set(MainForm form, string name, object? value) =>
        typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, value);
    private static T Get<T>(MainForm form, string name) =>
        (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
}
