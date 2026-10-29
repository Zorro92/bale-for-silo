namespace Bale.Core.Detail;

/// <summary>
/// A title's detail page, already shaped the way Silo's Android TV app presents it
/// (TvDetailHero, TvDetailMetadata, TvItemDetailScreen).
/// </summary>
/// <param name="Title">The title, or the series title on an episode.</param>
/// <param name="Subtitle">Second title line: the episode title on an episode.</param>
/// <param name="SourceTokens">"Movie · Documentary", "TV Show · Comedy · Animation".</param>
/// <param name="ContentRating">The outlined rating chip, e.g. "TV-MA".</param>
/// <param name="Facts">"1997 · 29 Seasons", "2026 · 1h 19m", then quality badges.</param>
/// <param name="QualityBadges">"4K", "HD", "HDR", "5.1".</param>
/// <param name="CreditLine">"Starring A, B, C" on a series, "Directed by X" on a movie.</param>
/// <param name="PlayTargetId">What Play starts: the next-up episode for a series, the item itself otherwise.</param>
/// <param name="ResumeSeconds">Where Play resumes, when there's a saved position.</param>
/// <param name="Ratings">External ratings after the facts: "TMDB 8.3", "IMDb 7.9" (ExternalRatings.forTitle).</param>
public sealed record ItemDetail(
    string ContentId,
    string Type,
    string Title,
    string? Subtitle,
    Uri? LogoUrl,
    Uri? BackdropUrl,
    string? Tagline,
    string? Overview,
    IReadOnlyList<string> SourceTokens,
    string? ContentRating,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> QualityBadges,
    string? CreditLine,
    IReadOnlyList<CastMember> Cast,
    string? SeriesId,
    string PlayTargetId,
    long? PlaySeasonNumber,
    double? ResumeSeconds,
    ItemFlags Flags,
    IReadOnlyList<DisplayRating> Ratings)
{
    public bool IsSeries => Type == "series";
}

/// <summary>One external rating: the source's mark ("TMDB", "IMDb") and its score as the source writes it ("8.3").</summary>
public sealed record DisplayRating(string Source, string Name, string Display);

/// <summary>The viewer's own flags on a title.</summary>
public sealed record ItemFlags(bool InWatchlist, bool IsFavorite, bool Played);

/// <param name="Role">The first character played, or the crew job.</param>
public sealed record CastMember(string Name, string? Role, Uri? PhotoUrl);

public sealed record SeasonChoice(long Number, string Title, long EpisodeCount);

/// <summary>An episode card on the series page (TvDetailEpisodeRail), and the hero it shows when focused.</summary>
public sealed record EpisodeCard(
    string ContentId,
    long SeasonNumber,
    long EpisodeNumber,
    string Title,
    string? Overview,
    Uri? StillUrl,
    DateOnly? AirDate,
    long? RuntimeMinutes,
    double? Progress,
    bool Played)
{
    private static readonly System.Globalization.CultureInfo English = System.Globalization.CultureInfo.GetCultureInfo("en-US");

    /// <summary>"S01E02 · Pilot", or the code alone (EpisodeCardCaption.title).</summary>
    public string Caption
    {
        get
        {
            var code = $"S{SeasonNumber:00}E{EpisodeNumber:00}";
            return string.IsNullOrWhiteSpace(Title) ? code : $"{code} · {Title.Trim()}";
        }
    }

    /// <summary>"Aug 13, 1997  ·  22m" under the caption (EpisodeCardCaption.metadata).</summary>
    public string CaptionMeta => string.Join("  ·  ", new[] { Date, ShortRuntime }.OfType<string>());

    /// <summary>The hero's facts while the episode has focus: "Aug 13, 1997", "22 min" (TvDetailMetadata).</summary>
    public IReadOnlyList<string> HeroFacts => new[] { Date, RuntimeMinutes is > 0 ? $"{RuntimeMinutes} min" : null }.OfType<string>().ToList();

    /// <summary>"Season 1", "Episode 1" after the rating.</summary>
    public IReadOnlyList<string> HeroTokens => new[]
    {
        SeasonNumber == 0 ? "Specials" : $"Season {SeasonNumber}",
        EpisodeNumber > 0 ? $"Episode {EpisodeNumber}" : null,
    }.OfType<string>().ToList();

    /// <summary>"Pilot · While the boys…": the hero synopsis names the episode first.</summary>
    public string HeroSynopsis => string.Join(" · ", new[] { Title, Overview }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private string? Date => AirDate?.ToString("MMM d, yyyy", English);

    private string? ShortRuntime => RuntimeMinutes switch
    {
        null or <= 0 => null,
        >= 60 => $"{RuntimeMinutes / 60}h {RuntimeMinutes % 60}m",
        _ => $"{RuntimeMinutes}m",
    };
}

/// <summary>Play button wording, as TvItemDetailScreen.playButtonLabel words it.</summary>
public static class PlayLabel
{
    /// <summary>"Play S1:E2" / "Resume S1:E2" for a series' next-up episode, else "Play" or "Resume 12:34".</summary>
    public static string For(ItemDetail detail, EpisodeCard? nextUp)
    {
        var resuming = detail.ResumeSeconds is > 0;
        if (detail.IsSeries && nextUp is not null)
        {
            return $"{(resuming ? "Resume" : "Play")} S{nextUp.SeasonNumber}:E{nextUp.EpisodeNumber}";
        }

        return resuming ? $"Resume {Clock(detail.ResumeSeconds!.Value)}" : "Play";
    }

    /// <summary>"1:02:05" over an hour, "12:34" under.</summary>
    internal static string Clock(double seconds)
    {
        var total = (long)Math.Round(seconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }
}
