namespace Bale.Xbox.Services;

/// <summary>
/// Layout.kt / tvRailPinOnFocus: discard held horizontal input during a card step, never queue it.
/// A held key therefore moves one card per scroll animation. Near a row's ends the pin is clamped and
/// nothing animates, so repeats would run at the raw key-repeat rate and the row would suddenly speed up
/// (owner, 2026-11-17); held steps are also paced to the last measured animation length there.
/// </summary>
internal sealed class RailScrollState
{
    /// <summary>Pace before any animation has been measured, close to the platform's ChangeView animation.</summary>
    public const long DefaultPaceMs = 250;
    public const long MinPaceMs = 80;
    public const long MaxPaceMs = 600;

    private long _lastStep = long.MinValue / 2;
    private long _began = -1;

    public bool IsAnimating { get; private set; }

    /// <summary>How long one held step takes: the last scroll animation's length.</summary>
    public long PaceMs { get; private set; } = DefaultPaceMs;

    public bool ShouldDrop(int horizontalStep, bool wasKeyDown) => horizontalStep != 0 && wasKeyDown && IsAnimating;

    /// <summary>
    /// A held (repeated) horizontal key waits for the animation, and where nothing animates for the pace since the
    /// last step. A new press is never consumed.
    /// </summary>
    public bool ShouldDrop(int horizontalStep, bool wasKeyDown, long nowMs) =>
        horizontalStep != 0 && wasKeyDown && (IsAnimating || nowMs - _lastStep < PaceMs);

    /// <summary>A horizontal key went through (it moves focus or reaches the edge).</summary>
    public void Stepped(long nowMs) => _lastStep = nowMs;

    /// <summary>Arm before ChangeView's first frame; a clamped/no-op pin must not lock the row.</summary>
    public double? Begin(double offset, double requestedOffset, double scrollableWidth, long? nowMs = null)
    {
        var target = Math.Clamp(requestedOffset, 0, Math.Max(0, scrollableWidth));
        if (Math.Abs(target - offset) < 1) return null;
        IsAnimating = true;
        _began = nowMs ?? -1;
        return target;
    }

    public void ViewChanging(double offset, double finalOffset)
    {
        if (Math.Abs(finalOffset - offset) >= 1) IsAnimating = true;
    }

    public void ViewChanged(bool isIntermediate, long? nowMs = null)
    {
        IsAnimating = isIntermediate;
        if (isIntermediate || _began < 0 || nowMs is not { } now) return;
        PaceMs = Math.Clamp(now - _began, MinPaceMs, MaxPaceMs);
        _began = -1;
    }

    public void Reset()
    {
        IsAnimating = false;
        _began = -1;
    }
}
