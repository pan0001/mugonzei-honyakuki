namespace SteamVrTranslator;

internal readonly record struct HeadRelativeOverlayPlacement(float Width, float Distance, float VerticalOffset);

/// <summary>Caches only successful native operations; a failed operation remains eligible for retry.</summary>
internal sealed class OverlayPresentationState
{
    private HeadRelativeOverlayPlacement? _placement;
    public bool IsVisible { get; private set; }

    public void Update(HeadRelativeOverlayPlacement placement, Action upload,
        Action<HeadRelativeOverlayPlacement> position, Action show)
    {
        // Keep the existing texture/visibility intact while replacing its pixels. Never
        // hide or clear the texture between animation frames.
        upload();
        if (_placement != placement)
        {
            position(placement);
            _placement = placement;
        }
        if (!IsVisible)
        {
            show();
            IsVisible = true;
        }
    }

    public void Hide(Action hide)
    {
        if (!IsVisible) return;
        hide();
        IsVisible = false;
    }

    public void Reset()
    {
        _placement = null;
        IsVisible = false;
    }
}
