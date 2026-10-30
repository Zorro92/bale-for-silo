using System.Net;
using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Core.Connection;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

public class DetailFeedTests
{
    private static CatalogItemDetail Series() => new()
    {
        ContentId = "series-1",
        Type = "series",
        Title = "South Park",
        Year = 1997,
        SeasonCount = 29,
        Genres = ["Comedy", "Animation", "Satire"],
        ContentRating = "TV-MA",
        Cast =
        [
            new CastCredit { Name = "Trey Parker", Character = "Stan Marsh|Eric Cartman" },
            new CastCredit { Name = "Matt Stone", Character = "Kyle" },
            new CastCredit { Name = "Trey Parker", Character = "Randy" },
            new CastCredit { Name = "Isaac Hayes", Character = "Chef" },
            new CastCredit { Name = "Mona Marshall" },
        ],
    };

    [Fact]
    public void SeriesReadsLikeAndroidTv()
    {
        var series = Series();

        Assert.Equal(["TV Show", "Comedy", "Animation"], DetailFeed.SourceTokens(series));
        Assert.Equal(["1997", "29 Seasons"], DetailFeed.Facts(series));
        Assert.Equal("Starring Trey Parker, Matt Stone, Isaac Hayes", DetailFeed.StarringCredit(series));
        Assert.Equal("Stan Marsh", DetailFeed.FirstRole(series.Cast![0].Character));
    }

    [Fact]
    public void MovieShowsRuntimeQualityAndDirector()
    {
        var movie = new CatalogItemDetail
        {
            Type = "movie",
            Year = 2026,
            Runtime = 79,
            Genres = ["Documentary"],
            Crew = [new CrewCredit { Name = "Producer P", Job = "Producer" }, new CrewCredit { Name = "Jane Doe", Job = "Director" }],
        };

        Assert.Equal(["Movie", "Documentary"], DetailFeed.SourceTokens(movie));
        Assert.Equal(["2026", "1h 19m"], DetailFeed.Facts(movie));
        Assert.Equal("Directed by Jane Doe", DetailFeed.DirectorCredit(movie));
        Assert.Equal(["HD", "5.1"], DetailFeed.QualityBadges(new CatalogItemOverlay { Resolution = "1080p", AudioChannels = "5.1" }));
        Assert.Equal(["4K", "DOLBY VISION"], DetailFeed.QualityBadges(new CatalogItemOverlay { Resolution = "2160p", Hdr = "DV HDR10", AudioChannels = "Stereo" }));
    }

    [Fact]
    public void PlayLabels()
    {
        var detail = Movie();

        Assert.Equal("Play", PlayLabel.For(detail, null));
        Assert.Equal("Resume 12:34", PlayLabel.For(detail with { ResumeSeconds = 754 }, null));
        Assert.Equal("Resume 1:02:05", PlayLabel.For(detail with { ResumeSeconds = 3725 }, null));

        var series = detail with { Type = "series" };
        var nextUp = new EpisodeCard("e", 1, 2, "Ep", null, null, null, null, null, false);
        Assert.Equal("Play S1:E2", PlayLabel.For(series, nextUp));
    }

    [Fact]
    public void EpisodeCaptionsReadLikeTheOfficialRail()
    {
        var episode = new EpisodeCard("e", 1, 1, "Cartman Gets an Anal Probe", "While the boys wait…", null, new DateOnly(1997, 8, 13), 22, null, false);

        Assert.Equal("S01E01 · Cartman Gets an Anal Probe", episode.Caption);
        Assert.Equal("Aug 13, 1997  ·  22m", episode.CaptionMeta);
        Assert.Equal(["Aug 13, 1997", "22 min"], episode.HeroFacts);
        Assert.Equal(["Season 1", "Episode 1"], episode.HeroTokens);
        Assert.Equal("Cartman Gets an Anal Probe · While the boys wait…", episode.HeroSynopsis);
        Assert.Equal("S00E03", (episode with { SeasonNumber = 0, EpisodeNumber = 3, Title = "" }).Caption);
        Assert.Equal("1h 5m", (episode with { AirDate = null, RuntimeMinutes = 65 }).CaptionMeta);
    }

    private static ItemDetail Movie() => new("m", "movie", "M", null, null, null, null, null, [], null, [], [], null, [], null, "m", null, null, new(false, false, false), []);

