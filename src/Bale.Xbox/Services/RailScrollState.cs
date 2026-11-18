namespace Bale.Xbox.Services;

/// <summary>
/// Layout.kt / tvRailPinOnFocus: discard held horizontal input during a card step, never queue it.
/// A held key therefore moves one card per scroll animation. Near a row's ends the pin is clamped and
/// nothing animates, so repeats would run at the raw key-repeat rate and the row would suddenly speed up
/// (owner, 2026-11-17); held steps are also paced there. The pace is the last animated held step's whole
/// cycle (focus work + animation + the wait for the next repeat), not the animation alone: on rows whose focus
/// also updates the hero backdrop the cycle is longer, and pacing to the animation sped the row's end up
/// (owner, 2026-11-18, wide landscape rows). Scrolls cut short by the row's end never set the pace. Before any
/// held cycle is seen, the last full animation's length stands in.
/// </summary>
internal sealed class RailScrollState
{
    /// <summary>Pace before any animation has been measured, close to the platform's ChangeView animation.</summary>
    public const long DefaultPaceMs = 250;
    public const long MinPaceMs = 80;
    public const long MaxPaceMs = 600;

    private long _lastStep = long.MinValue / 2;
    private long _began = -1;
    private bool _lastStepHeld;
    private bool _stepAnimated;
    private bool _cycleMeasured;
    private bool _stepFull;

    public bool IsAnimating { get; private set; }

    /// <summary>How long one held step takes: the last animated held cycle, else the last animation's length.</summary>
    public long PaceMs { get; private set; } = DefaultPaceMs;

    public bool ShouldDrop(int horizontalStep, bool wasKeyDown) => horizontalStep != 0 && wasKeyDown && IsAnimating;

    /// <summary>
    /// A held (repeated) horizontal key waits for the animation, and where nothing animates for the pace since the
    /// last step. A new press is never consumed. A step that animated is gated by its animation only, so a measured
    /// cycle never feeds back into itself.
    /// </summary>
    public bool ShouldDrop(int horizontalStep, bool wasKeyDown, long nowMs) =>
        horizontalStep != 0 && wasKeyDown && (IsAnimating || (!_stepAnimated && nowMs - _lastStep < PaceMs));

    /// <summary>
    /// A horizontal key went through (it moves focus or reaches the edge). Two held steps in a row, the earlier of
    /// which animated, measure the pace; the first repeat after a press is skipped (it carries the repeat delay).
    /// </summary>
    public void Stepped(long nowMs, bool held = false)
    {
        if (held && _lastStepHeld && _stepAnimated && _stepFull)
        {
            PaceMs = Math.Clamp(nowMs - _lastStep, MinPaceMs, MaxPaceMs);
            _cycleMeasured = true;
        }
        _lastStep = nowMs;
        _lastStepHeld = held;
        _stepAnimated = false;
        _stepFull = false;
    }

    /// <summary>Arm before ChangeView's first frame; a clamped/no-op pin must not lock the row.</summary>
    public double? Begin(double offset, double requestedOffset, double scrollableWidth, long? nowMs = null)
    {
        var target = Math.Clamp(requestedOffset, 0, Math.Max(0, scrollableWidth));
        if (Math.Abs(target - offset) < 1) return null;
        IsAnimating = true;
        // A scroll cut short by the row's end is quicker than a full card step: it never sets the pace.
        _stepFull = Math.Abs(target - requestedOffset) < 1;
        _began = _stepFull ? nowMs ?? -1 : -1;
        _stepAnimated = true;
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
        // Only a stand-in until a held cycle is measured: never shorten a measured cycle.
        var animation = Math.Clamp(now - _began, MinPaceMs, MaxPaceMs);
        if (!_cycleMeasured || animation > PaceMs) PaceMs = animation;
        _began = -1;
    }

    public void Reset()
    {
        IsAnimating = false;
        _began = -1;
    }
}
