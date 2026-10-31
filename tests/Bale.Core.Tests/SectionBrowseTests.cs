using System.Net;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class SectionBrowseTests
{
    private static SiloClient Create(StubHandler stub)
    {
        ServerAddress.TryParse("silo.example.com", out var server);
        return new SiloClient(server, new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), stub);
    }

    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?')
        .Split('&').Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    [Theory]
    [InlineData(null, "home")]
    [InlineData("12", "library")]
    public async Task SectionSourceUsesFiftyItemCursorPagesInItsOriginalScope(string? libraryId, string scope)
    {
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"content_id":"1","type":"movie","title":"One"}],"total":51,"total_exact":true,"page":{"has_more":true,"next_cursor":"opaque +/=?&"}}""")
            .Enqueue("""{"items":[{"content_id":"2","type":"movie","title":"Two"}],"total":51,"total_exact":true,"page":{"has_more":false}}""");
        using var client = Create(stub);
        ICatalogGridSource source = new SectionBrowseSource("recent", "Recently Added", libraryId);

        var first = await source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null);
        var last = await source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, first.Continuation);

        Assert.Equal("Recently Added", source.Title);
        Assert.Equal("1", Assert.Single(first.Cards).ContentId);
        Assert.Equal("2", Assert.Single(last.Cards).ContentId);
        Assert.Equal(51, first.Total);
        Assert.True(first.TotalExact);
        Assert.False(last.HasMore);
        var query = Query(stub.Requests[0]);
        Assert.Equal("section", query["source"]);
        Assert.Equal("recent", query["section_id"]);
        Assert.Equal(scope, query["scope"]);
        Assert.Equal("50", query["limit"]);
        Assert.Equal("208", query["poster_width"]);
        Assert.Equal("372", query["still_width"]);
        Assert.Equal(libraryId, query.GetValueOrDefault("library_id"));
        Assert.DoesNotContain(new[] { "q", "person_id", "type", "sort", "order", "seek", "offset", "cursor" }, query.ContainsKey);
        Assert.Equal("opaque +/=?&", Query(stub.Requests[1])["cursor"]);
    }

    [Fact]
    public async Task MixedLibraryScopeFiltersLocallyAndDrainsPagesThatHaveNoMatchingCards()
    {
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"content_id":"s","type":"series"},{"content_id":"b","type":"ebook"}],"page":{"has_more":true,"next_cursor":"next"}}""")
            .Enqueue("""{"items":[{"content_id":"m","type":"movie"}],"page":{"has_more":false}}""");
        using var client = Create(stub);
        var source = new SectionBrowseSource("mixed", "New", "12", "movie");

        var page = await source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null);

        Assert.Equal("m", Assert.Single(page.Cards).ContentId);
        Assert.False(page.HasMore);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal("next", Query(stub.Requests[1])["cursor"]);
        Assert.All(stub.Requests, request => Assert.False(Query(request).ContainsKey("type")));
    }

    [Theory]
    [InlineData("movie", "movie", true)]
    [InlineData("movie", " Movies ", true)]
    [InlineData("movie", "film", true)]
    [InlineData("movie", "episode", false)]
    [InlineData("series", "series", true)]
    [InlineData("series", "show", true)]
    [InlineData("series", " SHOWS ", true)]
    [InlineData("series", "tv", true)]
    [InlineData("series", "episode", true)]
    [InlineData("series", "movie", false)]
    [InlineData(null, "audiobook", true)]
    public void MediaScopeMatchesTheOfficialLibraryAliases(string? scope, string type, bool expected) =>
        Assert.Equal(expected, new SectionBrowseSource("row", "Row", MediaScope: scope).MatchesMediaScope(type));

    [Fact]
    public async Task SectionGridsKeepAudiobooksButHideReadingMedia()
    {
        using var client = Create(new StubHandler().Enqueue("""{"items":[{"content_id":"a","type":"audiobook"},{"content_id":"m","type":"movie"},{"content_id":"e","type":"ebook"},{"content_id":"c","type":"comic"},{"content_id":"g","type":"manga"}],"page":{"has_more":false}}"""));

        var page = await client.Browse.SectionPageAsync(new SectionBrowseSource("row", "Row"), ArtworkWidths.Tv1080);

        Assert.Equal(["a", "m"], page.Cards.Select(card => card.ContentId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BlankSectionIsRejectedBeforeTransport(string id)
    {
        var stub = new StubHandler();
        using var client = Create(stub);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.Browse.SectionPageAsync(new SectionBrowseSource(id, "Row"), ArtworkWidths.Tv1080));
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("not-a-library")]
    public async Task InvalidLibraryScopeCannotAccidentallyReadHome(string libraryId)
    {
        var stub = new StubHandler();
        using var client = Create(stub);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Browse.SectionPageAsync(new SectionBrowseSource("row", "Row", libraryId), ArtworkWidths.Tv1080));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task HiddenOnlyPagesCannotLoopTheirContinuation()
    {
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"content_id":"b","type":"ebook"}],"page":{"has_more":true,"next_cursor":"again"}}""")
            .Enqueue("""{"items":[{"content_id":"c","type":"comic"}],"page":{"has_more":true,"next_cursor":"again"}}""");
        using var client = Create(stub);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.Browse.SectionPageAsync(new SectionBrowseSource("row", "Row"), ArtworkWidths.Tv1080));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task RemovedSectionKeepsItsNotFoundStatusForThePage()
    {
        using var client = Create(new StubHandler().Enqueue(HttpStatusCode.NotFound, """{"title":"Not Found","status":404}"""));

        var error = await Assert.ThrowsAsync<Problem>(() => client.Browse.SectionPageAsync(new SectionBrowseSource("removed", "Removed"), ArtworkWidths.Tv1080));

        Assert.Equal(404, error.ResponseStatusCode);
    }

    [Fact]
    public async Task CanceledSourceDoesNotSendARequest()
    {
        var stub = new StubHandler();
        using var client = Create(stub);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SectionBrowseSource("row", "Row")
            .LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null, cancellation.Token));
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(10, "10")]
    [InlineData(100, "20")]
    public async Task HomeAndLibrarySendOnlyTheAdvertisedRowCap(int? maximum, string? expected)
    {
        var stub = new StubHandler().Enqueue(JsonSerializer.Serialize(new { section_item_limit_max = maximum }))
            .Enqueue("""{"sections":[]}""").Enqueue("""{"sections":[]}""");
        using var client = Create(stub);

        await client.Browse.LoadHomeAsync(ArtworkWidths.Tv1080);
        await client.Browse.LoadLibraryAsync("12", ArtworkWidths.Tv1080);

        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal(expected, Query(stub.Requests[1]).GetValueOrDefault("item_limit"));
        Assert.Equal(expected, Query(stub.Requests[2]).GetValueOrDefault("item_limit"));
    }

    [Fact]
    public void SectionRowsCapOldServerPayloadsAtTwentyAndKeepTheirDestination()
    {
        using var client = Create(new StubHandler());
        var row = client.Browse.ToRow(new Section
        {
            Id = "recent", Title = "Recently Added", SectionType = "recently_added",
            Items = Enumerable.Range(1, 30).Select(id => new CatalogItem { ContentId = id.ToString(), Type = "movie" }).ToList(),
        }, "12");

        Assert.Equal(20, row.Cards.Count);
        Assert.Equal("20", row.Cards[^1].ContentId);
        Assert.Equal(new SectionBrowseSource("recent", "Recently Added", "12"), row.ExploreSource);
        Assert.True(row.OffersExploreAll);
        Assert.False(row.OffersSeeAll);
    }

    [Fact]
    public async Task ForYouDiscoveryRowsHaveNeitherSectionActionsNorTheSectionCap()
    {
        var json = JsonSerializer.Serialize(new
        {
            items = new[] { new { key = "discovery", title = "For you", kind = "recommendations",
                items = Enumerable.Range(1, 25).Select(id => new { content_id = id.ToString(), type = "movie" }) } },
        });
        using var client = Create(new StubHandler().Enqueue(json));

        var row = Assert.Single(await client.Browse.LoadForYouAsync(ArtworkWidths.Tv1080));

        Assert.Equal(25, row.Cards.Count);
        Assert.Null(row.ExploreSource);
        Assert.False(row.OffersExploreAll);
        Assert.False(row.OffersSeeAll);
    }
}
