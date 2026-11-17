using System.Globalization;
using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>
/// The player's on-screen controls: shown by the D-pad or by A pausing, hidden after
/// <see cref="HideAfterMs"/> without input (also while paused, the owner's design), never while a
/// scrub preview is open.
/// </summary>
public sealed class PlaybackControlsState(long hideAfterMs = PlaybackControlsState.HideAfterMs)
{
    /// <summary>TvPlayerScreen.kt CONTROLS_AUTO_HIDE_MS.</summary>
    public const long HideAfterMs = 5_000;

    private long _lastInput;

    public bool Visible { get; private set; }

    public void Show(long nowMs)
    {
        Visible = true;
        _lastInput = nowMs;
    }

    /// <summary>Any key while the controls are up keeps them up.</summary>
    public void Input(long nowMs)
    {
        if (Visible) _lastInput = nowMs;
    }

    public void Hide() => Visible = false;

    public bool ShouldHide(long nowMs, bool scrubbing) => Visible && !scrubbing && nowMs - _lastInput >= hideAfterMs;
}

/// <summary>
/// The focused scrubber's preview (TvPlayerScrubber.kt): Left/Right move a preview in steps, Select
/// commits it as one seek, Back/Down cancels it. Playback is untouched until the commit.
/// </summary>
public sealed class PlaybackScrub
{
    public const double StepSeconds = 10;

    public double? Preview { get; private set; }

    public bool Active => Preview is not null;

    public double Nudge(double position, double? duration, bool forward)
    {
        var origin = Preview ?? position;
        var target = Math.Max(0, (double.IsFinite(origin) ? origin : 0) + (forward ? StepSeconds : -StepSeconds));
        if (duration is > 0 and { } end && double.IsFinite(end)) target = Math.Min(target, end);
        Preview = target;
        return target;
    }

    /// <summary>The target to seek to, or null when there was no preview.</summary>
    public double? Commit()
    {
        var target = Preview;
        Preview = null;
        return target;
    }

    public void Cancel() => Preview = null;
}

/// <summary>The title block above the scrubber (TvPlayerScreen.kt tvPlayerEyebrow).</summary>
public static class PlaybackControlsText
{
    /// <summary>"SEVERANCE · S2:E4" for an episode, "MOVIE · 2026" for a film; null when there is nothing.</summary>
    public static string? Eyebrow(ItemDetail detail)
    {
        if (detail.Type == "episode" && !string.IsNullOrWhiteSpace(detail.Title))
        {
            var code = detail.SeasonNumber is { } season && detail.EpisodeNumber is { } episode
                ? string.Create(CultureInfo.InvariantCulture, $"S{season}:E{episode}") : null;
            return string.Join(" · ", new[] { detail.Title.Trim(), code }.Where(part => part is not null)).ToUpperInvariant();
        }

        var kind = detail.Type == "movie" ? "Movie" : null;
        var year = detail.Year is > 0 and { } y ? y.ToString(CultureInfo.InvariantCulture) : null;
        var line = string.Join(" · ", new[] { kind, year }.Where(part => part is not null));
        return line.Length == 0 ? null : line.ToUpperInvariant();
    }

    /// <summary>The episode's own title for an episode, the title otherwise.</summary>
    public static string Title(ItemDetail detail) =>
        detail.Type == "episode" && !string.IsNullOrWhiteSpace(detail.Subtitle) ? detail.Subtitle.Trim() : detail.Title;
}
