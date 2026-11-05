using System.Globalization;

namespace Bale.Core.Playback;

/// <summary>Passive transport feedback, after silo-android TvSkipSeekIndicator.kt / TvPlayerScrubber.kt.</summary>
public sealed class PlaybackTransportFeedback
{
    // TvPlayerScreen.kt SKIP_FEEDBACK_HIDE_MS. Every discrete press restarts the timer.
    public const int HideMilliseconds = 1200;
    private long? _shownAt;
    public int DeltaSeconds { get; private set; }
    public double TargetSeconds { get; private set; }

    public void ShowSkip(int deltaSeconds, double targetSeconds, long nowMilliseconds)
    {
        DeltaSeconds = deltaSeconds;
        TargetSeconds = double.IsFinite(targetSeconds) ? Math.Max(0, targetSeconds) : 0;
        _shownAt = nowMilliseconds;
    }

    public bool Visible(long nowMilliseconds) => _shownAt is { } shown && nowMilliseconds - shown < HideMilliseconds;
    public void Hide() => _shownAt = null;

    public static double Progress(double position, double? duration) =>
        double.IsFinite(position) && duration is > 0 and { } end && double.IsFinite(end)
            ? Math.Clamp(position / end, 0, 1) : 0;

    public static string Delta(int seconds) => seconds < 0
        ? "−" + (-(long)seconds).ToString(CultureInfo.InvariantCulture) + "s"
        : "+" + seconds.ToString(CultureInfo.InvariantCulture) + "s";

    public static string Clock(double seconds, bool forceHours = false)
    {
        var total = double.IsFinite(seconds) && seconds > 0 ? (long)Math.Min(seconds, int.MaxValue) : 0;
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var rest = total % 60;
        return hours > 0 || forceHours
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    public static string Remaining(double position, double duration) => "−" + Clock(Math.Max(0, duration - position));
}
