namespace Bale.Xbox.Services;

/// <summary>Layout.kt / tvRailPinOnFocus: discard held horizontal input during a card step, never queue it.</summary>
internal sealed class RailScrollState
{
    public bool IsAnimating { get; private set; }

    public bool ShouldDrop(int horizontalStep, bool wasKeyDown) => horizontalStep != 0 && wasKeyDown && IsAnimating;

    /// <summary>Arm before ChangeView's first frame; a clamped/no-op pin must not lock the row.</summary>
    public double? Begin(double offset, double requestedOffset, double scrollableWidth)
    {
        var target = Math.Clamp(requestedOffset, 0, Math.Max(0, scrollableWidth));
        if (Math.Abs(target - offset) < 1) return null;
        IsAnimating = true;
        return target;
    }

    public void ViewChanging(double offset, double finalOffset)
    {
        if (Math.Abs(finalOffset - offset) >= 1) IsAnimating = true;
    }

    public void ViewChanged(bool isIntermediate) => IsAnimating = isIntermediate;

    public void Reset() => IsAnimating = false;
}
