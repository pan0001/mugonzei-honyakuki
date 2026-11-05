using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valve.VR;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SteamVrTranslator;

/// <summary>OpenVR and graphics calls are confined to the owning UI thread.</summary>
public sealed class VrService : IDisposable
{
    public const string AppKey = "local.steamvr.translator";
    private const string ActionSetPath = "/actions/translator";
    // One immutable combined atlas (~9 MiB), shared until process exit. Construction
    // starts only on first preparation; no OpenVR calls occur on the worker thread.
    private static readonly Lazy<Task<HoldProgressAtlas>> SharedHoldAtlas = new(CreateHoldAtlasTask);
    private CVRSystem? _system;
    private CVRCompositor? _compositor;
    private CVROverlay? _overlay;
    private CVRInput? _input;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IntPtr _mirrorView;
    private ulong _overlayHandle;
    private ulong _holdOverlayHandle;
    private ulong _selectionOverlayHandle;
    private ulong _actionSet;
    private ulong _translateAction;
    private ulong _nextPageAction;
    private ulong _leftPointerAction;
    private ulong _rightPointerAction;
    private ulong _lastTranslateOrigin;
    private ulong _lastLeftPointerOrigin;
    private ulong _lastRightPointerOrigin;
    private bool? _lastDiagnosticInputActive;
    private bool? _lastDiagnosticPressed;
    private EVRInputError _lastActionStateError;
    private EVRInputError _lastDigitalInputError;
    private readonly LongPressTrigger _translateHold = new(1500);
    private readonly OverlayPresentationState _holdPresentation = new();
    private readonly HoldAtlasPresentationState _holdAtlasPresentation = new();
    private long _holdAtlasUploadedAt;
    private readonly SpatialSelectionGesture _selectionGesture = new();
    private readonly TrackedDevicePose_t[] _trackingPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private bool _selectionMode;
    private bool _selectionEnabled;
    private float _selectionDistance = 1.2f;
    private long _spatialEpoch = 1;
    private ETrackingUniverseOrigin _trackingOrigin = ETrackingUniverseOrigin.TrackingUniverseStanding;
    private Bitmap? _selectionFrameBitmap;
    private Size _selectionFrameSize;
    private bool _resultIsSpatial;
    private bool _initialized;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly VRActiveActionSet_t[] _activeSets = new VRActiveActionSet_t[1];
    public bool IsConnected { get; private set; }
    public string Status { get; private set; } = "尚未连接 SteamVR";
    public bool HoldVisible => IsConnected && !_selectionMode && _translateHold.IsHolding;
    public float HoldProgress => _translateHold.Progress;
    public bool HoldCancelStage => _translateHold.CancelStage;
    public long HoldGestureId => _translateHold.GestureId;
    public event Action? TranslatePressed;
    public event Action<long>? TranslationGestureStarted;
    public event Action<long>? TranslationGestureCancelled;
    public event Action? NextPagePressed;
    public event Action<long>? SelectionStarted;
    public event Action<SpatialSelection>? SelectionCommitted;
    public event Action<string>? SelectionAborted;
    public event Action? SpatialAnchorsInvalidated;
    public SpatialSelection? CurrentSelection => _selectionGesture.Current;

    internal void ResetTranslationInput()
    {
        AssertThread();
        _translateHold.Reset();
        AbortSelection("配置已结束，请松开按键后重新框选。");
    }

    public bool SelectionMode
    {
        get => _selectionMode;
        set
        {
            AssertThread();
            if (_selectionMode == value) return;
            _selectionMode = value;
            _translateHold.Reset();
            AbortSelection("截图方式已切换，请松开按键后重新框选。");
        }
    }
    public bool SelectionEnabled
    {
        get => _selectionEnabled;
        set
        {
            AssertThread();
            if (_selectionEnabled == value) return;
            _selectionEnabled = value;
            AbortSelection("手柄框选已暂停，请松开按键后重新框选。");
        }
    }
    public float SelectionDistance
    {
        get => _selectionDistance;
        set
        {
            AssertThread();
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _selectionDistance = Math.Clamp(value, 0.4f, 4f);
        }
    }

