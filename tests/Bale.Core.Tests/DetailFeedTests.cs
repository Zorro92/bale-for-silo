using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

public class DetailFeedTests
{
    private static CatalogItemDetail Series() => new()
    {
        ContentId = "series-1", Type = "series", Title = "South Park", Year = 1997, SeasonCount = 29,
        Genres = ["Comedy", "Animation", "Satire"], ContentRating = "TV-MA",
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
            Type = "movie", Year = 2026, Runtime = 79, Genres = ["Documentary"],
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
        var feed = new DetailFeed(null!, null!);
        var detail = new ItemDetail("m", "movie", "M", null, null, null, null, null, [], null, [], [], null, [], null, "m", null, null, new(false, false, false));

        Assert.Equal("Play", PlayLabel.For(detail, null));
        Assert.Equal("Resume 12:34", PlayLabel.For(detail with { ResumeSeconds = 754 }, null));
        Assert.Equal("Resume 1:02:05", PlayLabel.For(detail with { ResumeSeconds = 3725 }, null));

        var series = detail with { Type = "series" };
        var nextUp = new EpisodeCard("e", 1, 2, "Ep", null, null, "EPISODE 2", null, false);
        Assert.Equal("Play S1:E2", PlayLabel.For(series, nextUp));
    }
}
