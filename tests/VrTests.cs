using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valve.VR;
using Vortice.DXGI;

namespace SteamVrTranslator;

/// <summary>Checks GPU readback conversion and distributed bindings without starting SteamVR.</summary>
public static class VrTests
{
    private static readonly MethodInfo CopyMappedImage = typeof(VrService).GetMethod(
        "CopyMappedImage", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(VrService), "CopyMappedImage");

    public static void Run()
    {
        // Two 8-byte pixel rows with four padding bytes each. Distinct alpha and padding
        // values catch accidental BGRA/RGBA swaps, ignored row pitch and transparent output.
        byte[] rgba =
        [
            255, 0, 0, 0, 0, 255, 0, 5, 101, 102, 103, 104,
            0, 0, 255, 23, 7, 29, 191, 47, 201, 202, 203, 204
        ];
        byte[] bgra =
        [
            0, 0, 255, 0, 0, 255, 0, 5, 101, 102, 103, 104,
            255, 0, 0, 23, 191, 29, 7, 47, 201, 202, 203, 204
        ];
        foreach (var format in new[] { Format.R8G8B8A8_UNorm, Format.R8G8B8A8_UNorm_SRgb })
        {
            using var image = ConvertPixels(rgba, 12, 2, 2, format);
            CheckPixels(image, format.ToString());
        }
        foreach (var format in new[] { Format.B8G8R8A8_UNorm, Format.B8G8R8X8_UNorm_SRgb })
        {
            using var image = ConvertPixels(bgra, 12, 2, 2, format);
            CheckPixels(image, format.ToString());
        }
        Expect<NotSupportedException>(() => ConvertPixels(rgba, 12, 2, 2, Format.R8_UNorm).Dispose(), "Unsupported mirror format");
        Expect<InvalidOperationException>(() => ConvertPixels(rgba, 4, 2, 2, Format.R8G8B8A8_UNorm).Dispose(), "Truncated row pitch");
        CheckBindings();
        CheckSelectionGesture();
        CheckPoseValidation();
        CheckDisconnectedSelectionConfiguration();
        CheckOverlayAnimationState();
        CheckOverlayFailureRecovery();
        CheckOverlayErrorDetails();
        CheckHoldAtlasAnimationState();
        CheckHoldAtlasFailureRecovery();
    }

