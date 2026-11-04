namespace Bale.Core.Playback;

/// <summary>
/// PlayerSpinnerVisibility.kt: delay a continuous load by 500 ms, then keep the spinner up for
/// at least 500 ms. The platform supplies a monotonic millisecond clock, not wall-clock time.
/// </summary>
public sealed class PlayerSpinnerVisibility
{
    public const long ShowDelayMilliseconds = 500;
    public const long MinimumVisibleMilliseconds = 500;

    private long? _loadingSince;
    private long _shownAt;

    public bool Visible { get; private set; }

    public bool Update(bool loading, long now)
    {
        if (loading)
        {
            _loadingSince ??= now;
            if (!Visible && now - _loadingSince.Value >= ShowDelayMilliseconds)
            {
                Visible = true;
                _shownAt = now;
            }
        }
        else
        {
            _loadingSince = null;
            if (Visible && now - _shownAt >= MinimumVisibleMilliseconds) Visible = false;
        }

        return Visible;
    }
}
