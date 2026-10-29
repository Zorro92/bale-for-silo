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
        Assert.Equal("S1 · E5 · The Rains", card.Subtitle);
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
        Assert.Equal("2021 · PG-13 · 2h 35m · Science Fiction · Adventure", card.Meta);
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
    public void EpisodeShowsBadgesCodeTitleAndTimeLeft()
    {
        var overlay = new CatalogItemOverlay { Resolution = "2160p", Hdr = "DV HDR10", Audio = "TrueHD Atmos" };
        var badges = BrowseFeed.Badges(overlay);
        var card = new MediaCard("e1", "episode", "Dark", null, null, null, null, null, null, 0.5, 23, null, "s1", badges, "S2 E7", "The Rains");

        Assert.Equal("4K · DOLBY VISION · ATMOS · S2 E7 · The Rains · 23m left", card.MarqueeMeta);
    }

    [Fact]
    public void PlainHdrIsShownAsSent() => Assert.Equal(["HDR10"], BrowseFeed.Badges(new CatalogItemOverlay { Resolution = "1080p", Hdr = "HDR10" }));
}
