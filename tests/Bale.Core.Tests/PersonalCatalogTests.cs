using System.Text.Json;
using Bale.Core.Browse;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class PersonalCatalogTests
{
    private static SiloClient Create(StubHandler stub)
    {
        ServerAddress.TryParse("silo.example.com", out var server);
        return new SiloClient(server, new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), stub);
    }

    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?')
        .Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    [Theory]
    [InlineData("watchlist", "Watchlist", "Your watchlist is empty")]
    [InlineData("favorites", "Favorites", "No favorites yet")]
    public async Task SavedListsUseCrossLibraryCatalogInStoredOrder(string kind, string title, string empty)
    {
        var stub = new StubHandler().Enqueue("""{"items":[{"content_id":"b","title":"Two","type":"series"},{"content_id":"a","title":"One","type":"movie"}],"total":3,"page":{"has_more":true,"next_cursor":"opaque +/=?&"}}""")
            .Enqueue("""{"items":[{"content_id":"c","type":"movie"}],"total":3,"page":{"has_more":false}}""");
        using var client = Create(stub);
        var source = LibraryCatalogSource.Personal(kind, client.LibraryBrowse);
        var widths = ArtworkWidths.Tv1080 with { Poster = 276 };
        var first = await source.LoadPageAsync(client.Browse, widths, null);
        var second = await source.LoadPageAsync(client.Browse, widths, first.Continuation);
        Assert.Equal(title, source.Title);
        Assert.Equal(empty, source.EmptyMessage);
        Assert.Equal(["b", "a"], first.Cards.Select(card => card.ContentId));
        Assert.Equal("c", Assert.Single(second.Cards).ContentId);
        Assert.False(second.HasMore);
        Assert.Equal(3, source.Total);
        var query = Query(stub.Requests[0]);
        Assert.EndsWith("/catalog", stub.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(kind, query["source"]);
        Assert.Equal("40", query["limit"]);
        Assert.Equal("276", query["poster_width"]);
        foreach (var key in new[] { "library_id", "type", "sort", "order", "offset", "seek", "cursor" }) Assert.False(query.ContainsKey(key));
        Assert.Equal("opaque +/=?&", Query(stub.Requests[1])["cursor"]);
        Assert.False(source.ShowsAlphabet);
    }

    [Theory]
    [InlineData("watchlist")]
    [InlineData("favorites")]
    public async Task SavedListFiltersAndFacetsKeepPersonalSource(string kind)
    {
        var stub = new StubHandler().Enqueue("""{"genres":["Drama"]}""").Enqueue("""{"items":[],"total":0,"page":{"has_more":false}}""");
        using var client = Create(stub);
        var source = LibraryCatalogSource.Personal(kind, client.LibraryBrowse);
        await source.FiltersAsync();
        Assert.Equal(kind, Query(stub.Requests[0])["source"]);
        Assert.Equal("false", Query(stub.Requests[0])["skip_technical"]);
        source.SetFilter(source.Query.Filter.SelectSort(LibrarySortOption.Title)
            .ApplyFacets(new LibraryFacetSelection().Toggle(LibraryFacet.Genre, "Drama")));
        var page = await source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null);
        Assert.Empty(page.Cards);
        Assert.False(page.HasMore);
        Assert.Equal(2, stub.Requests.Count); // No unrelated unfiltered library probe.
        Assert.Equal(HttpMethod.Post, stub.Requests[1].Method);
        using var document = JsonDocument.Parse(stub.RequestBodies[1]!);
        var body = document.RootElement;
        Assert.Equal(kind, body.GetProperty("source").GetString());
        Assert.Equal("title", body.GetProperty("sort").GetString());
        Assert.Equal("asc", body.GetProperty("order").GetString());
        Assert.Equal(40, body.GetProperty("limit").GetInt32());
        Assert.False(source.ShowsAlphabet); // Personal grids never get the library-only A–Z edge.
    }

    [Theory]
    [InlineData("watchlist")]
    [InlineData("favorites")]
    [InlineData("history")]
    public async Task EmptyPersonalListsFinishWithoutContinuation(string kind)
    {
        var stub = new StubHandler().Enqueue("""{"items":[],"page":{"has_more":false}}""");
        using var client = Create(stub);
        ICatalogGridSource source = kind == "history" ? new HistoryCatalogSource() : LibraryCatalogSource.Personal(kind, client.LibraryBrowse);
        var page = await source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null);
        Assert.Empty(page.Cards);
        Assert.False(page.HasMore);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task HistoryUsesChronologicalEndpointAndDeduplicatesAcrossEmptyPages()
    {
        var stub = new StubHandler().Enqueue("""{"items":[],"page":{"has_more":true,"next_cursor":"empty"}}""")
            .Enqueue("""{"items":[{"content_id":"s","type":"series","title":"Series","poster_url":"/series.jpg","watch":{"media_item_id":"episode-2","watched_at":"2026-01-02T00:00:00Z","duration_seconds":60,"completed":true}}],"page":{"has_more":true,"next_cursor":"opaque +/=?&"}}""")
            .Enqueue("""{"items":[{"content_id":"s","type":"series"},{"content_id":"book","type":"ebook"}],"page":{"has_more":true,"next_cursor":"duplicates"}}""")
            .Enqueue("""{"items":[{"content_id":"m","type":"movie","title":"Older"}],"page":{"has_more":false}}""");
        using var client = Create(stub);
        var source = new HistoryCatalogSource();
        var first = await source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null);
        var second = await source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, first.Continuation);
        var card = Assert.Single(first.Cards);
        Assert.Equal("s", card.ContentId); // Navigate to the card, not the episode watch witness.
        Assert.Equal("https://silo.example.com/series.jpg", card.ImageUrl!.ToString());
        Assert.Equal("m", Assert.Single(second.Cards).ContentId);
        Assert.False(second.HasMore);
        Assert.Equal("Watch History", source.Title);
        Assert.Equal("No watch history yet", source.EmptyMessage);
        Assert.Equal(4, stub.Requests.Count);
        var query = Query(stub.Requests[0]);
        Assert.EndsWith("/history", stub.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("40", query["limit"]);
        Assert.Equal("208", query["poster_width"]);
        foreach (var key in new[] { "source", "sort", "offset", "cursor" }) Assert.False(query.ContainsKey(key));
        Assert.Equal("empty", Query(stub.Requests[1])["cursor"]);
        Assert.Equal("opaque +/=?&", Query(stub.Requests[2])["cursor"]);
        Assert.Equal("duplicates", Query(stub.Requests[3])["cursor"]);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("unexpected", false)]
    public async Task InvalidHistoryContinuationIsRejected(string cursor, bool hasMore)
    {
        var json = JsonSerializer.Serialize(new { items = Array.Empty<object>(), page = new { has_more = hasMore, next_cursor = cursor } });
        using var client = Create(new StubHandler().Enqueue(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => new HistoryCatalogSource().LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null));
    }

    [Fact]
    public async Task HistoryRejectsRepeatedForeignAndChangedArtworkContinuations()
    {
        const string page = """{"items":[{"content_id":"m","type":"movie"}],"page":{"has_more":true,"next_cursor":"same"}}""";
        var stub = new StubHandler().Enqueue(page).Enqueue(page);
        using var client = Create(stub);
        var source = new HistoryCatalogSource();
        var first = await source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HistoryCatalogSource().LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, first.Continuation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080 with { Poster = 300 }, first.Continuation));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, first.Continuation));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task CancelledHistoryNeverSendsARequest()
    {
        var stub = new StubHandler();
        using var client = Create(stub);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HistoryCatalogSource().LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null, cancellation.Token));
        Assert.Empty(stub.Requests);
    }
}
