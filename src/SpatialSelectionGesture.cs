using Valve.VR;

namespace SteamVrTranslator;

internal readonly record struct SpatialSelectionUpdate(long? Started = null,
    SpatialSelection? Committed = null, string? Aborted = null);

/// <summary>One press selects, one valid release commits; interruption always requires a new release.</summary>
internal sealed class SpatialSelectionGesture
{
    private bool _awaitRelease = true;
    private HmdMatrix34_t _plane;
    private ETrackingUniverseOrigin _origin;
    private long _epoch;
    private long _gestureId;
    public bool IsSelecting { get; private set; }
    public SpatialSelection? Current { get; private set; }

    public SpatialSelectionUpdate Update(bool inputActive, bool pressed, bool posesValid,
        HmdMatrix34_t head, HmdMatrix34_t leftPointer, HmdMatrix34_t rightPointer,
        ETrackingUniverseOrigin origin, long epoch, float distance)
    {
        if (!inputActive) return Abort("手柄输入已中断，请松开按键后重新框选。");
        if (_awaitRelease)
        {
            if (!pressed) _awaitRelease = false;
            return default;
        }
        if (!pressed && !IsSelecting) return default;
        if (!posesValid || !SpatialSelectionGeometry.IsRigid(head)
            || !SpatialSelectionGeometry.IsRigid(leftPointer) || !SpatialSelectionGeometry.IsRigid(rightPointer))
        {
            bool wasSelecting = IsSelecting;
            var aborted = Abort("头显或双手柄失去追踪；请确认双手柄已连接并绑定指向姿态，再松开按键重试。");
            return wasSelecting ? aborted : new(Aborted: "无法读取双手柄指向，请确认追踪与 SteamVR 指向绑定后重试。");
        }
        if (IsSelecting && (origin != _origin || epoch != _epoch))
            return Abort("SteamVR 追踪原点已改变，请重新框选。");

        long? started = null;
        if (!IsSelecting)
        {
            if (_gestureId == long.MinValue || !float.IsFinite(distance) || distance < 0.4f || distance > 4f)
            {
                _awaitRelease = true;
                return new(Aborted: "框选参数无效，请重新设置截图距离。");
            }
            _gestureId--;
            _plane = SpatialSelectionGeometry.CreatePlane(head, distance);
            _origin = origin;
            _epoch = epoch;
            IsSelecting = true;
            started = _gestureId;
        }

        // Invalid geometry clears the last valid rectangle, so release cannot commit stale corners.
        Current = null;
        if (SpatialSelectionGeometry.TryRayPoint(leftPointer, _plane, out var left)
            && SpatialSelectionGeometry.TryRayPoint(rightPointer, _plane, out var right)
            && SpatialSelectionGeometry.TryCreateAnchor(_plane, left, right, _origin, _epoch, out var anchor))
            Current = new(anchor, _gestureId);
        if (pressed) return new(Started: started);

        var selection = Current;
        IsSelecting = false;
        Current = null;
        // This sample itself is an active, valid release and therefore rearms the next gesture.
        return selection is SpatialSelection committed
            ? new(Committed: committed)
            : new(Aborted: "选区无效：请让双手柄指向同一平面，拉开矩形后再松开按键。");
    }

    public SpatialSelectionUpdate Abort(string reason)
    {
        bool wasSelecting = IsSelecting;
        IsSelecting = false;
        Current = null;
        _awaitRelease = true;
        return wasSelecting ? new(Aborted: reason) : default;
    }
}
