using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>
/// The episode Up Next offers (NextEpisodeResolver.kt): the next episode of the season by number, else the
/// first episode of the next numbered season. The last special continues into the first numbered season
/// (Apple c466777f); a regular episode never continues into Specials.
/// </summary>
public static class PlaybackUpNext
{
    public const int CountdownSeconds = 10;

    public static EpisodeCard? NextInSeason(IReadOnlyList<EpisodeCard> season, long episodeNumber) =>
        season.Where(episode => episode.EpisodeNumber > episodeNumber).OrderBy(episode => episode.EpisodeNumber).FirstOrDefault();

    /// <summary>The season to continue into after the last episode of <paramref name="season"/>, or null.</summary>
    public static long? NextSeason(IReadOnlyList<SeasonChoice> seasons, long season)
    {
        var numbered = seasons.Where(choice => !choice.IsSpecials && choice.Number > 0 && choice.EpisodeCount > 0)
            .Select(choice => choice.Number).Order().ToList();
        var specials = season == 0 || seasons.Any(choice => choice.Number == season && choice.IsSpecials);
        if (specials) return numbered.Count > 0 ? numbered[0] : null;
        foreach (var number in numbered)
            if (number > season) return number;
        return null;
    }

    /// <summary>The first episode of a season's list.</summary>
    public static EpisodeCard? First(IReadOnlyList<EpisodeCard> season) => season.OrderBy(episode => episode.EpisodeNumber).FirstOrDefault();
}

/// <summary>
/// TvPlayerViewModel.kt's Up Next surface. Raised when playback crosses the credits start (never by a
/// resume or seek that lands inside them) with a countdown that mirrors the remaining playback, so the
/// advance waits for the stream to end; or raised at the end with a 10 s wall-clock countdown. With
/// auto-play off it shows without a countdown and never advances by itself.
/// </summary>
public enum UpNextEnd
{
    /// <summary>Play the next episode now.</summary>
    Advance,
    /// <summary>Show the card (its countdown, if any, runs from here).</summary>
    Show,
    /// <summary>Leave the player, as without Up Next.</summary>
    Leave,
}

public sealed class PlaybackUpNextState(bool autoPlay = true)
{
    private bool _handled;
    private bool _dismissed;
    private long _endedAt;
    private bool _requested;

    /// <summary>playback.auto_play_next: off shows the card without a countdown.</summary>
    public bool AutoPlay { get; private set; } = autoPlay;

    /// <summary>
    /// The setting changed (or its first read arrived). Off stops a running countdown; on starts one on a
    /// card already showing (the 10 s end countdown from now, or the credits countdown to the end).
    /// </summary>
    public void SetAutoPlay(bool value, long nowMs, double position, double? duration)
    {
        if (AutoPlay == value) return;
        AutoPlay = value;
        if (!value)
        {
            Countdown = null;
            return;
        }
        if (!Visible || _dismissed) return;
        if (VideoEnded)
        {
            _endedAt = nowMs;
            Countdown = PlaybackUpNext.CountdownSeconds;
        }
        else if (!_requested && duration is { } end) Countdown = (int)Math.Ceiling(Math.Max(0, end - position));
    }

    /// <summary>The control bar's Up Next button is offered while the card is not up.</summary>
    public bool CanShowNow => !Visible;

    /// <summary>
    /// The viewer asked for the card early (TvPlayerViewModel.kt onUserRequestedNextUp): it shows without a
    /// countdown, since they are choosing; the end of the episode still runs the usual end countdown.
    /// </summary>
    public bool ShowNow()
    {
        if (Visible) return false;
        _handled = true;
        _dismissed = false;
        _requested = true;
        Visible = true;
        VideoEnded = false;
        Countdown = null;
        return true;
    }

    public bool Visible { get; private set; }

    /// <summary>
    /// The episode reached its credits or its end (Android e727c9af5): the series page moves on to the next
    /// episode when the player closes. Recorded before the card's once-per-item guard, so an Up Next opened by hand
    /// (or kept watching) still counts a later credits crossing; a seek that lands in the credits counts too.
    /// </summary>
    public bool Finished { get; private set; }

    /// <summary>Raised at the end of the stream (wall clock) rather than at the credits.</summary>
    public bool VideoEnded { get; private set; }

    /// <summary>Seconds left on the countdown; null when there is none.</summary>
    public int? Countdown { get; private set; }

    /// <summary>A sampled position. True when this sample raised the card.</summary>
    public bool OnPosition(double previous, double current, double? duration, double? creditsStart, bool seeking)
    {
        if (!seeking && creditsStart is { } credits && double.IsFinite(credits) && credits > 0 && current >= credits) Finished = true;
        if (_handled || seeking || creditsStart is not { } start || !double.IsFinite(start) || start <= 0) return false;
        if (!(previous < start && current >= start)) return false;
        _handled = true;
        Visible = true;
        Countdown = AutoPlay && duration is { } end ? (int)Math.Ceiling(Math.Max(0, end - current)) : null;
        return true;
    }

    /// <summary>
    /// The stream ended: a credits card's countdown advances; a card the viewer hid leaves; otherwise the
    /// card shows with a 10 s countdown (none with auto-play off) and waits for the viewer.
    /// </summary>
    public UpNextEnd OnEnded(long nowMs)
    {
        Finished = true;
        if (_dismissed) return UpNextEnd.Leave;
        if (Visible && !VideoEnded && Countdown is not null) return UpNextEnd.Advance;
        _handled = true;
        Visible = true;
        VideoEnded = true;
        _endedAt = nowMs;
        Countdown = AutoPlay ? PlaybackUpNext.CountdownSeconds : null;
        return UpNextEnd.Show;
    }

    /// <summary>Advance the countdown. True when the player should advance now.</summary>
    public bool Tick(long nowMs, double position, double? duration)
    {
        if (!Visible || Countdown is null) return false;
        if (VideoEnded)
        {
            Countdown = Math.Max(0, PlaybackUpNext.CountdownSeconds - (int)((nowMs - _endedAt) / 1000));
            return Countdown == 0;
        }
        if (duration is { } end) Countdown = (int)Math.Ceiling(Math.Max(0, end - position));
        return false;
    }

    /// <summary>The viewer hid the card: no countdown, no automatic advance this episode.</summary>
    public void Dismiss()
    {
        _dismissed = true;
        Visible = false;
        Countdown = null;
    }
}

/// <summary>
/// SeriesPlaybackReturn (tvOS) / Android e727c9af5: the episode the series page selects when the player closes. The
/// episode on screen, or the Up Next episode once the one on screen reached its credits or end.
/// </summary>
public sealed record SeriesPlaybackReturn(string SeriesId, string EpisodeId, long? SeasonNumber)
{
    public static SeriesPlaybackReturn? For(string? seriesId, string? episodeId, long? seasonNumber, bool finished, EpisodeCard? next)
    {
        if (string.IsNullOrWhiteSpace(seriesId) || string.IsNullOrWhiteSpace(episodeId)) return null;
        return finished && next is not null
            ? new(seriesId, next.ContentId, next.SeasonNumber)
            : new(seriesId, episodeId, seasonNumber);
    }
}