    private static DetailFeed Feed(StubHandler? handler = null)
    {
        var address = new Uri("https://silo.example.com");
        var connection = new SiloConnection(address, new ClientIdentity("d", "x", "xbox", "Bale"), transport: handler ?? new StubHandler());
        return new DetailFeed(connection.Api, new BrowseFeed(connection.Api, address));
    }

    [Fact]
    public void FocusedEpisodeOwnsResumeEvenWhenTheSeriesHasDifferentProgress()
    {
        var series = Movie() with { Type = "series", ResumeSeconds = 900 };
        var episode = new EpisodeCard("e", 2, 3, "Ep", null, null, null, null, null, false);
        Assert.Equal("Play S2:E3", PlayLabel.For(series, episode));
        Assert.Null(PlayLabel.ResumeFor(series, episode));
        Assert.Equal("Play", PlayLabel.For(series, null));
        Assert.Null(PlayLabel.ResumeFor(series, null));

        episode = episode with { ResumeSeconds = 750 };
        series = series with { ResumeSeconds = null };
        Assert.Equal("Resume S2:E3", PlayLabel.For(series, episode));
        Assert.Equal(750, PlayLabel.ResumeFor(series, episode));
        Assert.Equal("Resume E3", PlayLabel.For(series with { Type = "season" }, episode));
        Assert.Equal("Resume 12:30", PlayLabel.For(Movie() with { ResumeSeconds = 750 }, null));
    }

    [Theory]
    [InlineData(null, 1800d, null)]
    [InlineData(0d, 1800d, null)]
    [InlineData(30d, 1800d, null)]
    [InlineData(31d, 1800d, 31d)]
    [InlineData(750d, 1800d, 750d)]
    [InlineData(1794d, 1800d, 1794d)]
    [InlineData(1795d, 1800d, null)]
    [InlineData(1800d, 1800d, null)]
    [InlineData(750d, 0d, null)]
    [InlineData(750d, null, null)]
    [InlineData(double.NaN, 1800d, null)]
    [InlineData(750d, double.PositiveInfinity, null)]
    public void ResumeUsesTheOfficialOpeningAndEndingThresholds(double? position, double? duration, double? expected)
    {
        var data = new WatchRollup { PositionSeconds = position, DurationSeconds = duration };
        var feed = Feed();
        var episode = feed.ToEpisode(new Episode { ContentId = "e", UserData = data }, 1);
        Assert.Equal(expected, episode.ResumeSeconds);
        Assert.Equal(expected, feed.ToDetail(new CatalogItemDetail { Type = "movie", UserData = data }).ResumeSeconds);
        Assert.Null(feed.ToDetail(new CatalogItemDetail { Type = "series", UserData = data }).ResumeSeconds);
        Assert.Null(feed.ToDetail(new CatalogItemDetail { Type = "season", UserData = data }).ResumeSeconds);
    }

    [Fact]
    public void NextUpPrefersInProgressThenUnwatchedThenTheFirstEpisode()
    {
        var first = new EpisodeCard("e1", 1, 1, "", null, null, null, null, null, true);
        var second = first with { ContentId = "e2", EpisodeNumber = 2, Played = false };
        var third = first with { ContentId = "e3", EpisodeNumber = 3, IsInProgress = true };
        Assert.Same(third, DetailEpisodes.NextUp([first, second, third]));
        Assert.Same(second, DetailEpisodes.NextUp([first, second]));
        Assert.Same(first, DetailEpisodes.NextUp([first]));
        Assert.Null(DetailEpisodes.NextUp([]));
    }

    [Theory]
    [InlineData("episode", "series", 2L, "e")]
    [InlineData("season", "series", 2L, null)]
    [InlineData("episode", "series", 0L, "e")]
    [InlineData("episode", " series ", 1L, "e")]
    [InlineData("episode", null, 2L, null)]
    [InlineData("episode", "", 2L, null)]
    [InlineData("episode", "e", 2L, null)]
    [InlineData("episode", "series", null, null)]
    [InlineData("movie", "series", 2L, null)]
    public void EpisodeAndSeasonRoutesCarryOneShotSeriesSelection(string type, string? seriesId, long? season, string? episodeId)
    {
        var detail = Feed().ToDetail(new CatalogItemDetail { ContentId = "e", Type = type, SeriesId = seriesId, SeasonNumber = season });
        var valid = type is "episode" or "season" && !string.IsNullOrWhiteSpace(seriesId) && seriesId.Trim() != "e" && season is not null;
        Assert.Equal(valid ? new SeriesDetailEntry(seriesId!.Trim(), season!.Value, episodeId) : null, detail.SeriesEntry);
    }