    private static Bitmap ConvertPixels(byte[] bytes, uint pitch, int width, int height, Format format)
    {
        IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            try { return (Bitmap)CopyMappedImage.Invoke(null, [memory, pitch, width, height, format])!; }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static void CheckPixels(Bitmap image, string format)
    {
        Check(image.Width == 2 && image.Height == 2, format + " dimensions");
        var expected = new[] { Color.FromArgb(255, 255, 0, 0), Color.FromArgb(255, 0, 255, 0),
            Color.FromArgb(255, 0, 0, 255), Color.FromArgb(255, 7, 29, 191) };
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                Check(image.GetPixel(x, y).ToArgb() == expected[y * 2 + x].ToArgb(),
                    $"{format} pixel ({x}, {y}) channels, row pitch and opaque alpha");
    }

    private static void CheckBindings()
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "assets");
        string manifestPath = Path.Combine(assets, "actions.json");
        Check(File.Exists(manifestPath), "Packaged action manifest exists");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        const string actionSet = "/actions/translator";
        var actions = manifest.RootElement.GetProperty("actions").EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("type").GetString()!);
        Check(actions.TryGetValue(actionSet + "/in/translate", out var translateType) && translateType == "boolean", "Translation boolean action exists");
        Check(actions.TryGetValue(actionSet + "/in/nextpage", out var nextType) && nextType == "boolean", "Page boolean action exists");
        Check(actions.TryGetValue(actionSet + "/in/leftpointer", out var leftType) && leftType == "pose", "Left pointer pose action exists");
        Check(actions.TryGetValue(actionSet + "/in/rightpointer", out var rightType) && rightType == "pose", "Right pointer pose action exists");
        Check(manifest.RootElement.GetProperty("action_sets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == actionSet), "Declared action set exists");

        var inputByController = new Dictionary<string, string>
        {
            ["oculus_touch"] = "joystick",
            ["knuckles"] = "thumbstick",
            ["vive_controller"] = "application_menu"
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.RootElement.GetProperty("default_bindings").EnumerateArray())
        {
            string controller = entry.GetProperty("controller_type").GetString()!;
            Check(seen.Add(controller), "No duplicate controller binding");
            Check(inputByController.ContainsKey(controller), "Known controller type: " + controller);
            string relative = entry.GetProperty("binding_url").GetString()!;
            string file = Path.GetFullPath(Path.Combine(assets, relative));
            Check(file.StartsWith(Path.GetFullPath(assets) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Binding stays in assets");
            Check(File.Exists(file), "Packaged binding exists: " + relative);
            using var binding = JsonDocument.Parse(File.ReadAllText(file));
            Check(binding.RootElement.GetProperty("controller_type").GetString() == controller, "Binding controller agrees with manifest");
            Check(binding.RootElement.GetProperty("action_manifest_version").GetInt32() == manifest.RootElement.GetProperty("version").GetInt32(), "Binding manifest version agrees");
            var boundActions = new HashSet<string>();
            foreach (var source in binding.RootElement.GetProperty("bindings").GetProperty(actionSet).GetProperty("sources").EnumerateArray())
            {
                string output = source.GetProperty("inputs").GetProperty("click").GetProperty("output").GetString()!;
                Check(actions.ContainsKey(output), "Binding output names a declared action");
                Check(boundActions.Add(output), "Each default action has one source");
                bool translate = output.EndsWith("/translate", StringComparison.Ordinal);
                string expectedPath = translate ? controller switch
                {
                    "oculus_touch" => "/user/hand/left/input/x",
                    "knuckles" => "/user/hand/left/input/a",
                    _ => "/user/hand/right/input/application_menu"
                } : $"/user/hand/left/input/{inputByController[controller]}";
                Check(source.GetProperty("path").GetString() == expectedPath, "Controller-specific source path: " + controller);
                Check(source.GetProperty("mode").GetString() == "button", "Click binding has button mode");
            }
            foreach (var pose in binding.RootElement.GetProperty("bindings").GetProperty(actionSet).GetProperty("poses").EnumerateArray())
            {
                string output = pose.GetProperty("output").GetString()!;
                Check(actions.TryGetValue(output, out var type) && type == "pose", "Pose binding names a declared pose action");
                Check(boundActions.Add(output), "Pose action has one default source");
                string expectedPath = output == actionSet + "/in/leftpointer" ? "/user/hand/left/pose/tip" : "/user/hand/right/pose/tip";
                Check(pose.GetProperty("path").GetString() == expectedPath, "Controller uses the pointer tip for the correct hand");
            }
            Check(boundActions.SetEquals(actions.Keys), "Default binding covers buttons and both pointer poses");
        }
        Check(seen.SetEquals(inputByController.Keys), "Touch, Index and Vive default bindings included");
    }

    private static HmdMatrix34_t Pose(float x = 0, float y = 0, float z = 0) =>
        new() { m0 = 1, m5 = 1, m10 = 1, m3 = x, m7 = y, m11 = z };

    private static void CheckSelectionGesture()
    {
        var gesture = new SpatialSelectionGesture();
        var head = Pose(); var left = Pose(-0.3f, 0.2f, -0.2f); var right = Pose(0.3f, -0.2f, -0.2f);
        const ETrackingUniverseOrigin standing = ETrackingUniverseOrigin.TrackingUniverseStanding;
        SpatialSelectionUpdate Tick(bool pressed, bool active = true, bool valid = true, long epoch = 7,
            ETrackingUniverseOrigin origin = standing, HmdMatrix34_t? h = null, HmdMatrix34_t? r = null) =>
            gesture.Update(active, pressed, valid, h ?? head, left, r ?? right, origin, epoch, 1.2f);

        Check(Tick(true).Started is null && gesture.Current is null, "Held input at startup cannot start selection");
        Tick(false);
        var start = Tick(true);
        Check(start.Started is < 0 && gesture.IsSelecting && gesture.Current.HasValue, "Fresh press creates a distinct negative gesture ID and rectangle");
        long firstId = start.Started!.Value;
        Check(Tick(true, h: Pose(0, 0, 0.5f)).Started is null, "Holding emits no duplicate start");
        Check(MathF.Abs(gesture.Current!.Value.Anchor.Transform.m11 + 1.2f) < 0.001f, "Moving the head does not move the frozen plane");
        var commit = Tick(false);
        Check(commit.Committed?.GestureId == firstId && !gesture.IsSelecting && gesture.Current is null, "Release commits exactly one frozen selection and clears preview");
        Check(Tick(false).Committed is null, "Repeated released input never commits again");

        start = Tick(true);
        Check(start.Started < firstId, "Subsequent gesture IDs cannot collide with previous jobs");
        Tick(true, r: left);
        Check(gesture.Current is null, "Degenerate rectangle clears prior valid corners");
        var badRelease = Tick(false, r: left);
        Check(badRelease.Committed is null && badRelease.Aborted is not null, "Invalid release cannot commit the previous valid rectangle");

        Tick(true);
        var inactive = Tick(true, active: false);
        Check(inactive.Aborted is not null && gesture.Current is null && !gesture.IsSelecting, "Input inactivity aborts and clears selection");
        Check(Tick(true).Started is null, "Restoring input while held cannot restart selection");
        Tick(false, active: false);
        Check(Tick(true).Started is null, "Inactive release does not rearm");
        Tick(false);
        start = Tick(true);
        Check(start.Started is not null, "Active release rearms after input interruption");
        long beforeReset = start.Started!.Value;
        Check(Tick(true, valid: false).Aborted is not null, "Lost tracking aborts immediately");
        Check(Tick(true).Started is null, "Restored tracking while held cannot restart");
        Tick(false);
        Check(Tick(true).Started < beforeReset, "Tracking reset never reuses a gesture ID");
        Check(Tick(false, valid: false).Committed is null && !gesture.IsSelecting, "Lost tracking on release cannot commit stale points");

        Tick(false); Tick(true);
        Check(Tick(true, epoch: 8).Aborted is not null && gesture.Current is null, "Tracking epoch change aborts the old selection");
        Tick(false); Tick(true);
        Check(Tick(true, origin: ETrackingUniverseOrigin.TrackingUniverseSeated).Aborted is not null, "Changing tracking universe aborts selection");
        Tick(false); Tick(true);
        Check(gesture.Abort("disabled").Aborted == "disabled", "Disabling selection reports active interruption once");
        Check(gesture.Abort("disabled").Aborted is null && Tick(true).Started is null, "Repeated disable is quiet and cannot rearm a held button");
        Tick(false);
        Check(Tick(true, valid: false).Aborted is not null, "Unavailable controllers on a fresh press produce a useful failure");
        Check(Tick(true, valid: false).Aborted is null, "Unavailable controller failure does not repeat every poll");
    }

    private static void CheckPoseValidation()
    {
        var pose = new TrackedDevicePose_t { mDeviceToAbsoluteTracking = Pose(), bDeviceIsConnected = true,
            bPoseIsValid = true, eTrackingResult = ETrackingResult.Running_OK };
        Check(VrService.IsUsablePose(pose), "A connected rigid 6DOF pose is accepted");
        pose.bPoseIsValid = false;
        Check(!VrService.IsUsablePose(pose), "An invalid pose is rejected");
        pose.bPoseIsValid = true; pose.bDeviceIsConnected = false;
        Check(!VrService.IsUsablePose(pose), "A disconnected pose is rejected");
        pose.bDeviceIsConnected = true; pose.eTrackingResult = ETrackingResult.Fallback_RotationOnly;
        Check(!VrService.IsUsablePose(pose), "Rotation-only fallback cannot place a world rectangle");
        pose.eTrackingResult = ETrackingResult.Running_OK;
        pose.mDeviceToAbsoluteTracking = default;
        Check(!VrService.IsUsablePose(pose), "A zero transform cannot place a world rectangle");
        var invalid = Pose(); invalid.m3 = float.NaN; pose.mDeviceToAbsoluteTracking = invalid;
        Check(!VrService.IsUsablePose(pose), "Non-finite tracking is rejected");

        Check(VrService.IsCapturePoseStable(Pose(), Pose(0.005f)), "Small readback movement is tolerated");
        Check(!VrService.IsCapturePoseStable(Pose(), Pose(0.03f)), "Large capture translation is rejected");
        var rotated = Pose(); float angle = 3 * MathF.PI / 180;
        rotated.m0 = rotated.m10 = MathF.Cos(angle); rotated.m2 = MathF.Sin(angle); rotated.m8 = -MathF.Sin(angle);
        Check(!VrService.IsCapturePoseStable(Pose(), rotated), "Large capture rotation is rejected");
        Check(!VrService.IsCapturePoseStable(Pose(), invalid), "Non-finite capture pose is rejected");
    }

    private static void CheckDisconnectedSelectionConfiguration()
    {
        using var service = new VrService();
        Check(!service.SelectionMode && !service.SelectionEnabled && service.CurrentSelection is null, "Selection defaults never enable controllers or connect runtime");
        service.SelectionMode = true; service.SelectionEnabled = true;
        Check(!service.IsConnected && !service.HoldVisible, "Configuring selection does not start SteamVR");
        service.SelectionDistance = 100;
        Check(service.SelectionDistance == 4, "Selection distance stays within geometry bounds");
        service.SelectionDistance = 0;
        Check(service.SelectionDistance == 0.4f, "Near selection distance stays within geometry bounds");
        Expect<ArgumentOutOfRangeException>(() => service.SelectionDistance = float.NaN, "Non-finite selection distance");
        Check(!service.IsAnchorValid(new(Pose(), 1, 1, ETrackingUniverseOrigin.TrackingUniverseStanding, 1)), "Disconnected anchors cannot be displayed");
    }

    private static void CheckOverlayAnimationState()
    {
        var state = new OverlayPresentationState();
        var fake = new FakeOverlay();
        var placement = new HeadRelativeOverlayPlacement(.12f, 1.2f, -.1f);
        for (int frame = 0; frame < 100; frame++)
            state.Update(placement, fake.Upload, fake.Position, fake.Show);
        Check(fake.UploadCount == 100 && fake.PositionCount == 1 && fake.ShowCount == 1 && fake.HideCount == 0,
            "Animating pixels does not repeatedly reposition, show, hide or clear the overlay");
        Check(state.IsVisible, "Successful show is cached");
        state.Hide(fake.Hide);
        state.Hide(fake.Hide);
        Check(!state.IsVisible && fake.HideCount == 1, "Repeated hidden frames do not repeat native hide");
        state.Update(placement, fake.Upload, fake.Position, fake.Show);
        Check(fake.ShowCount == 2 && fake.PositionCount == 1 && state.IsVisible,
            "A hidden ring becomes visible again without moving its unchanged placement");
        state.Update(placement with { Distance = 2 }, fake.Upload, fake.Position, fake.Show);
        Check(fake.PositionCount == 2 && fake.ShowCount == 2, "Changed placement updates geometry without toggling visibility");
        state.Reset();
        Check(!state.IsVisible, "Disconnect resets visible state");
        state.Update(placement with { Distance = 2 }, fake.Upload, fake.Position, fake.Show);
        Check(fake.PositionCount == 3 && fake.ShowCount == 3, "Reconnect resubmits placement and visibility for a new native handle");
    }

    private static void CheckOverlayFailureRecovery()
    {
        var placement = new HeadRelativeOverlayPlacement(.12f, 1.2f, -.1f);
        foreach (string failedOperation in new[] { "upload", "position", "show" })
        {
            var state = new OverlayPresentationState();
            var fake = new FakeOverlay { FailOnce = failedOperation };
            Expect<VrOverlayException>(() => state.Update(placement, fake.Upload, fake.Position, fake.Show),
                "Overlay " + failedOperation + " failure");
            Check(!state.IsVisible, "Failed first presentation is not falsely marked visible");
            state.Update(placement, fake.Upload, fake.Position, fake.Show);
            Check(state.IsVisible, "A transient " + failedOperation + " error can recover on the next frame");
            Check(fake.PositionCount == (failedOperation == "position" ? 2 : 1),
                "Only successfully positioned geometry is cached after " + failedOperation + " failure");
            Check(fake.ShowCount == (failedOperation == "show" ? 2 : 1),
                "Failed native show remains eligible for retry");
        }

        var visibleState = new OverlayPresentationState();
        var visibleFake = new FakeOverlay();
        visibleState.Update(placement, visibleFake.Upload, visibleFake.Position, visibleFake.Show);
        visibleFake.FailOnce = "upload";
        Expect<VrOverlayException>(() => visibleState.Update(placement, visibleFake.Upload, visibleFake.Position, visibleFake.Show), "Visible upload error");
        Check(visibleState.IsVisible && visibleFake.ShowCount == 1 && visibleFake.HideCount == 0,
            "Pixel upload failure does not hide an already displayed ring");
        visibleState.Update(placement, visibleFake.Upload, visibleFake.Position, visibleFake.Show);
        Check(visibleFake.ShowCount == 1, "Recovered pixel upload does not toggle existing visibility");
        visibleFake.FailOnce = "hide";
        Expect<VrOverlayException>(() => visibleState.Hide(visibleFake.Hide), "Native hide error");
        Check(visibleState.IsVisible, "Failed hide preserves state for retry rather than abandoning a visible overlay");
        visibleState.Hide(visibleFake.Hide);
        Check(!visibleState.IsVisible && visibleFake.HideCount == 2, "Transient hide error can be retried successfully");
        visibleState.Update(placement, visibleFake.Upload, visibleFake.Position, visibleFake.Show);
        Check(visibleState.IsVisible && visibleFake.ShowCount == 2, "A recovered hide does not prevent the next gesture from showing");
    }

    private static void CheckOverlayErrorDetails()
    {
        var checkOverlay = typeof(VrService).GetMethod("CheckOverlay", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(VrService), "CheckOverlay");
        checkOverlay.Invoke(null, [EVROverlayError.None, "测试圆环上传"]);
        try { checkOverlay.Invoke(null, [EVROverlayError.RequestFailed, "测试圆环上传"]); }
        catch (TargetInvocationException wrapper) when (wrapper.InnerException is VrOverlayException error)
        {
            Check(error.Operation == "测试圆环上传" && error.Error == EVROverlayError.RequestFailed,
                "Native overlay failure preserves the operation and exact error code for UI isolation");
            Check(error.Message.Contains(error.Operation, StringComparison.Ordinal)
                && error.Message.Contains(nameof(EVROverlayError.RequestFailed), StringComparison.Ordinal), "Overlay error text is actionable");
            return;
        }
        throw new Exception("VrTests: Native overlay error did not use VrOverlayException");
    }

    private static void CheckHoldAtlasAnimationState()
    {
        var atlas = new HoldAtlasPresentationState();
        var presentation = new OverlayPresentationState();
        var native = new FakeOverlay();
        var placement = new HeadRelativeOverlayPlacement(.12f, 1.2f, -.1f);
        int boundsCalls = 0, lastBucket = -1, readyQueries = 0;
        void Frame(bool cancel, int bucket)
        {
            Check(atlas.Prepare(native.Upload, () => { readyQueries++; return true; }), "Combined atlas is ready before presentation");
            presentation.Update(placement,
                () => atlas.Update(cancel, bucket, index => { boundsCalls++; lastBucket = index; }),
                native.Position, native.Show);
        }

        for (int bucket = 0; bucket <= 60; bucket++)
        {
            Frame(false, bucket);
            Frame(false, bucket);
        }
        Check(native.UploadCount == 1 && boundsCalls == 61 && lastBucket == 60,
            "An entire translate ring uploads one combined atlas and only changes UV bounds per distinct bucket");
        Check(readyQueries == 1, "A loaded texture does not query native readiness every animation frame");
        Check(native.PositionCount == 1 && native.ShowCount == 1 && native.HideCount == 0,
            "Atlas animation does not toggle visibility or repeatedly move the ring");
        presentation.Hide(native.Hide);
        Frame(false, 0);
        Check(native.UploadCount == 1 && native.ShowCount == 2 && boundsCalls == 62,
            "Release and a new gesture reuse the uploaded combined atlas");
        Frame(true, 0);
        Check(native.UploadCount == 1 && boundsCalls == 63 && atlas.CancelStage == true && atlas.Bucket == 0,
            "Stage transition only changes UV bounds, including at the same bucket");
        for (int bucket = 1; bucket <= 60; bucket++) Frame(true, bucket);
        Check(native.UploadCount == 1 && native.ShowCount == 2 && readyQueries == 1, "Both ring stages animate without texture replacement or readiness polling");
        atlas.Reset(); // Disconnect or use of the legacy bitmap path invalidates the native texture cache.
        presentation.Reset();
        Frame(true, 60);
        Check(native.UploadCount == 2 && native.PositionCount == 2 && native.ShowCount == 3 && readyQueries == 2,
            "A new native handle receives the atlas, bounds, position and show again");
        int uploadsBeforeInvalid = native.UploadCount;
        Expect<ArgumentOutOfRangeException>(() => Frame(false, 61), "Out-of-range atlas bucket");
        Check(native.UploadCount == uploadsBeforeInvalid, "Invalid bucket cannot replace an existing texture");
    }

    private static void CheckHoldAtlasFailureRecovery()
    {
        var atlas = new HoldAtlasPresentationState();
        int uploads = 0, boundsCalls = 0;
        void Upload() => uploads++;
        void Bounds(int _) => boundsCalls++;
        Expect<VrOverlayException>(() => atlas.Prepare(
            () => throw new VrOverlayException("upload-atlas", EVROverlayError.RequestFailed), () => true), "Atlas upload failure");
        Check(!atlas.IsUploaded && !atlas.IsReady && atlas.CancelStage is null && atlas.Bucket is null,
            "Failed atlas upload is not falsely cached or made presentable");
        Check(!atlas.Prepare(Upload, () => false) && uploads == 1 && atlas.IsUploaded && !atlas.IsReady,
            "Accepted asynchronous upload is cached while the native texture is still unavailable");
        Check(!atlas.Prepare(Upload, () => false) && uploads == 1, "Pending native texture is not uploaded again on each poll");
        Expect<InvalidOperationException>(() => atlas.Update(false, 0, Bounds), "UV presentation before texture readiness");
        Check(boundsCalls == 0, "Pending texture cannot submit bounds for a visible frame");
        Expect<VrOverlayException>(() => atlas.Prepare(Upload,
            () => throw new VrOverlayException("atlas-readiness", EVROverlayError.RequestFailed)), "Native readiness query failure");
        Check(atlas.IsUploaded && !atlas.IsReady && uploads == 1, "Readiness query failure preserves accepted upload for retry");
        Check(atlas.Prepare(Upload, () => true) && uploads == 1, "Native completion enables presentation without another upload");
        atlas.Update(false, 10, Bounds);
        Expect<VrOverlayException>(() => atlas.Update(true, 0,
            _ => throw new VrOverlayException("atlas-bounds", EVROverlayError.RequestFailed)), "Stage bounds failure");
        Check(atlas.CancelStage == false && atlas.Bucket == 10 && boundsCalls == 1 && atlas.IsReady,
            "Failed stage bounds preserve the last successful frame and ready texture");
        atlas.Update(true, 0, Bounds);
        Check(uploads == 1 && boundsCalls == 2 && atlas.CancelStage == true, "Stage change can recover without reloading texture or resetting input");

        atlas.Reset();
        Check(!atlas.IsUploaded && !atlas.IsReady && atlas.CancelStage is null && atlas.Bucket is null,
            "Disconnect and legacy texture changes invalidate all combined-atlas state");
        atlas.Prepare(Upload, () => true);
        Expect<VrOverlayException>(() => atlas.Update(false, 20,
            _ => throw new VrOverlayException("atlas-bounds", EVROverlayError.RequestFailed)), "Atlas bounds failure");
        Check(atlas.IsReady && atlas.CancelStage is null && atlas.Bucket is null, "Successful upload survives a subsequent bounds failure");
        atlas.Update(false, 20, Bounds);
        Check(uploads == 2 && boundsCalls == 3 && atlas.Bucket == 20,
            "Retrying failed bounds does not reload the atlas and introduce a new texture transition");
        atlas.Update(false, 20, Bounds);
        Check(uploads == 2 && boundsCalls == 3, "Recovered frame does not repeat native work for an unchanged bucket");

        atlas.Reset();
        Expect<VrOverlayException>(() => atlas.Prepare(Upload, () =>
        {
            atlas.Reset(); // Native loading timeout invalidates the accepted but unusable upload.
            throw new VrOverlayException("atlas-timeout", EVROverlayError.TimedOut);
        }), "Asynchronous atlas loading timeout");
        Check(!atlas.IsUploaded && !atlas.IsReady, "Loading timeout leaves the atlas eligible for a paced retry");
        Check(atlas.Prepare(Upload, () => true) && uploads == 4, "Next attempt after timeout uploads again and can recover");
    }

    private sealed class FakeOverlay
    {
        public int UploadCount, PositionCount, ShowCount, HideCount;
        public string? FailOnce;
        public void Upload() { UploadCount++; MaybeFail("upload"); }
        public void Position(HeadRelativeOverlayPlacement _) { PositionCount++; MaybeFail("position"); }
        public void Show() { ShowCount++; MaybeFail("show"); }
        public void Hide() { HideCount++; MaybeFail("hide"); }
        private void MaybeFail(string operation)
        {
            if (FailOnce != operation) return;
            FailOnce = null;
            throw new VrOverlayException(operation, EVROverlayError.RequestFailed);
        }
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("VrTests: " + message + " was accepted");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception("VrTests: " + message);
    }
}