    public bool Connect()
    {
        AssertThread();
        if (IsConnected) return true;
        Disconnect();
        try
        {
            if (SteamVrPresence.ReadServerPid() == 0)
            {
                Status = "SteamVR 未就绪，当前可使用桌面翻译。";
                return false;
            }
            var error = EVRInitError.None;
            // Valve's background app type also prevents a launch if vrserver exits
            // after the process probe. It can use the overlay and input interfaces.
            _system = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Background);
            if (error != EVRInitError.None || _system is null)
                throw new InvalidOperationException($"SteamVR 初始化失败：{error}。请先启动 SteamVR 并连接头显。");
            _initialized = true;
            _compositor = OpenVR.Compositor ?? throw new InvalidOperationException("SteamVR 合成器不可用。");
            _overlay = OpenVR.Overlay ?? throw new InvalidOperationException("SteamVR 叠层接口不可用。");
            _input = OpenVR.Input ?? throw new InvalidOperationException("SteamVR 输入接口不可用。");

            RegisterApplication();
            // SteamVR requires this before the first event poll or action-state update.
            CheckInput(_input.SetActionManifestPath(Path.Combine(AppContext.BaseDirectory, "assets", "actions.json")), "加载手柄按键配置");
            CheckInput(_input.GetActionSetHandle(ActionSetPath, ref _actionSet), "获取动作集");
            CheckInput(_input.GetActionHandle(ActionSetPath + "/in/translate", ref _translateAction), "获取翻译动作");
            CheckInput(_input.GetActionHandle(ActionSetPath + "/in/nextpage", ref _nextPageAction), "获取翻页动作");
            CheckInput(_input.GetActionHandle(ActionSetPath + "/in/leftpointer", ref _leftPointerAction), "获取左手指向动作");
            CheckInput(_input.GetActionHandle(ActionSetPath + "/in/rightpointer", ref _rightPointerAction), "获取右手指向动作");
            _activeSets[0] = new VRActiveActionSet_t { ulActionSet = _actionSet, nPriority = 0 };
            CheckOverlay(_overlay.CreateOverlay(AppKey + ".result", "SteamVR 翻译结果", ref _overlayHandle), "创建翻译叠层");
            CheckOverlay(_overlay.SetOverlayInputMethod(_overlayHandle, VROverlayInputMethod.None), "设置叠层输入");
            CheckOverlay(_overlay.CreateOverlay(AppKey + ".hold_progress", "SteamVR 长按进度", ref _holdOverlayHandle), "创建长按进度叠层");
            CheckOverlay(_overlay.SetOverlayInputMethod(_holdOverlayHandle, VROverlayInputMethod.None), "设置长按进度叠层输入");
            CheckOverlay(_overlay.CreateOverlay(AppKey + ".selection", "SteamVR 框选区域", ref _selectionOverlayHandle), "创建框选叠层");
            CheckOverlay(_overlay.SetOverlayInputMethod(_selectionOverlayHandle, VROverlayInputMethod.None), "设置框选叠层输入");
            _trackingOrigin = _compositor.GetTrackingSpace();
            IsConnected = true;
            Status = "SteamVR 已连接";
            PrepareHoldProgress();
            return true;
        }
        catch (Exception ex)
        {
            Disconnect();
            Status = ex is DllNotFoundException
                ? "缺少 openvr_api.dll，请使用完整的程序文件夹启动。"
                : ex.Message;
            return false;
        }
    }

    private void RegisterApplication()
    {
        string binary = Path.Combine(AppContext.BaseDirectory, "SteamVrTranslator.exe");
        if (!File.Exists(binary)) binary = Environment.ProcessPath ?? binary;
        // Keep the manifest beside the application: SteamVR's app container may
        // not be able to read an arbitrary LocalAppData directory.
        string manifestPath = Path.Combine(AppContext.BaseDirectory, "SteamVrTranslator.vrmanifest");
        var manifest = new
        {
            source = "builtin",
            applications = new[]
            {
                new
                {
                    app_key = AppKey,
                    launch_type = "binary",
                    binary_path_windows = binary,
                    working_directory = AppContext.BaseDirectory,
                    is_dashboard_overlay = true,
                    action_manifest_path = Path.Combine(AppContext.BaseDirectory, "assets", "actions.json"),
                    strings = new { en_us = new { name = "Mugonzei Honyakuki", description = "Capture, recognize and translate VR text" } }
                }
            }
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        var applications = OpenVR.Applications ?? throw new InvalidOperationException("SteamVR 应用接口不可用。");
        var error = applications.AddApplicationManifest(manifestPath, true);
        if (error != EVRApplicationError.None)
            throw new InvalidOperationException($"注册 SteamVR 应用失败：{error}");
        error = applications.IdentifyApplication((uint)Environment.ProcessId, AppKey);
        if (error != EVRApplicationError.None)
            throw new InvalidOperationException($"标识 SteamVR 应用失败：{error}");
    }

    public void Poll()
    {
        AssertThread();
        if (!IsConnected) return;
        bool wasHolding = _translateHold.IsHolding;
        long previousGestureId = _translateHold.GestureId;
        var vrEvent = new VREvent_t();
        bool trackingChanged = false, inputInterrupted = false;
        while (_system!.PollNextEvent(ref vrEvent, (uint)Marshal.SizeOf<VREvent_t>()))
        {
            if (vrEvent.eventType is 409 or 1702 or 1703 or 1707)
            {
                inputInterrupted = true;
                string operation = vrEvent.eventType switch
                {
                    409 => "input.binding-reloaded",
                    1702 => "input.binding-load-successful",
                    1703 => "input.manifest-reloaded",
                    _ => "input.bindings-updated"
                };
                RuntimeFaultLog.Record(wasHolding ? operation + ".during-hold" : operation);
            }
            if (vrEvent.eventType is 800 or 801 or 804 or 806 or 807 or 808)
                trackingChanged = true;
            if (vrEvent.eventType == (uint)EVREventType.VREvent_Quit)
            {
                _system.AcknowledgeQuit_Exiting();
                Disconnect();
                Status = "SteamVR 已退出，请重新连接";
                return;
            }
        }

        var origin = _compositor!.GetTrackingSpace();
        if (origin != _trackingOrigin || trackingChanged)
        {
            RuntimeFaultLog.Record(wasHolding ? "input.tracking-reset.during-hold" : "input.tracking-reset");
            _trackingOrigin = origin;
            InvalidateSpatialAnchors("SteamVR 追踪原点已改变，请重新框选。");
            if (!IsConnected) return;
        }
        if (inputInterrupted)
        {
            _translateHold.Reset();
            AbortSelection("手柄输入已中断，请松开按键后重新框选。");
            if (!IsConnected) return;
        }

        var error = _input!.UpdateActionState(_activeSets, (uint)Marshal.SizeOf<VRActiveActionSet_t>());
        if (error != _lastActionStateError)
        {
            RuntimeFaultLog.Record(error == EVRInputError.None ? "input.action-state-recovered" : "input.action-state-error." + error);
            _lastActionStateError = error;
        }
        if (error != EVRInputError.None)
        {
            RecordInputState(false, false, wasHolding);
            _translateHold.Reset();
            Status = $"SteamVR 已连接；手柄输入暂不可用：{error}";
            AbortSelection("手柄输入暂不可用，请松开按键后重新框选。");
            return;
        }
        Status = "SteamVR 已连接";
        // Read both actions before invoking user code, which may disconnect the service.
        var data = new InputDigitalActionData_t();
        var digitalError = _input.GetDigitalActionData(_translateAction, ref data,
            (uint)Marshal.SizeOf<InputDigitalActionData_t>(), 0);
        if (digitalError != _lastDigitalInputError)
        {
            RuntimeFaultLog.Record(digitalError == EVRInputError.None ? "input.digital-recovered" : "input.digital-error." + digitalError);
            _lastDigitalInputError = digitalError;
        }
        bool validInput = digitalError == EVRInputError.None && data.bActive;
        if (validInput && _lastTranslateOrigin != 0 && data.activeOrigin != _lastTranslateOrigin)
        {
            RuntimeFaultLog.Record(wasHolding ? "input.origin-changed.during-hold" : "input.origin-changed");
            _translateHold.Reset();
            validInput = false;
        }
        _lastTranslateOrigin = data.bActive ? data.activeOrigin : 0;
        RecordInputState(validInput, data.bState, wasHolding);
        bool translate = false;
        SpatialSelectionUpdate selectionUpdate = default;
        if (_selectionMode)
        {
            _translateHold.Reset();
            selectionUpdate = ReadSelection(validInput, data.bState);
        }
        else translate = _translateHold.Update(validInput, data.bState, Environment.TickCount64);
        bool cancel = _translateHold.CancelRequested;
        long gestureId = _translateHold.GestureId;
        if (gestureId != previousGestureId) RuntimeFaultLog.Record("gesture.hold-start");
        if (translate) RuntimeFaultLog.Record("gesture.translate");
        if (cancel) RuntimeFaultLog.Record("gesture.cancel");
        bool nextPage = Pressed(_nextPageAction);
        if (_selectionMode)
        {
            if (CurrentSelection is null) HideSelectionFrame();
            DispatchSelectionUpdate(selectionUpdate);
        }
        else if (cancel) TranslationGestureCancelled?.Invoke(gestureId);
        else if (translate)
        {
            TranslationGestureStarted?.Invoke(gestureId);
            if (IsConnected) TranslatePressed?.Invoke();
        }
        if (IsConnected && nextPage) NextPagePressed?.Invoke();
    }

    private void RecordInputState(bool active, bool pressed, bool wasHolding)
    {
        if (_lastDiagnosticInputActive != active)
            RuntimeFaultLog.Record(active ? "input.active" : wasHolding ? "input.inactive.during-hold" : "input.inactive");
        _lastDiagnosticInputActive = active;
        if (!active)
        {
            // No valid sample means no evidence of a physical release.
            _lastDiagnosticPressed = null;
            return;
        }
        if (_lastDiagnosticPressed != pressed)
            RuntimeFaultLog.Record(pressed ? "input.button-pressed" : wasHolding ? "input.button-released.during-hold" : "input.button-released");
        _lastDiagnosticPressed = pressed;
    }

    private SpatialSelectionUpdate ReadSelection(bool validInput, bool pressed)
    {
        if (!_selectionEnabled || !validInput)
            return _selectionGesture.Abort("手柄输入已中断，请松开按键后重新框选。");
        if (!pressed && !_selectionGesture.IsSelecting)
            return _selectionGesture.Update(true, false, false, default, default, default, _trackingOrigin, _spatialEpoch, _selectionDistance);

        bool headValid = TryReadHeadPose(_trackingOrigin, out var head);
        bool leftValid = TryReadPointer(_leftPointerAction, out var left, ref _lastLeftPointerOrigin);
        bool rightValid = TryReadPointer(_rightPointerAction, out var right, ref _lastRightPointerOrigin);
        return _selectionGesture.Update(true, pressed, headValid && leftValid && rightValid,
            head, left, right, _trackingOrigin, _spatialEpoch, _selectionDistance);
    }

    private bool TryReadPointer(ulong action, out HmdMatrix34_t transform, ref ulong lastOrigin)
    {
        var data = new InputPoseActionData_t();
        bool valid = _input!.GetPoseActionDataRelativeToNow(action, _trackingOrigin, 0, ref data,
            (uint)Marshal.SizeOf<InputPoseActionData_t>(), 0) == EVRInputError.None && data.bActive && IsUsablePose(data.pose);
        if (lastOrigin != 0 && data.activeOrigin != lastOrigin) valid = false;
        lastOrigin = data.bActive ? data.activeOrigin : 0;
        transform = data.pose.mDeviceToAbsoluteTracking;
        return valid;
    }

    private bool TryReadHeadPose(ETrackingUniverseOrigin origin, out HmdMatrix34_t head)
    {
        Array.Clear(_trackingPoses);
        _system!.GetDeviceToAbsoluteTrackingPose(origin, 0, _trackingPoses);
        var pose = _trackingPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        head = pose.mDeviceToAbsoluteTracking;
        return IsUsablePose(pose);
    }

    internal static bool IsUsablePose(TrackedDevicePose_t pose) => pose.bDeviceIsConnected && pose.bPoseIsValid
        && pose.eTrackingResult == ETrackingResult.Running_OK && SpatialSelectionGeometry.IsRigid(pose.mDeviceToAbsoluteTracking);

    private void DispatchSelectionUpdate(SpatialSelectionUpdate update)
    {
        if (update.Aborted is not null) SelectionAborted?.Invoke(update.Aborted);
        if (!IsConnected) return;
        if (update.Started is long started) SelectionStarted?.Invoke(started);
        if (IsConnected && update.Committed is SpatialSelection committed && IsAnchorValid(committed.Anchor))
            SelectionCommitted?.Invoke(committed);
    }

    private void AbortSelection(string reason)
    {
        var update = _selectionGesture.Abort(reason);
        HideSelectionFrame();
        if (update.Aborted is not null) SelectionAborted?.Invoke(update.Aborted);
    }

    private void InvalidateSpatialAnchors(string reason)
    {
        _spatialEpoch++;
        var update = _selectionGesture.Abort(reason);
        _translateHold.Reset();
        _lastLeftPointerOrigin = _lastRightPointerOrigin = 0;
        HideSelectionFrame();
        if (_resultIsSpatial) HideOverlay();
        _resultIsSpatial = false;
        SpatialAnchorsInvalidated?.Invoke();
        if (update.Aborted is not null) SelectionAborted?.Invoke(update.Aborted);
    }

    public bool IsAnchorValid(SpatialAnchor anchor)
    {
        AssertThread();
        return IsConnected && anchor.Epoch == _spatialEpoch && anchor.Origin == _trackingOrigin
            && SpatialSelectionGeometry.IsValidAnchor(anchor);
    }

    private void AssertAnchor(SpatialAnchor anchor)
    {
        AssertConnected();
        if (_compositor!.GetTrackingSpace() != _trackingOrigin)
        {
            _trackingOrigin = _compositor.GetTrackingSpace();
            InvalidateSpatialAnchors("SteamVR 追踪原点已改变，请重新框选。");
        }
        if (!IsAnchorValid(anchor)) throw new InvalidOperationException("此选区已失效，请重新框选。");
    }

    public object GetTranslationBindingDiagnostics()
    {
        AssertConnected();
        var bindings = new InputBindingInfo_t[16];
        uint count = 0;
        var error = _input!.GetActionBindingInfo(_translateAction, bindings, (uint)Marshal.SizeOf<InputBindingInfo_t>(), ref count);
        var data = new InputDigitalActionData_t();
        var inputError = _input.GetDigitalActionData(_translateAction, ref data, (uint)Marshal.SizeOf<InputDigitalActionData_t>(), 0);
        return new
        {
            holdSeconds = 1.5,
            bindingError = error.ToString(),
            returnedCount = count,
            bindings = error == EVRInputError.None ? bindings.Take((int)Math.Min(count, (uint)bindings.Length)).Select(binding => new
            {
                device = binding.rchDevicePathName, input = binding.rchInputPathName,
                mode = binding.rchModeName, slot = binding.rchSlotName, sourceType = binding.rchInputSourceType
            }).ToArray() : [],
            inputError = inputError.ToString(), active = data.bActive, pressed = data.bState,
            selectionMode = SelectionMode, selectionEnabled = SelectionEnabled,
            trackingOrigin = _trackingOrigin.ToString(), spatialEpoch = _spatialEpoch,
            leftPointer = GetPointerDiagnostics(_leftPointerAction), rightPointer = GetPointerDiagnostics(_rightPointerAction)
        };
    }

    private object GetPointerDiagnostics(ulong action)
    {
        var pose = new InputPoseActionData_t();
        var error = _input!.GetPoseActionDataRelativeToNow(action, _trackingOrigin, 0, ref pose,
            (uint)Marshal.SizeOf<InputPoseActionData_t>(), 0);
        var bindings = new InputBindingInfo_t[16];
        uint count = 0;
        var bindingError = _input.GetActionBindingInfo(action, bindings, (uint)Marshal.SizeOf<InputBindingInfo_t>(), ref count);
        return new
        {
            error = error.ToString(), active = pose.bActive, connected = pose.pose.bDeviceIsConnected,
            poseValid = IsUsablePose(pose.pose), trackingResult = pose.pose.eTrackingResult.ToString(),
            bindingError = bindingError.ToString(), returnedCount = count,
            bindings = bindingError == EVRInputError.None ? bindings.Take((int)Math.Min(count, (uint)bindings.Length))
                .Select(b => new { device = b.rchDevicePathName, input = b.rchInputPathName, mode = b.rchModeName }).ToArray() : []
        };
    }

    private bool Pressed(ulong action)
    {
        var data = new InputDigitalActionData_t();
        return _input!.GetDigitalActionData(action, ref data, (uint)Marshal.SizeOf<InputDigitalActionData_t>(), 0) == EVRInputError.None
            && data.bActive && data.bState && data.bChanged;
    }

    private void EnsureDevice()
    {
        if (_device is not null) return;
        int adapterIndex = -1;
        _system!.GetDXGIOutputInfo(ref adapterIndex);
        if (adapterIndex < 0) throw new InvalidOperationException("无法找到头显使用的显卡。");
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1((uint)adapterIndex, out var adapter).CheckError();
        using (adapter)
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out _device, out _context).CheckError();
        }
    }

    /// <summary>Opens the shared eye texture before waiting for the compositor to update it.</summary>
    public void PrepareEyeCapture()
    {
        AssertConnected();
        EnsureDevice();
        EnsureMirror();
    }

    private void EnsureMirror()
    {
        if (_mirrorView != IntPtr.Zero) return;
        IntPtr mirrorView = IntPtr.Zero;
        try
        {
            var error = _compositor!.GetMirrorTextureD3D11(EVREye.Eye_Left, _device!.NativePointer, ref mirrorView);
            if (error != EVRCompositorError.None || mirrorView == IntPtr.Zero)
                throw new InvalidOperationException($"读取 VR 画面失败：{error}。请确认头显正在显示游戏。");
            // Reuse the mirror for this connection; per-capture release can repeat stale frames.
            // https://github.com/ValveSoftware/openvr/issues/1888#issuecomment-3152484251
            _mirrorView = mirrorView;
            mirrorView = IntPtr.Zero;
        }
        finally
        {
            // An unsuccessful acquisition can still return a handle. Never retain it for reuse.
            if (mirrorView != IntPtr.Zero) _compositor!.ReleaseMirrorTextureD3D11(mirrorView);
        }
    }

    /// <summary>Captures the undistorted, composited left-eye image. Hide this app's overlay first.</summary>
    public Bitmap CaptureEye()
    {
        PrepareEyeCapture();
        // GetResource adds its own COM reference. The SRV itself remains OpenVR-owned;
        // wrapping it in a disposable COM object would incorrectly release it twice.
        IntPtr vtable = Marshal.ReadIntPtr(_mirrorView);
        var getResource = Marshal.GetDelegateForFunctionPointer<GetResourceDelegate>(Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size));
        getResource(_mirrorView, out IntPtr resourcePointer);
        if (resourcePointer == IntPtr.Zero) throw new InvalidOperationException("SteamVR 返回了空的图像资源。");
        using var resource = new ID3D11Resource(resourcePointer);
        using var texture = resource.QueryInterface<ID3D11Texture2D>();
        var description = texture.Description;
        if (description.Width == 0 || description.Height == 0 || description.Width > 16384 || description.Height > 16384)
            throw new InvalidOperationException("SteamVR 返回了无效的画面尺寸。");
        if (description.SampleDescription.Count != 1)
            throw new NotSupportedException("当前 SteamVR 镜像使用多重采样，暂不支持该截图格式。");

        var stagingDescription = description;
        stagingDescription.MipLevels = 1;
        stagingDescription.ArraySize = 1;
        stagingDescription.Usage = ResourceUsage.Staging;
        stagingDescription.BindFlags = BindFlags.None;
        stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDescription.MiscFlags = ResourceOptionFlags.None;
        using var staging = _device!.CreateTexture2D(stagingDescription);
        _context!.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0);
        var mapped = _context.Map(staging, 0, MapMode.Read);
        try { return CopyMappedImage(mapped.DataPointer, mapped.RowPitch, (int)description.Width, (int)description.Height, description.Format); }
        finally { _context.Unmap(staging, 0); }
    }

    /// <summary>The mirror and pose are separate runtime reads, so reject movement rather than claim frame-perfect alignment.</summary>
    public Bitmap CaptureSelection(SpatialAnchor anchor)
    {
        AssertAnchor(anchor);
        PrepareEyeCapture();
        if (!TryReadHeadPose(anchor.Origin, out var before))
            throw new InvalidOperationException("头显定位已丢失，请恢复追踪后重新框选。");
        var renderPose = new TrackedDevicePose_t();
        var gamePose = new TrackedDevicePose_t();
        var poseError = _compositor!.GetLastPoseForTrackedDeviceIndex(OpenVR.k_unTrackedDeviceIndex_Hmd, ref renderPose, ref gamePose);
        // Overlay apps cannot call WaitGetPoses. The last compositor pose is useful when
        // available, but carries no atomic association with GetMirrorTextureD3D11.
        var captureHead = poseError == EVRCompositorError.None && IsUsablePose(renderPose)
            ? renderPose.mDeviceToAbsoluteTracking : before;
        if (!IsCapturePoseStable(before, captureHead))
            throw new InvalidOperationException("截图时头部移动较快，请保持头部稳定后重新框选。");
        var eyeToWorld = SpatialSelectionGeometry.Multiply(captureHead, _system!.GetEyeToHeadTransform(EVREye.Eye_Left));
        var projection = _system.GetProjectionMatrix(EVREye.Eye_Left, 0.01f, 100f);
        using var image = CaptureEye();
        if (!TryReadHeadPose(anchor.Origin, out var after) || !IsCapturePoseStable(before, after))
            throw new InvalidOperationException("截图期间头部位置改变，请保持头部稳定后重新框选。");
        AssertAnchor(anchor);
        return SpatialSelectionGeometry.Crop(image, anchor, eyeToWorld, projection);
    }

    internal static bool IsCapturePoseStable(HmdMatrix34_t before, HmdMatrix34_t after)
    {
        if (!SpatialSelectionGeometry.IsRigid(before) || !SpatialSelectionGeometry.IsRigid(after)) return false;
        if (Vector3.DistanceSquared(SpatialSelectionGeometry.Position(before), SpatialSelectionGeometry.Position(after)) > 0.02f * 0.02f)
            return false;
        float trace = Vector3.Dot(SpatialSelectionGeometry.Right(before), SpatialSelectionGeometry.Right(after))
            + Vector3.Dot(SpatialSelectionGeometry.Up(before), SpatialSelectionGeometry.Up(after))
            + Vector3.Dot(SpatialSelectionGeometry.Back(before), SpatialSelectionGeometry.Back(after));
        return (trace - 1) * 0.5f >= MathF.Cos(2 * MathF.PI / 180);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetResourceDelegate(IntPtr instance, out IntPtr resource);

    private static Bitmap CopyMappedImage(IntPtr source, uint rowPitch, int width, int height, Format format)
    {
        bool rgba = format is Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb or Format.R8G8B8A8_Typeless;
        bool bgra = format is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or Format.B8G8R8A8_Typeless
            or Format.B8G8R8X8_UNorm or Format.B8G8R8X8_UNorm_SRgb or Format.B8G8R8X8_Typeless;
        bool half = format == Format.R16G16B16A16_Float;
        bool ten = format == Format.R10G10B10A2_UNorm;
        if (!rgba && !bgra && !half && !ten) throw new NotSupportedException($"尚不支持此 SteamVR 镜像像素格式：{format}");
        int pixelBytes = half ? 8 : 4;
        byte[] input = new byte[checked(width * pixelBytes)];
        byte[] output = new byte[checked(width * 4)];
        if (rowPitch < input.Length) throw new InvalidOperationException("SteamVR 镜像行间距无效。");
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(source, checked((int)(y * (long)rowPitch))), input, 0, input.Length);
                for (int x = 0; x < width; x++)
                {
                    int s = x * pixelBytes, d = x * 4;
                    if (half)
                    {
                        output[d] = LinearToSrgb((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(input, s + 4)));
                        output[d + 1] = LinearToSrgb((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(input, s + 2)));
                        output[d + 2] = LinearToSrgb((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(input, s)));
                    }
                    else if (ten)
                    {
                        uint value = BitConverter.ToUInt32(input, s);
                        output[d] = (byte)(((value >> 20) & 1023) * 255 / 1023);
                        output[d + 1] = (byte)(((value >> 10) & 1023) * 255 / 1023);
                        output[d + 2] = (byte)((value & 1023) * 255 / 1023);
                    }
                    else
                    {
                        output[d] = input[s + (rgba ? 2 : 0)];
                        output[d + 1] = input[s + 1];
                        output[d + 2] = input[s + (rgba ? 0 : 2)];
                    }
                    output[d + 3] = 255;
                }
                Marshal.Copy(output, 0, IntPtr.Add(data.Scan0, y * data.Stride), output.Length);
            }
            bitmap.UnlockBits(data);
            data = null;
            return bitmap;
        }
        catch
        {
            if (data is not null) bitmap.UnlockBits(data);
            bitmap.Dispose();
            throw;
        }
    }

    private static byte LinearToSrgb(float value)
    {
        if (!float.IsFinite(value)) return 0;
        value = Math.Clamp(value, 0, 1);
        float srgb = value <= 0.0031308f ? 12.92f * value : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(srgb * 255), 0, 255);
    }

    public void ShowOverlay(Bitmap bitmap, float widthMeters, float distanceMeters, float verticalOffsetMeters)
    {
        AssertConnected();
        ArgumentNullException.ThrowIfNull(bitmap);
        if (!float.IsFinite(widthMeters) || !float.IsFinite(distanceMeters) || !float.IsFinite(verticalOffsetMeters))
            throw new ArgumentOutOfRangeException(nameof(widthMeters), "叠层参数必须为有限数值。");
        UploadOverlayBitmap(_overlayHandle, bitmap, "更新翻译画面");
        CheckOverlay(_overlay!.SetOverlayTexelAspect(_overlayHandle, 1), "设置叠层比例");
        PositionOverlay(_overlayHandle, Math.Clamp(widthMeters, 0.2f, 3f), distanceMeters, verticalOffsetMeters);
        CheckOverlay(_overlay!.ShowOverlay(_overlayHandle), "显示翻译叠层");
        _resultIsSpatial = false;
    }

    public void ShowSpatialOverlay(Bitmap bitmap, SpatialAnchor anchor)
    {
        AssertAnchor(anchor);
        ArgumentNullException.ThrowIfNull(bitmap);
        UploadOverlayBitmap(_overlayHandle, bitmap, "更新空间翻译画面");
        PositionSpatialOverlay(_overlayHandle, bitmap.Size, anchor);
        CheckOverlay(_overlay!.ShowOverlay(_overlayHandle), "显示空间翻译叠层");
        _resultIsSpatial = true;
    }

    public void ShowSelectionFrame(Bitmap bitmap, SpatialAnchor anchor)
    {
        AssertAnchor(anchor);
        ArgumentNullException.ThrowIfNull(bitmap);
        // The UI owns the bitmap and can reuse it while only the rectangle geometry changes.
        if (!ReferenceEquals(_selectionFrameBitmap, bitmap) || _selectionFrameSize != bitmap.Size)
        {
            UploadOverlayBitmap(_selectionOverlayHandle, bitmap, "更新框选边框");
            _selectionFrameBitmap = bitmap;
            _selectionFrameSize = bitmap.Size;
        }
        PositionSpatialOverlay(_selectionOverlayHandle, bitmap.Size, anchor);
        CheckOverlay(_overlay!.ShowOverlay(_selectionOverlayHandle), "显示框选边框");
    }

    private void PositionSpatialOverlay(ulong handle, Size imageSize, SpatialAnchor anchor)
    {
        CheckOverlay(_overlay!.SetOverlayWidthInMeters(handle, anchor.Width), "设置框选宽度");
        CheckOverlay(_overlay.SetOverlayTexelAspect(handle, anchor.Aspect * imageSize.Height / imageSize.Width), "设置框选比例");
        var transform = anchor.Transform;
        CheckOverlay(_overlay.SetOverlayTransformAbsolute(handle, anchor.Origin, ref transform), "设置空间固定位置");
    }

    public void HideSelectionFrame()
    {
        AssertThread();
        if (IsConnected && _selectionOverlayHandle != 0)
            CheckOverlay(_overlay!.HideOverlay(_selectionOverlayHandle), "隐藏框选边框");
    }

    public void PrepareHoldProgress()
    {
        AssertThread();
        _ = SharedHoldAtlas.Value;
    }

    private static Task<HoldProgressAtlas> CreateHoldAtlasTask()
    {
        var task = Task.Run(HoldProgressAtlas.Create);
        // Observe failures even if the application never displays a ring. The original
        // exception is still propagated by ShowHoldProgress to the visual error guard.
        _ = task.ContinueWith(failed => RuntimeFaultLog.Record("hold.atlas-build-failed", failed.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    public bool TryPrepareHoldProgressTexture()
    {
        AssertConnected();
        var task = SharedHoldAtlas.Value;
        if (!task.IsCompleted) return false;
        var atlas = task.GetAwaiter().GetResult();
        return _holdAtlasPresentation.Prepare(() =>
        {
            // SetOverlayRaw loads asynchronously. Upload while hidden and wait for the
            // native size to appear before permitting the first ShowOverlay.
            HideHoldOverlay();
            CheckOverlay(_overlay!.SetOverlayTexelAspect(_holdOverlayHandle, 1), "设置圆环图集比例");
            UploadOverlayBitmap(_holdOverlayHandle, atlas.GetTexture(), "上传圆环静态图集");
            _holdAtlasUploadedAt = Environment.TickCount64;
            RuntimeFaultLog.Record("hold.atlas-upload");
        }, () =>
        {
            uint width = 0, height = 0;
            var error = _overlay!.GetOverlayTextureSize(_holdOverlayHandle, ref width, ref height);
            if (error == EVROverlayError.None && width == HoldProgressAtlas.AtlasSize && height == HoldProgressAtlas.AtlasSize)
            {
                RuntimeFaultLog.Record("hold.atlas-ready");
                return true;
            }
            // The runtime returns InvalidHandle while its image-loading job is pending.
            if (error is not (EVROverlayError.None or EVROverlayError.InvalidHandle))
                CheckOverlay(error, "检查圆环图集加载状态");
            if (Environment.TickCount64 - _holdAtlasUploadedAt > 10_000)
            {
                // A failed asynchronous load must not leave the accepted-upload cache
                // permanently suppressing future uploads. The visual guard paces retries.
                _holdAtlasPresentation.Reset();
                _holdAtlasUploadedAt = 0;
                throw new VrOverlayException("等待圆环图集加载", EVROverlayError.TimedOut);
            }
            return false;
        });
    }

    public bool ShowHoldProgress(float progress, bool cancelStage, float distanceMeters, float verticalOffsetMeters)
    {
        AssertConnected();
        var placement = HoldPlacement(distanceMeters, verticalOffsetMeters);
        if (!TryPrepareHoldProgressTexture()) return false;
        int bucket = HoldProgressAtlas.FrameIndex(progress);
        _holdPresentation.Update(placement,
            () => _holdAtlasPresentation.Update(cancelStage, bucket,
                index =>
                {
                    var bounds = HoldProgressAtlas.Bounds(index, cancelStage);
                    CheckOverlay(_overlay!.SetOverlayTextureBounds(_holdOverlayHandle, ref bounds), "切换圆环图集帧");
                }),
            value => PositionOverlay(_holdOverlayHandle, value.Width, value.Distance, value.VerticalOffset),
            ShowHoldNative);
        return true;
    }

    /// <summary>Legacy bitmap path for diagnostics. It invalidates atlas state before changing the texture.</summary>
    public void ShowHoldOverlay(Bitmap bitmap, float distanceMeters, float verticalOffsetMeters)
    {
        AssertConnected();
        ArgumentNullException.ThrowIfNull(bitmap);
        var placement = HoldPlacement(distanceMeters, verticalOffsetMeters);
        _holdAtlasPresentation.Reset();
        _holdAtlasUploadedAt = 0;
        _holdPresentation.Update(placement,
            () =>
            {
                UploadOverlayBitmap(_holdOverlayHandle, bitmap, "更新长按进度");
                var bounds = new VRTextureBounds_t { uMin = 0, vMin = 0, uMax = 1, vMax = 1 };
                CheckOverlay(_overlay!.SetOverlayTextureBounds(_holdOverlayHandle, ref bounds), "恢复圆环完整纹理");
                CheckOverlay(_overlay.SetOverlayTexelAspect(_holdOverlayHandle, 1), "设置圆环比例");
            },
            value => PositionOverlay(_holdOverlayHandle, value.Width, value.Distance, value.VerticalOffset),
            ShowHoldNative);
    }

    private static HeadRelativeOverlayPlacement HoldPlacement(float distanceMeters, float verticalOffsetMeters)
    {
        if (!float.IsFinite(distanceMeters) || !float.IsFinite(verticalOffsetMeters))
            throw new ArgumentOutOfRangeException(nameof(distanceMeters), "叠层参数必须为有限数值。");
        return new HeadRelativeOverlayPlacement(0.12f,
            Math.Clamp(distanceMeters, 0.3f, 5f), Math.Clamp(verticalOffsetMeters, -2f, 2f));
    }

    private void ShowHoldNative()
    {
        CheckOverlay(_overlay!.ShowOverlay(_holdOverlayHandle), "显示长按进度叠层");
        RuntimeFaultLog.Record("hold.show");
    }

    public object GetHoldProgressDiagnostics()
    {
        AssertConnected();
        uint width = 0, height = 0;
        float aspect = 0;
        var bounds = new VRTextureBounds_t();
        var imageError = _overlay!.GetOverlayTextureSize(_holdOverlayHandle, ref width, ref height);
        var boundsError = _overlay.GetOverlayTextureBounds(_holdOverlayHandle, ref bounds);
        var aspectError = _overlay.GetOverlayTexelAspect(_holdOverlayHandle, ref aspect);
        return new
        {
            visible = _overlay.IsOverlayVisible(_holdOverlayHandle), texelAspect = aspect, width, height,
            imageError = imageError.ToString(), boundsError = boundsError.ToString(), aspectError = aspectError.ToString(),
            bounds = new { bounds.uMin, bounds.vMin, bounds.uMax, bounds.vMax },
            atlasBucket = _holdAtlasPresentation.Bucket ?? -1,
            atlasCancelStage = _holdAtlasPresentation.CancelStage,
            atlasUploaded = _holdAtlasPresentation.IsUploaded, atlasReady = _holdAtlasPresentation.IsReady
        };
    }

    private void UploadOverlayBitmap(ulong handle, Bitmap bitmap, string operation)
    {
        using var image = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(bitmap, 0, 0);
        }
        byte[] rgba = new byte[checked(image.Width * image.Height * 4)];
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[image.Width * 4];
            for (int y = 0; y < image.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                int offset = y * row.Length;
                for (int x = 0; x < row.Length; x += 4)
                {
                    rgba[offset + x] = row[x + 2];
                    rgba[offset + x + 1] = row[x + 1];
                    rgba[offset + x + 2] = row[x];
                    rgba[offset + x + 3] = row[x + 3];
                }
            }
        }
        finally { image.UnlockBits(data); }
        var pinned = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try { CheckOverlay(_overlay!.SetOverlayRaw(handle, pinned.AddrOfPinnedObject(), (uint)image.Width, (uint)image.Height, 4), operation); }
        finally { pinned.Free(); }
    }

    private void PositionOverlay(ulong handle, float widthMeters, float distanceMeters, float verticalOffsetMeters)
    {
        CheckOverlay(_overlay!.SetOverlayWidthInMeters(handle, widthMeters), "设置叠层大小");
        var transform = new HmdMatrix34_t
        {
            m0 = 1, m5 = 1, m10 = 1,
            m7 = Math.Clamp(verticalOffsetMeters, -2, 2),
            m11 = -Math.Clamp(distanceMeters, 0.3f, 5f)
        };
        CheckOverlay(_overlay.SetOverlayTransformTrackedDeviceRelative(handle, OpenVR.k_unTrackedDeviceIndex_Hmd, ref transform), "设置叠层位置");
    }

    public void HideOverlay()
    {
        AssertThread();
        if (IsConnected && _overlayHandle != 0) CheckOverlay(_overlay!.HideOverlay(_overlayHandle), "隐藏翻译叠层");
    }

    public void HideHoldOverlay()
    {
        AssertThread();
        if (IsConnected && _holdOverlayHandle != 0)
            _holdPresentation.Hide(() =>
            {
                CheckOverlay(_overlay!.HideOverlay(_holdOverlayHandle), "隐藏长按进度叠层");
                RuntimeFaultLog.Record("hold.hide");
            });
    }

    public void OpenBindings()
    {
        AssertConnected();
        CheckInput(_input!.OpenBindingUI(AppKey, _actionSet, 0, true), "打开 SteamVR 按键绑定");
    }

    public void Disconnect()
    {
        AssertThread();
        bool wasConnected = IsConnected;
        var interrupted = _selectionGesture.Abort("SteamVR 已断开，请重新连接后框选。");
        IsConnected = false;
        _holdPresentation.Reset();
        _holdAtlasPresentation.Reset();
        _holdAtlasUploadedAt = 0;
        _spatialEpoch++;
        _translateHold.Reset();
        _lastTranslateOrigin = 0;
        _lastLeftPointerOrigin = _lastRightPointerOrigin = 0;
        _lastDiagnosticInputActive = _lastDiagnosticPressed = null;
        _lastActionStateError = _lastDigitalInputError = EVRInputError.None;
        _selectionFrameBitmap = null;
        _selectionFrameSize = default;
        _resultIsSpatial = false;
        try { ReleaseCaptureResources(); }
        finally
        {
            try
            {
                if (_initialized)
                {
                    try
                    {
                        try
                        {
                            if (_overlay is not null && _overlayHandle != 0) _overlay.DestroyOverlay(_overlayHandle);
                        }
                        finally
                        {
                            try
                            {
                                if (_overlay is not null && _holdOverlayHandle != 0) _overlay.DestroyOverlay(_holdOverlayHandle);
                            }
                            finally
                            {
                                if (_overlay is not null && _selectionOverlayHandle != 0) _overlay.DestroyOverlay(_selectionOverlayHandle);
                            }
                        }
                    }
                    finally { OpenVR.Shutdown(); }
                }
            }
            finally
            {
                _initialized = false;
                _overlayHandle = 0;
                _holdOverlayHandle = 0;
                _selectionOverlayHandle = 0;
                _actionSet = _translateAction = _nextPageAction = _leftPointerAction = _rightPointerAction = 0;
                _system = null;
                _compositor = null;
                _overlay = null;
                _input = null;
                Status = "尚未连接 SteamVR";
            }
        }
        if (wasConnected) SpatialAnchorsInvalidated?.Invoke();
        if (interrupted.Aborted is not null) SelectionAborted?.Invoke(interrupted.Aborted);
    }

    private void ReleaseCaptureResources()
    {
        var mirrorView = _mirrorView;
        var context = _context;
        var device = _device;
        _mirrorView = IntPtr.Zero;
        _context = null;
        _device = null;
        try
        {
            // OpenVR must release its shared view while the D3D device and runtime still exist.
            if (mirrorView != IntPtr.Zero) _compositor?.ReleaseMirrorTextureD3D11(mirrorView);
        }
        finally
        {
            try { context?.Dispose(); }
            finally { device?.Dispose(); }
        }
    }

    private void AssertThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("SteamVR 接口必须从主界面线程调用。");
    }

    private void AssertConnected()
    {
        AssertThread();
        if (!IsConnected) throw new InvalidOperationException("请先连接 SteamVR。");
    }

    private static void CheckOverlay(EVROverlayError error, string operation)
    {
        if (error != EVROverlayError.None) throw new VrOverlayException(operation, error);
    }

    private static void CheckInput(EVRInputError error, string operation)
    {
        if (error != EVRInputError.None) throw new InvalidOperationException($"{operation}失败：{error}");
    }

    public void Dispose() => Disconnect();
}