    [Fact]
    public void StarringUsesBillingOrderBeforeDeduplicatingAndLimitingNames()
    {
        var series = Series();
        series.Cast =
        [
            new CastCredit { Name = " Late ", Order = 20 },
            new CastCredit { Name = "Second", Order = 2 },
            new CastCredit { Name = "First", Order = 0 },
            new CastCredit { Name = "First", Order = 1 },
            new CastCredit { Name = "Third", Order = 3 },
            new CastCredit { Name = "Unbilled" },
        ];
        Assert.Equal("Starring First, Second, Third", DetailFeed.StarringCredit(series));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(0L, "")]
    [InlineData(22L, "22 min")]
    [InlineData(59L, "59 min")]
    [InlineData(60L, "1h 0m")]
    [InlineData(65L, "1h 5m")]
    [InlineData(125L, "2h 5m")]
    public void EpisodeHeroRuntimeUsesHoursAtSixtyMinutes(long? minutes, string expected)
    {
        var episode = new EpisodeCard("e", 1, 1, "", null, null, null, minutes, null, false);
        Assert.Equal(expected, string.Join("", episode.HeroFacts));
    }

    [Theory]
    [InlineData("A: B", "A", "B")]
    [InlineData("A — B", "A", "B")]
    [InlineData("A – B", "A", "B")]
    [InlineData("A - B", "A", "B")]
    [InlineData("A", "A", null)]
    [InlineData(": B", ": B", null)]
    [InlineData("A: ", "A: ", null)]
    public void TextTitlesSplitLikeTheOfficialHero(string raw, string title, string? subtitle) =>
        Assert.Equal((title, subtitle), DetailTitle.Split(raw));

    [Theory]
    [InlineData("series", "Series")]
    [InlineData("season", "Season")]
    [InlineData("episode", "Episode")]
    [InlineData("movie", "Movie")]
    [InlineData("other", "as")]
    public void WatchedActionsUseTheOfficialTypeSpecificLabels(string type, string noun)
    {
        Assert.Equal($"Mark {noun} Watched", DetailActions.WatchedLabel(type, false));
        Assert.Equal($"Mark {noun} Unwatched", DetailActions.WatchedLabel(type, true));
    }

    [Fact]
    public async Task SeasonWatchedUsesTheExistingGeneratedEndpointWithTheSeasonsContentId()
    {
        var handler = new StubHandler()
            .Enqueue("""{"items":[{"content_id":"series:show-S01","season_number":1,"episode_count":5,"user_data":{"played":true}}]}""")
            .Enqueue(HttpStatusCode.NoContent, "")
            .Enqueue(HttpStatusCode.NoContent, "");
        var feed = Feed(handler);
        var season = Assert.Single(await feed.SeasonsAsync("series:show"));
        Assert.Equal("series:show-S01", season.ContentId);
        Assert.True(season.Played);
        await feed.SetPlayedAsync(season.ContentId, true);
        await feed.SetPlayedAsync(season.ContentId, false);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Delete, handler.Requests[2].Method);
        Assert.EndsWith("/api/v2/watched/series%3Ashow-S01", handler.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void RatingsPreferTheServersListThenFallBackToImdbAndTmdb()
    {
        var served = new CatalogItemDetail { Ratings = [new CatalogRating { Source = "tmdb", Name = "TMDB", Display = "8.3" }], RatingImdb = 7.0 };
        Assert.Equal([new DisplayRating("tmdb", "TMDB", "8.3")], DetailFeed.Ratings(served));

        var older = new CatalogItemDetail { RatingImdb = 7.85, RatingTmdb = 8.25, RatingRtCritic = 90 };
        Assert.Equal([new DisplayRating("imdb", "IMDb", "7.9"), new DisplayRating("tmdb", "TMDB", "8.3")], DetailFeed.Ratings(older));
        Assert.Empty(DetailFeed.Ratings(new CatalogItemDetail { RatingTmdb = 0 }));
    }
}
