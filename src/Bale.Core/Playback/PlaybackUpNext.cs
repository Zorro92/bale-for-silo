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

    public bool Visible { get; private set; }

    /// <summary>Raised at the end of the stream (wall clock) rather than at the credits.</summary>
    public bool VideoEnded { get; private set; }

    /// <summary>Seconds left on the countdown; null when there is none.</summary>
    public int? Countdown { get; private set; }

    /// <summary>A sampled position. True when this sample raised the card.</summary>
    public bool OnPosition(double previous, double current, double? duration, double? creditsStart, bool seeking)
    {
        if (_handled || seeking || creditsStart is not { } start || !double.IsFinite(start) || start <= 0) return false;
        if (!(previous < start && current >= start)) return false;
        _handled = true;
        Visible = true;
        Countdown = autoPlay && duration is { } end ? (int)Math.Ceiling(Math.Max(0, end - current)) : null;
        return true;
    }

    /// <summary>
    /// The stream ended: a credits card's countdown advances; a card the viewer hid leaves; otherwise the
    /// card shows with a 10 s countdown (none with auto-play off) and waits for the viewer.
    /// </summary>
    public UpNextEnd OnEnded(long nowMs)
    {
        if (_dismissed) return UpNextEnd.Leave;
        if (Visible && !VideoEnded && Countdown is not null) return UpNextEnd.Advance;
        _handled = true;
        Visible = true;
        VideoEnded = true;
        _endedAt = nowMs;
        Countdown = autoPlay ? PlaybackUpNext.CountdownSeconds : null;
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
