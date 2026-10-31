using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Bale.Core.Detail;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Tests;

public class DetailSectionsTests
{
    private static SiloClient Client(StubHandler? stub = null) => new(new Uri("https://silo.example.com"), new ClientIdentity("d", "Test", "xbox", "Bale"), stub ?? new StubHandler());

    [Fact]
    public void TrailersKeepLocalFirstServerOrderAndDeduplicateNamespacedKeys()
    {
        using var client = Client();
        var detail = client.Detail.ToDetail(new CatalogItemDetail
        {
            Extras =
            [
                new ItemExtraInfo { ContentId = "same", Title = "  Making Of  ", DurationSeconds = 65, StillUrl = "/art/extra.jpg" },
                new ItemExtraInfo { ContentId = "same", Title = "Duplicate" },
                new ItemExtraInfo { ContentId = "second", Kind = "behind_the_scenes", DurationSeconds = 3725 },
            ],
            Videos =
            [
                new ItemVideoInfo { Site = "YouTube", SiteKey = "same", Name = " Trailer " },
                new ItemVideoInfo { Site = "YouTube", SiteKey = "same", Name = "Duplicate" },
                new ItemVideoInfo { Site = "vimeo", SiteKey = "unsupported" },
                new ItemVideoInfo { Site = "youtube", SiteKey = "  " },
                new ItemVideoInfo { Site = "youtube", SiteKey = "teaser", Kind = "teaser" },
            ],
        });

        Assert.Equal(["local:same", "local:second", "remote:YouTube:same", "remote:youtube:teaser"], detail.Trailers.Select(card => card.Key));
        Assert.Equal(["Making Of", "Behind the Scenes", "Trailer", "Teaser"], detail.Trailers.Select(card => card.Title));
        Assert.Equal(["1:05", "1:02:05", "YouTube", "YouTube"], detail.Trailers.Select(card => card.Secondary));
        Assert.Equal("https://silo.example.com/art/extra.jpg", detail.Trailers[0].ThumbnailUrl!.AbsoluteUri);
        Assert.Null(detail.Trailers[1].ThumbnailUrl);
        Assert.Equal("https://i.ytimg.com/vi/same/hqdefault.jpg", detail.Trailers[2].ThumbnailUrl!.AbsoluteUri);
    }

    [Fact]
    public void EmptySupportingDataCreatesNoSectionsAndCastRetainsRoutablePersonId()
    {
        using var client = Client();
        var empty = client.Detail.ToDetail(new CatalogItemDetail());
        Assert.Empty(empty.Trailers);
        Assert.Empty(empty.Details);
        var detail = client.Detail.ToDetail(new CatalogItemDetail
        {
            Type = "series",
            Cast = [new CastCredit { Name = " A ", Character = "One|Two", PersonId = " person-1 " }],
        });
        var cast = Assert.Single(detail.Cast);
        Assert.Equal("person-1", cast.PersonId);
        Assert.Equal("One", cast.Role);
        Assert.Equal("Recommended Series", detail.SimilarTitle);
        Assert.Equal("Related Movies", (detail with { Type = "movie" }).SimilarTitle);
        Assert.False((detail with { Type = "audiobook" }).ShowsSupportingSections);
    }

    [Fact]
    public void FactsMatchOfficialLabelsOrderingCreditTokenMatchingAndLimits()
    {
        var facts = DetailFeed.DetailFacts(new CatalogItemDetail
        {
            Type = "movie",
            Crew =
            [
                new CrewCredit { Name = " Zed ", Job = "Director" },
                new CrewCredit { Name = "Amy", Job = "Co-Director" },
                new CrewCredit { Name = "Zed", Job = "Director" },
                new CrewCredit { Name = "D", Job = "Screenplay" },
                new CrewCredit { Name = "C", Job = "Story" },
                new CrewCredit { Name = "B", Job = "Teleplay" },
                new CrewCredit { Name = "A", Job = "Writer" },
                new CrewCredit { Name = "Ignored", Job = "Producer" },
            ],
            Studios = ["S1", "S2", "S3", "S4"],
            Networks = ["N"],
            Countries = ["C"],
            Genres = ["Drama", "Comedy", "", "Action", "Thriller"],
            ReleaseDate = new Date(2025, 3, 2),
            FirstAirDate = new Date(2025, 4, 3),
            LastAirDate = new Date(2025, 5, 4),
        });
        Assert.Equal(["Director", "Writer", "Studio", "Network", "Country", "Genres", "Released", "First Aired", "Last Aired"], facts.Select(fact => fact.Label));
        Assert.Equal(["Amy, Zed", "A, B, C, …", "S1, S2, S3", "N", "C", "Drama, Comedy, Action, Thriller", "March 2, 2025", "April 3, 2025", "May 4, 2025"], facts.Select(fact => fact.Value));
    }

    [Fact]
    public void EpisodeFactsUseOneAbbreviatedAirDateAndWrittenByWithoutScreenplay()
    {
        var item = new CatalogItemDetail
        {
            Type = "episode",
            Crew = [new CrewCredit { Name = "A", Job = "Writer" }],
            ReleaseDate = new Date(2025, 1, 1),
            AirDate = new Date(2025, 2, 3),
            FirstAirDate = new Date(2000, 1, 1),
        };
        Assert.Equal([new DetailFact("Written by", "A"), new DetailFact("Aired", "Feb 3, 2025")], DetailFeed.DetailFacts(item));
        item.AirDate = null;
        Assert.Equal("Jan 1, 2025", DetailFeed.DetailFacts(item)[1].Value);
    }

    [Fact]
    public async Task RelatedCardsKeepRankExcludeTheSourceAndTvHiddenTypesAndUseTheTypedEndpoint()
    {
        var stub = new StubHandler().Enqueue("""{"items":[{"content_id":"self","type":"movie"},{"content_id":"z","type":"movie"},{"content_id":"book","type":"ebook"},{"content_id":"a","type":"series"},{"content_id":"z","type":"movie"}],"page":{"has_more":false}}""");
        using var client = Client(stub);
        var cards = await client.Detail.SimilarAsync("self");
        Assert.Equal(["z", "a"], cards.Select(card => card.ContentId));
        Assert.Equal("/api/v2/recommendations/similar/self", Assert.Single(stub.Requests).RequestUri!.AbsolutePath);
        Assert.Contains("limit=12", stub.Requests[0].RequestUri!.Query);
        Assert.Contains("poster_width=260", stub.Requests[0].RequestUri!.Query);
    }
}
