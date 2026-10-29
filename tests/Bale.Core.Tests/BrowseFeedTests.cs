using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Bale.Core.Browse;

namespace Bale.Core.Tests;

public class BrowseFeedTests
{
    private static BrowseFeed Feed(string server = "silo.example.com")
    {
        ServerAddress.TryParse(server, out var address);
        var connection = new SiloConnection(address, new ClientIdentity("d", "x", "xbox", "Bale"), transport: new StubHandler());
        return new BrowseFeed(connection.Api, address);
    }

    [Fact]
    public void ContinueWatchingRowIsLandscapeWithProgressAndEpisodeLabels()
    {
        var section = new Section
        {
            Id = "cw", Title = "Continue Watching", SectionType = "continue_watching",
            Items =
            [
                new CatalogItem
                {
                    ContentId = "e1", Type = "episode", Title = "The Rains", SeriesTitle = "Dark",
                    SeasonNumber = 1, EpisodeNumber = 5, ItemSource = "in_progress",
                    PositionSeconds = 900, DurationSeconds = 3600,
                    BackdropUrl = "/api/v2/artwork/still.jpg?sig=x", SeriesBackdropUrl = "/api/v2/artwork/series.jpg",
                },
            ],
        };

        var row = Feed().ToRow(section);

        Assert.Equal(CardShape.Landscape, row.Shape);
        var card = Assert.Single(row.Cards);
        Assert.Equal("Dark", card.Title);
        Assert.Equal("The Rains", card.Subtitle);
        Assert.Equal("S1 E5", card.EpisodeCode);
        Assert.Equal(0.25, card.Progress);
        Assert.Equal("https://silo.example.com/api/v2/artwork/still.jpg?sig=x", card.ImageUrl!.ToString());
        Assert.Equal("https://silo.example.com/api/v2/artwork/series.jpg", card.BackdropUrl!.ToString());
    }

    [Fact]
    public void MovieRowIsPosterWithMetaLine()
    {
        var section = new Section
        {
            Id = "ra", Title = "Recently Added", SectionType = "recently_added",
            Items =
            [
                new CatalogItem
                {
                    ContentId = "m1", Type = "movie", Title = "Dune", Year = 2021, ContentRating = "PG-13",
                    Runtime = 155, Genres = ["Science Fiction", "Adventure", "Drama"], PosterUrl = "https://cdn.example.com/p.jpg",
                },
            ],
        };

        var card = Assert.Single(Feed().ToRow(section).Cards);

        Assert.Equal(CardShape.Poster, Feed().ToRow(section).Shape);
        Assert.Equal("2021", card.Subtitle);
        Assert.Equal("2021 · 2h 35m · Science Fiction · Adventure", card.Meta);
        Assert.Equal(["PG-13"], card.Badges);
        Assert.Equal("https://cdn.example.com/p.jpg", card.ImageUrl!.ToString());
        Assert.Null(card.Progress);
    }

    [Fact]
    public void RelativeArtworkKeepsServerSubpath() =>
        Assert.Equal("https://example.com/silo/api/v2/artwork/a.jpg", Feed("https://example.com/silo").Resolve("/api/v2/artwork/a.jpg")!.ToString());
}

public class MarqueeMetaTests
{
    [Fact]
    public void EpisodeMetaMatchesAndroidTv()
    {
        var badges = BrowseFeed.Badges(new CatalogItemOverlay { Resolution = "2160p", Hdr = "HDR10", Audio = "EAC3" }, "TV-MA");
        var card = new MediaCard("e1", "episode", "Paradise", "Exodus", null, null, null, null, null, 0.6, 18, null, "s1", badges, "S2 E8", "Exodus", "52 min");

        Assert.Equal(["4K", "HDR", "EAC3", "TV-MA"], card.Badges);
        Assert.Equal("S2 E8 · Exodus · 52 min · 18m left", card.MarqueeMeta);
    }

    [Fact]
    public void DolbyVisionAndAtmosGetTheirOwnBadges() =>
        Assert.Equal(["DOLBY VISION", "ATMOS"], BrowseFeed.Badges(new CatalogItemOverlay { Hdr = "DV HDR10", Audio = "TrueHD Atmos" }));

    [Theory]
    [InlineData(52, "52 min")]
    [InlineData(118, "1h 58m")]
    public void RuntimeFormat(long minutes, string expected) => Assert.Equal(expected, BrowseFeed.Runtime(minutes));
}
