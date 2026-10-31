using System.Text.Json;
using Bale.Core.Browse;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class LibraryBrowseTests
{
    private static SiloClient Create(StubHandler stub)
    {
        ServerAddress.TryParse("silo.example.com", out var server);
        return new SiloClient(server, new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), stub);
    }

    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?')
        .Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    private static LibraryBrowseQuery Library(LibraryBrowseFilter? filter = null) => new("7", "movie", filter ?? new());
    private const string LastPage = """{"items":[{"content_id":"m2","title":"Two","type":"movie"}],"page":{"has_more":false}}""";
    private const string MorePage = """{"items":[{"content_id":"m1","title":"One","type":"movie"}],"page":{"has_more":true,"next_cursor":"opaque +/=?&"}}""";

    [Fact]
    public async Task DefaultIsScopedNewestFirstWithHundredItemCursorPages()
    {
        var stub = new StubHandler().Enqueue(MorePage).Enqueue(LastPage);
        using var client = Create(stub);
        var first = await client.LibraryBrowse.LoadAsync(Library(), ArtworkWidths.Tv1080);
        var second = await client.LibraryBrowse.LoadAsync(Library(), ArtworkWidths.Tv1080, first.Continuation);

        Assert.Equal("m1", Assert.Single(first.Cards).ContentId);
        Assert.True(first.HasMore);
        Assert.Equal("m2", Assert.Single(second.Cards).ContentId);
        Assert.False(second.HasMore);
        var query = Query(stub.Requests[0]);
        Assert.Equal("query", query["source"]);
        Assert.Equal("7", query["library_id"]);
        Assert.Equal("movie", query["type"]);
        Assert.Equal("100", query["limit"]);
        Assert.Equal("-added_at", query["sort"]);
        Assert.Equal("208", query["poster_width"]);
        Assert.False(query.ContainsKey("cursor"));
        Assert.False(query.ContainsKey("seek"));
        Assert.False(query.ContainsKey("offset"));
        Assert.Equal("opaque +/=?&", Query(stub.Requests[1])["cursor"]);
    }

    [Fact]
    public async Task FacetsUsePostWithTypedGroupsAndArtworkInUrl()
    {
        var facets = new LibraryFacetSelection { MatchAll = false }.Toggle(LibraryFacet.Genre, "Sci-Fi & Fantasy")
            .Toggle(LibraryFacet.Decade, "2010").Toggle(LibraryFacet.WatchStatus, "unwatched")
            .Toggle(LibraryFacet.DynamicRange, "hdr");
        var filter = new LibraryBrowseFilter { Sort = LibrarySortOption.Title, Descending = false, Facets = facets, NamePrefix = "T" };
        var stub = new StubHandler().Enqueue(MorePage).Enqueue(LastPage);
        using var client = Create(stub);
        var page = await client.LibraryBrowse.LoadAsync(Library(filter), ArtworkWidths.Tv1080);
        await client.LibraryBrowse.LoadAsync(Library(filter), ArtworkWidths.Tv1080, page.Continuation);

        Assert.Equal(HttpMethod.Post, stub.Requests[0].Method);
        Assert.EndsWith("/catalog/query", stub.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("208", Query(stub.Requests[0])["poster_width"]);
        using var document = JsonDocument.Parse(stub.RequestBodies[0]!);
        var body = document.RootElement;
        Assert.Equal("query", body.GetProperty("source").GetString());
        Assert.Equal("7", body.GetProperty("library_id").GetString());
        Assert.Equal("movie", body.GetProperty("type").GetString());
        Assert.Equal("title", body.GetProperty("sort").GetString());
        Assert.Equal("asc", body.GetProperty("order").GetString());
        Assert.Equal("any", body.GetProperty("match").GetString());
        Assert.Equal("T", body.GetProperty("name_prefix").GetString());
        var groups = body.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(4, groups.Count);
        var rules = groups.SelectMany(group => group.GetProperty("rules").EnumerateArray()).ToList();
        Assert.Equal("contains", rules.Single(rule => rule.GetProperty("field").GetString() == "genre").GetProperty("op").GetString());
        var years = rules.Single(rule => rule.GetProperty("field").GetString() == "year").GetProperty("value");
        Assert.Equal(["2010", "2019"], years.EnumerateArray().Select(value => value.GetString()));
        Assert.False(rules.Single(rule => rule.GetProperty("field").GetString() == "watched").GetProperty("value").GetBoolean());
        Assert.True(rules.Single(rule => rule.GetProperty("field").GetString() == "hdr").GetProperty("value").GetBoolean());
        using var next = JsonDocument.Parse(stub.RequestBodies[1]!);
        Assert.Equal("opaque +/=?&", next.RootElement.GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task PrefixAndSortAreCompositeAndNumberBucketIsZeroThroughNine()
    {
        var filter = new LibraryBrowseFilter().SelectSort(LibrarySortOption.Title).SelectPrefix("#");
        var stub = new StubHandler().Enqueue(LastPage);
        using var client = Create(stub);
        await client.LibraryBrowse.LoadAsync(Library(filter), ArtworkWidths.Tv1080);
        var query = Query(stub.Requests[0]);
        Assert.Equal("title", query["sort"]);
        Assert.Equal("0-9", query["name_prefix"]);
        Assert.True(filter.ShowsAlphabet);
        Assert.False(filter.Descending);
        Assert.Equal("0-9", filter.SelectSort(LibrarySortOption.Title).NamePrefix);
        Assert.True(filter.SelectSort(LibrarySortOption.Title).Descending);
        Assert.Equal("Z–A", LibrarySortOption.Title.DirectionLabel(true));
        Assert.Equal("0-9", filter.SelectSort(LibrarySortOption.DateAdded).NamePrefix);
        Assert.Null(filter.SelectPrefix("All").NamePrefix);
        Assert.Null(filter.ApplyFacets(new()).NamePrefix);
    }

    [Fact]
    public void FacetsCountDimensionsAndWatchStatusIsSingleSelect()
    {
        var selection = new LibraryFacetSelection().Toggle(LibraryFacet.Genre, "Drama").Toggle(LibraryFacet.Genre, "Action")
            .Toggle(LibraryFacet.WatchStatus, "watched").Toggle(LibraryFacet.WatchStatus, "in_progress");
        Assert.Equal(2, selection.ActiveCount);
        Assert.Equal("in_progress", Assert.Single(selection.Selected(LibraryFacet.WatchStatus)));
        Assert.Empty(selection.Toggle(LibraryFacet.WatchStatus, "in_progress").Selected(LibraryFacet.WatchStatus));
        Assert.Equal(1, selection.Clear(LibraryFacet.Genre).ActiveCount);
        Assert.True(new LibraryFacetSelection { MatchAll = false }.CanReset);
        Assert.Equal(["Title", "Date Added", "Release Date", "Year", "Rating", "Runtime", "Resolution"], LibrarySortOption.Video.Select(option => option.Label));
    }

    [Fact]
    public async Task HiddenOnlyPageIsDrainedAndRepeatedCursorsAreRejected()
    {
        var stub = new StubHandler().Enqueue("""{"items":[{"content_id":"b","type":"ebook"}],"page":{"has_more":true,"next_cursor":"a"}}""")
            .Enqueue("""{"items":[{"content_id":"1","type":"movie"}],"page":{"has_more":true,"next_cursor":"b"}}""")
            .Enqueue("""{"items":[{"content_id":"2","type":"movie"}],"page":{"has_more":true,"next_cursor":"a"}}""");
        using var client = Create(stub);
        var page = await client.LibraryBrowse.LoadAsync(Library(), ArtworkWidths.Tv1080);
        Assert.Equal("1", Assert.Single(page.Cards).ContentId);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal("a", Query(stub.Requests[1])["cursor"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.LibraryBrowse.LoadAsync(Library(), ArtworkWidths.Tv1080, page.Continuation));
    }

    [Fact]
    public async Task CursorCannotBeReusedAfterChangingScopePrefixSortOrArtwork()
    {
        var stub = new StubHandler().Enqueue(MorePage);
        using var client = Create(stub);
        var first = await client.LibraryBrowse.LoadAsync(Library(), ArtworkWidths.Tv1080);
        foreach (var query in new[]
        {
            Library() with { MediaType = "series" }, Library() with { LibraryId = "8" },
            Library(new LibraryBrowseFilter().SelectPrefix("T")), Library(new LibraryBrowseFilter().SelectSort(LibrarySortOption.Title)),
        })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.LibraryBrowse.LoadAsync(query, ArtworkWidths.Tv1080, first.Continuation));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LibraryBrowse.LoadAsync(Library(), ArtworkWidths.Tv1080 with { Poster = 300 }, first.Continuation));
        Assert.Single(stub.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingCursorCannotRepeatPageOne(string? cursor)
    {
        var json = JsonSerializer.Serialize(new { items = new[] { new { content_id = "1", type = "movie" } }, page = new { has_more = true, next_cursor = cursor } });
        using var client = Create(new StubHandler().Enqueue(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.LibraryBrowse.LoadAsync(Library(), ArtworkWidths.Tv1080));
    }

    [Fact]
    public async Task EmptyFilteredResultProbesUnfilteredScopedLibrary()
    {
        var stub = new StubHandler().Enqueue("""{"items":[],"page":{"has_more":false}}""").Enqueue(LastPage);
        using var client = Create(stub);
        var page = await client.LibraryBrowse.LoadAsync(Library(new LibraryBrowseFilter().SelectPrefix("Z")), ArtworkWidths.Tv1080);
        Assert.True(page.LibraryHasItems);
        Assert.Empty(page.Cards);
        var probe = Query(stub.Requests[1]);
        Assert.Equal("movie", probe["type"]);
        Assert.Equal("7", probe["library_id"]);
        Assert.Equal("1", probe["limit"]);
        Assert.False(probe.ContainsKey("name_prefix"));
    }

    [Fact]
    public async Task TechnicalFacetsAreRequestedAndUserCollectionsKeepTheirSource()
    {
        var stub = new StubHandler().Enqueue("""{"genres":["Drama"],"technical":{"resolutions":["4K"],"audio_languages":["en"]}}""").Enqueue(LastPage);
        using var client = Create(stub);
        var facets = await client.LibraryBrowse.FiltersAsync("7");
        Assert.Equal("false", Query(stub.Requests[0])["skip_technical"]);
        Assert.Equal("4K", Assert.Single(LibraryFacet.Resolution.Options(facets)).Label);
        await client.LibraryBrowse.LoadAsync(Library() with { CollectionId = "12", UserCollection = true, Filter = new() { Sort = LibrarySortOption.CollectionOrder } }, ArtworkWidths.Tv1080);
        var query = Query(stub.Requests[1]);
        Assert.Equal("user_collection", query["source"]);
        Assert.Equal("12", query["collection_id"]);
        Assert.False(query.ContainsKey("sort"));
    }

    [Fact]
    public async Task CollectionGroupsAndUngroupedAreWovenWithoutLosingUserCollectionKind()
    {
        var stub = new StubHandler().Enqueue("""{"groups":[{"name":"Last","kind":"regular","sort_order":3,"collections":[{"id":"3","title":"Three"}]},{"name":"Mine","kind":"user_collections","sort_order":1,"collections":[{"id":"1","title":"One","poster_url":"/poster.jpg"}]}],"ungrouped":{"sort_order":2,"collections":[{"id":"2","title":"Two"}]}}""");
        using var client = Create(stub);
        var sections = await client.LibraryBrowse.CollectionsAsync("7");
        Assert.Equal(["Mine", "", "Last"], sections.Select(section => section.Name));
        Assert.True(sections[0].Collections[0].UserCollection);
        Assert.Equal("https://silo.example.com/poster.jpg", sections[0].Collections[0].Poster!.ToString());
    }

    [Fact]
    public async Task StateRejectsStalePagesAndDeduplicatesAppendWithoutReplacingEarlierCards()
    {
        var stub = new StubHandler().Enqueue(MorePage).Enqueue(MorePage);
        using var client = Create(stub);
        var page = await client.LibraryBrowse.LoadAsync(Library(), ArtworkWidths.Tv1080);
        var state = new LibraryBrowseState();
        var old = state.Begin(Library());
        var active = state.Begin(Library(new LibraryBrowseFilter().SelectPrefix("T")));
        Assert.False(state.Apply(old, page));
        Assert.False(state.Fail(old, new Exception("old error")));
        Assert.True(state.Apply(active, page));
        var card = Assert.Single(state.Items);
        var more = state.BeginMore()!;
        Assert.Null(state.BeginMore());
        Assert.True(state.Apply(more, page));
        Assert.Same(card, Assert.Single(state.Items));
        var retry = state.BeginMore()!;
        Assert.True(state.Fail(retry, new Exception("Offline")));
        Assert.Null(state.BeginMore());
        Assert.Equal("Offline", state.Error);
    }

    [Theory]
    [InlineData(0, -1, 0, 0)]
    [InlineData(5, 1, 0, 5)]
    [InlineData(6, -1, 0, 6)]
    [InlineData(5, 0, 1, 5)]
    [InlineData(4, 0, 1, 4)]
    [InlineData(3, 0, 1, 9)]
    [InlineData(9, 0, -1, 3)]
    [InlineData(9, 1, 0, 9)]
    public void GridMovesNeverWrapOrShiftColumnsAtAnIncompleteEdge(int index, int horizontal, int vertical, int expected) =>
        Assert.Equal(expected, LibraryGridNavigation.Move(index, 10, horizontal, vertical));

    [Fact]
    public async Task RecommendedHidesFeaturedRowsAndScopesMixedLibraryCardsAndExploreSources()
    {
        var stub = new StubHandler().Enqueue("{}").Enqueue("""{"sections":[{"id":"hero","featured":true,"items":[{"content_id":"h","type":"movie"}]},{"id":"mixed","title":"Mixed","items":[{"content_id":"m","type":"movie"},{"content_id":"s","type":"series"},{"content_id":"e","type":"episode"},{"content_id":"b","type":"ebook"}]}]}""");
        using var client = Create(stub);
        var rows = await client.Browse.LoadLibraryAsync("7", ArtworkWidths.Tv1080);
        var movies = Assert.Single(LibraryRecommendedRows.For(rows, "movie"));
        Assert.Equal("m", Assert.Single(movies.Cards).ContentId);
        Assert.Equal("movie", movies.ExploreSource!.MediaScope);
        Assert.Equal("7", movies.ExploreSource.LibraryId);
        Assert.Equal(["s", "e"], Assert.Single(LibraryRecommendedRows.For(rows, "series")).Cards.Select(card => card.ContentId));
        Assert.Equal(3, Assert.Single(LibraryRecommendedRows.For(rows, null)).Cards.Count);
    }

    [Fact]
    public async Task SharedGridAdapterKeepsLibraryQueryAndCursorFencing()
    {
        var stub = new StubHandler().Enqueue(MorePage).Enqueue(LastPage);
        using var client = Create(stub);
        var source = new LibraryCatalogSource("Movies", Library(), client.LibraryBrowse);
        var first = await source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null);
        var other = new LibraryCatalogSource("Other", Library(), client.LibraryBrowse);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, first.Continuation));
        source.SetFilter(new LibraryBrowseFilter().SelectSort(LibrarySortOption.Title));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, first.Continuation));
        var reloaded = await source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null);
        Assert.Equal("m2", Assert.Single(reloaded.Cards).ContentId);
        Assert.Equal("title", Query(stub.Requests[1])["sort"]);
        Assert.False(Query(stub.Requests[1]).ContainsKey("cursor"));
    }

    [Fact]
    public void PagingThresholdAndPreferencesMatchOfficialDefaults()
    {
        Assert.False(LibraryGridNavigation.IsNearEnd(51, 100));
        Assert.True(LibraryGridNavigation.IsNearEnd(52, 100));
        var filter = new LibraryBrowseFilter().SelectSort(LibrarySortOption.Title).SelectPrefix("T")
            .ApplyFacets(new LibraryFacetSelection { MatchAll = false }.Toggle(LibraryFacet.Genre, "Sci-Fi & Fantasy"));
        var saved = LibraryBrowsePreferences.Serialize(filter with { NamePrefix = "T" });
        var restored = LibraryBrowsePreferences.Deserialize(saved, "movie")!;
        Assert.Null(restored.NamePrefix);
        Assert.Equal(LibrarySortOption.Title, restored.Sort);
        Assert.False(restored.Descending);
        Assert.False(restored.Facets.MatchAll);
        Assert.Equal("Sci-Fi & Fantasy", Assert.Single(restored.Facets.Selected(LibraryFacet.Genre)));
        Assert.Null(LibraryBrowsePreferences.Deserialize("invalid", "movie"));
    }
}
