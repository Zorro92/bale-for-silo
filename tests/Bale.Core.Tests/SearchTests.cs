using System.Net;
using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class SearchFeedTests
{
    private static SiloClient Create(StubHandler stub)
    {
        ServerAddress.TryParse("silo.example.com", out var server);
        return new SiloClient(server, new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), stub);
    }

    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?')
        .Split('&').Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    [Fact]
    public async Task SearchUsesFortyItemCursorPagesAndPreservesServerTotals()
    {
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"content_id":"m1","type":"movie","title":"One"}],"total":1200,"total_exact":false,"page":{"has_more":true,"next_cursor":"opaque +/=?&"}}""")
            .Enqueue("""{"items":[{"content_id":"m2","type":"movie","title":"Two"}],"total":2,"total_exact":true,"page":{"has_more":false}}""");
        using var client = Create(stub);

        var first = await client.Browse.SearchPageAsync(" x ", ArtworkWidths.Tv1080, "movie");
        var last = await client.Browse.SearchPageAsync("x", ArtworkWidths.Tv1080, "movie", first.Continuation);

        Assert.Equal("m1", Assert.Single(first.Cards).ContentId);
        Assert.Equal(1200, first.Total);
        Assert.False(first.TotalExact);
        Assert.True(first.HasMore);
        Assert.Equal("m2", Assert.Single(last.Cards).ContentId);
        Assert.Equal(2, last.Total);
        Assert.True(last.TotalExact);
        Assert.False(last.HasMore);
        var initialQuery = Query(stub.Requests[0]);
        Assert.Equal("query", initialQuery["source"]);
        Assert.Equal("x", initialQuery["q"]);
        Assert.Equal("movie", initialQuery["type"]);
        Assert.Equal("40", initialQuery["limit"]);
        Assert.Equal("208", initialQuery["poster_width"]);
        Assert.Equal("372", initialQuery["still_width"]);
        Assert.False(initialQuery.ContainsKey("cursor"));
        Assert.False(initialQuery.ContainsKey("seek"));
        Assert.Equal("opaque +/=?&", Query(stub.Requests[1])["cursor"]);
    }

    [Fact]
    public async Task AllDrainsHiddenOnlyPagesWithoutUsingAnOffset()
    {
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"content_id":"b","type":"ebook"},{"content_id":"a","type":"audiobook"}],"page":{"has_more":true,"next_cursor":"next"}}""")
            .Enqueue("""{"items":[{"content_id":"e","type":"episode","title":"Pilot","series_title":"Series","year":2025,"series_poster_url":"/art/series.jpg"}],"total":3,"total_exact":true,"page":{"has_more":false}}""");
        using var client = Create(stub);

        var page = await client.Browse.SearchPageAsync("p", ArtworkWidths.Tv1080);

        var card = Assert.Single(page.Cards);
        Assert.Equal("Pilot", card.Title);
        Assert.Equal("2025", card.Subtitle);
        Assert.Equal("https://silo.example.com/art/series.jpg", card.ImageUrl!.ToString());
        Assert.False(page.HasMore);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal("next", Query(stub.Requests[1])["cursor"]);
        Assert.False(Query(stub.Requests[0]).ContainsKey("type"));
    }

    [Fact]
    public async Task AudiobooksAreVisibleOnlyWhenEnabledAndReadingIsAlwaysHidden()
    {
        const string response = """{"items":[{"content_id":"a","type":"audiobook"},{"content_id":"b","type":"manga"},{"content_id":"c","type":"comic"}],"page":{"has_more":false}}""";
        var stub = new StubHandler().Enqueue(response).Enqueue(response);
        using var client = Create(stub);

        Assert.Empty((await client.Browse.SearchPageAsync("a", ArtworkWidths.Tv1080)).Cards);
        Assert.Equal("a", Assert.Single((await client.Browse.SearchPageAsync("a", ArtworkWidths.Tv1080, showAudiobooks: true)).Cards).ContentId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingCursorIsRejectedInsteadOfRepeatingTheFirstPage(string? cursor)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            items = new[] { new { content_id = "m", type = "movie" } },
            page = new { has_more = true, next_cursor = cursor },
        });
        using var client = Create(new StubHandler().Enqueue(json));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.Browse.SearchPageAsync("x", ArtworkWidths.Tv1080));
    }

    [Fact]
    public async Task ContinuationRejectsNonconsecutiveCursorLoops()
    {
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"content_id":"1","type":"movie"}],"page":{"has_more":true,"next_cursor":"a"}}""")
            .Enqueue("""{"items":[{"content_id":"2","type":"movie"}],"page":{"has_more":true,"next_cursor":"b"}}""")
            .Enqueue("""{"items":[{"content_id":"3","type":"movie"}],"page":{"has_more":true,"next_cursor":"a"}}""");
        using var client = Create(stub);

        var first = await client.Browse.SearchPageAsync("x", ArtworkWidths.Tv1080);
        var second = await client.Browse.SearchPageAsync("x", ArtworkWidths.Tv1080, continuation: first.Continuation);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.Browse.SearchPageAsync("x", ArtworkWidths.Tv1080, continuation: second.Continuation));
    }

    [Fact]
    public async Task EmptyRawPageStopsPagingEvenIfServerClaimsMore()
    {
        using var client = Create(new StubHandler().Enqueue("""{"items":[],"page":{"has_more":true}}"""));

        var page = await client.Browse.SearchPageAsync("x", ArtworkWidths.Tv1080);

        Assert.Empty(page.Cards);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task CanceledSearchDoesNotSendARequest()
    {
        var stub = new StubHandler();
        using var client = Create(stub);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Browse.SearchPageAsync("x", ArtworkWidths.Tv1080, cancellationToken: cancellation.Token));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task PeopleAreNeverRequestedWithoutAccessScopedSearchSupport()
    {
        var stub = new StubHandler().Enqueue("""{"people_media_scope":false}""");
        using var client = Create(stub);

        Assert.Empty(await client.Browse.SearchPeopleAsync("Nolan"));
        Assert.EndsWith("/catalog/search/capabilities", Assert.Single(stub.Requests).RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("series")]
    [InlineData("audiobook")]
    public async Task PeopleUseTheSelectedMediaScope(string type)
    {
        var stub = new StubHandler().Enqueue("""{"people_media_scope":true}""")
            .Enqueue("""{"items":[{"id":"7","name":"Nolan","photo_url":"/art/person.jpg"}]}""");
        using var client = Create(stub);

        var person = Assert.Single(await client.Browse.SearchPeopleAsync(" Nolan ", type));

        Assert.Equal("Nolan", person.Name);
        Assert.Equal("https://silo.example.com/art/person.jpg", person.PhotoUrl!.ToString());
        Assert.Equal(type, Query(stub.Requests[1])["media_scope"]);
        Assert.Equal("Nolan", Query(stub.Requests[1])["q"]);
        Assert.Equal("20", Query(stub.Requests[1])["limit"]);
    }

    [Fact]
    public async Task AllPeopleMergeVideoAndAudiobooksAndRecheckCapabilitiesEachTime()
    {
        var stub = new StubHandler().Enqueue("""{"people_media_scope":true}""")
            .Enqueue("""{"items":[{"id":"1","name":"Other"},{"id":"2","name":"Nolan"}]}""")
            .Enqueue("""{"items":[{"id":"2","name":"Nolan"},{"id":"3","name":"Another"}]}""")
            .Enqueue("""{"people_media_scope":false}""");
        using var client = Create(stub);

        var people = await client.Browse.SearchPeopleAsync("nolan", showAudiobooks: true);
        var afterAccessChange = await client.Browse.SearchPeopleAsync("nolan", showAudiobooks: true);

        Assert.Equal(["2", "3", "1"], people.Select(person => person.Id));
        Assert.Equal(["video", "audiobook"], stub.Requests.Skip(1).Take(2).Select(request => Query(request)["media_scope"]));
        Assert.Empty(afterAccessChange);
        Assert.Equal(4, stub.Requests.Count);
    }

    [Fact]
    public async Task FailedPeopleScopeDoesNotDiscardTheOtherScopeOrReorderItsRanking()
    {
        var stub = new StubHandler().Enqueue("""{"people_media_scope":true}""")
            .Enqueue(HttpStatusCode.Forbidden, """{"title":"Forbidden","status":403}""")
            .Enqueue("""{"items":[{"id":"9","name":"Zed"},{"id":"2","name":"Alpha"}]}""");
        using var client = Create(stub);

        Assert.Equal(["9", "2"], (await client.Browse.SearchPeopleAsync("x", showAudiobooks: true)).Select(person => person.Id));
    }

    [Fact]
    public async Task PartialPeopleEnvelopeIsNotPresentedAsACompleteRow()
    {
        var stub = new StubHandler().Enqueue("""{"people_media_scope":true}""")
            .Enqueue("""{"items":[{"id":"1","name":"Hidden"}],"page":{"has_more":true,"next_cursor":"next"}}""");
        using var client = Create(stub);

        Assert.Empty(await client.Browse.SearchPeopleAsync("x"));
    }

    [Fact]
    public void MultiScopePeopleRankExactNamesAndNumericIdsBeforeLimiting()
    {
        SearchPerson Person(string id, string name) => new(id, name, null);
        var people = BrowseFeed.MergePeople(" nolan ",
        [
            [Person("10", "Nolan"), Person("2", "nolan"), Person("3", "Alpha")],
            [Person("2", "nolan"), Person("1", " Nolan "), .. Enumerable.Range(20, 30).Select(id => Person(id.ToString(), "Zed"))],
        ]);

        Assert.Equal(["1", "2", "10", "3"], people.Take(4).Select(person => person.Id));
        Assert.Equal(20, people.Count);
        Assert.Equal(20, people.Select(person => person.Id).Distinct().Count());
    }
}

public class SearchResultsTests
{
    private static MediaCard Card(string id) => new(id, "movie", id, null, null, null, null, null, null, null, null, null, null, [], null, null, null);
    private static SearchResultPage Page(bool more, params MediaCard[] cards) =>
        new(cards, 9999, false, more ? new SearchContinuation("next", ["next"]) : null);

    [Fact]
    public void AppendingDeduplicatesWithoutReplacingExistingCardsAndCountsVisibleAllResults()
    {
        var state = new SearchResults();
        var firstCard = Card("1");
        var first = state.BeginSearch("x", SearchMediaFilter.All)!;
        Assert.True(state.ApplyPage(first, Page(true, firstCard, Card("2"))));
        Assert.Equal("2+ results", state.Status);
        var more = state.BeginMore()!;
        Assert.Null(state.BeginMore());

        Assert.True(state.ApplyPage(more, Page(false, Card("1"), Card("3"))));

        Assert.Equal(["1", "2", "3"], state.Items.Select(card => card.ContentId));
        Assert.Same(firstCard, state.Items[0]);
        Assert.Equal(3, state.Total);
        Assert.True(state.TotalExact);
        Assert.Equal("3 results", state.Status);
        Assert.False(state.HasMore);
        Assert.Null(state.BeginMore());
    }

    [Fact]
    public void QueryEditInvalidatesOldPagesAndPeopleBeforeTheNextDebounceStarts()
    {
        var state = new SearchResults();
        state.ApplyPage(state.BeginSearch("old", SearchMediaFilter.All)!, Page(true, Card("1")));
        var more = state.BeginMore()!;

        state.ChangeQuery("new", SearchMediaFilter.All);

        Assert.False(state.ApplyPage(more, Page(false, Card("stale"))));
        Assert.False(state.ApplyPeople(more.Generation, [new SearchPerson("7", "Stale", null)]));
        Assert.False(state.Fail(more, new HttpRequestException()));
        Assert.Equal("1", Assert.Single(state.Items).ContentId);
        Assert.Empty(state.People);
        Assert.Null(state.BeginMore());
        Assert.False(state.IsLoadingMore);
        Assert.Null(state.Error);
    }

    [Fact]
    public void FilterEditRejectsBothSuccessAndErrorFromThePreviousSearch()
    {
        var state = new SearchResults();
        var old = state.BeginSearch("x", SearchMediaFilter.All)!;
        var current = state.BeginSearch("x", SearchMediaFilter.Movies)!;

        Assert.False(state.ApplyPage(old, Page(false, Card("old"))));
        Assert.False(state.Fail(old, new HttpRequestException()));
        Assert.True(state.IsSearching);
        Assert.Equal("Searching…", state.Status);
        Assert.True(state.ApplyPage(current, new SearchResultPage([Card("new")], 1200, false, null)));
        Assert.Equal("About 1200 results", state.Status);
    }

    [Fact]
    public void BlankQueryClearsEverySectionAndAnyNonblankQueryCanSearch()
    {
        var state = new SearchResults();
        var first = state.BeginSearch(" x ", SearchMediaFilter.All)!;
        Assert.Equal("x", first.Query);
        state.ApplyPage(first, Page(true, Card("1")));
        state.ApplyPeople(first.Generation, [new SearchPerson("7", "X", null)]);

        Assert.Null(state.BeginSearch(" \t ", SearchMediaFilter.All));
        Assert.Empty(state.Items);
        Assert.Empty(state.People);
        Assert.Null(state.Status);
        Assert.False(state.IsSearching);
        Assert.False(state.IsLoadingMore);
        Assert.False(state.HasMore);
        Assert.Equal(0, state.Total);
        Assert.Null(state.Error);
    }

    [Fact]
    public void FailedPaginationKeepsCardsAndCursorAndRequiresExplicitRetry()
    {
        var state = new SearchResults();
        state.ApplyPage(state.BeginSearch("x", SearchMediaFilter.All)!, Page(true, Card("1")));
        var more = state.BeginMore()!;

        Assert.True(state.Fail(more, new HttpRequestException("https://secret.example/token")));
        Assert.Equal("1", Assert.Single(state.Items).ContentId);
        Assert.True(state.HasMore);
        Assert.False(state.IsLoadingMore);
        Assert.Equal("Couldn't update results", state.Status);
        Assert.Null(state.BeginMore());
        Assert.Same(more.Continuation, state.BeginMore(retry: true)!.Continuation);
    }

    [Fact]
    public void DuplicateOnlyPageCannotCauseAnAutomaticRetryStorm()
    {
        var state = new SearchResults();
        state.ApplyPage(state.BeginSearch("x", SearchMediaFilter.All)!, Page(true, Card("1")));

        state.ApplyPage(state.BeginMore()!, Page(true, Card("1")));

        Assert.Single(state.Items);
        Assert.NotNull(state.Error);
        Assert.Null(state.BeginMore());
    }

    [Theory]
    [InlineData(-1, 40, false)]
    [InlineData(33, 40, false)]
    [InlineData(34, 40, true)]
    [InlineData(39, 40, true)]
    [InlineData(0, 1, true)]
    [InlineData(0, 0, false)]
    public void PaginationStartsWithinSixVisibleItems(int lastVisible, int count, bool expected) =>
        Assert.Equal(expected, SearchResults.IsNearEnd(lastVisible, count));
}

public class SearchPresentationTests
{
    [Theory]
    [InlineData("", 7, true, "error", true, true, true, null)]
    [InlineData(" \t ", 7, false, null, false, false, false, null)]
    [InlineData("x", 0, true, "error", false, false, false, "Searching…")]
    [InlineData("x", 7, false, "error", false, false, false, "Couldn't update results")]
    [InlineData("x", 0, false, null, false, false, true, "No matching titles")]
    [InlineData("x", 0, false, null, false, true, false, "No results")]
    [InlineData("x", 1, false, null, false, false, false, "1 result")]
    [InlineData("x", 1, false, null, true, true, false, "1 result")]
    [InlineData("x", 1, false, null, false, true, false, "About 1 results")]
    [InlineData("x", 40, false, null, true, true, false, "40+ results")]
    [InlineData("x", 42, false, null, false, true, false, "About 42 results")]
    [InlineData("x", 42, false, null, false, false, false, "42 results")]
    public void StatusCopiesOfficialPrecedence(string query, long total, bool searching, string? error,
        bool partial, bool estimated, bool people, string? expected) =>
        Assert.Equal(expected, SearchText.Status(query, total, searching, error, partial, estimated, people));

    [Fact]
    public void AvailableFiltersUseLibraryCapabilitiesNotMusicAsAnAudiobookProxy()
    {
        IReadOnlyList<SearchMediaFilter> For(params string[] types) => SearchMediaFilter.FromTabs(
            LibraryTabs.From(types.Select(type => new UserLibrary { Type = type })));

        Assert.Equal([SearchMediaFilter.All], For());
        Assert.Equal([SearchMediaFilter.All], For("music", "audio", "ebook"));
        Assert.Equal([SearchMediaFilter.All, SearchMediaFilter.Movies, SearchMediaFilter.Series], For("movies"));
        Assert.Equal([SearchMediaFilter.All, SearchMediaFilter.Movies, SearchMediaFilter.Series], For("series"));
        Assert.Equal([SearchMediaFilter.All, SearchMediaFilter.Audiobooks], For(" AUDIOBOOKS "));
        Assert.Equal([SearchMediaFilter.All, SearchMediaFilter.Movies, SearchMediaFilter.Series, SearchMediaFilter.Audiobooks], For("mixed", "audiobook"));
    }

    [Fact]
    public void PromptsAndEmptyDescriptionsUseOfficialConjunctions()
    {
        Assert.Equal("Search your library", SearchText.Prompt([SearchMediaFilter.All]));
        Assert.Equal("Search audiobooks", SearchText.Prompt([SearchMediaFilter.All, SearchMediaFilter.Audiobooks]));
        Assert.Equal("Search movies and series", SearchText.Prompt([SearchMediaFilter.All, SearchMediaFilter.Movies, SearchMediaFilter.Series]));
        Assert.Equal("Find movies, series, and audiobooks in one place.", SearchText.Description(
            [SearchMediaFilter.All, SearchMediaFilter.Movies, SearchMediaFilter.Series, SearchMediaFilter.Audiobooks]));
    }

    [Fact]
    public void ErrorCopyUsesSafeProblemDetailsAndNeverRawNetworkExceptions()
    {
        Assert.Equal("Try later.", SearchText.ErrorMessage(new Problem { Detail = "Try later.", Title = "Unavailable" }));
        Assert.Equal("Unavailable", SearchText.ErrorMessage(new Problem { Title = "Unavailable" }));
        Assert.Equal("Unable to connect to the server.", SearchText.ErrorMessage(new HttpRequestException("https://secret.example/token")));
        Assert.Equal("The request timed out. Try again.", SearchText.ErrorMessage(new TaskCanceledException()));
        Assert.Equal("Search failed", SearchText.ErrorMessage(new Exception("internal stack trace")));
    }
}
