using System.Net;
using Bale.Core.Browse;
using Bale.Core.Connection;
using Bale.Core.Detail;
using Bale.Core.People;

namespace Bale.Core.Tests;

public class PersonFeedTests
{
    private static SiloClient Client(StubHandler stub) => new(new Uri("https://silo.example.com"), new ClientIdentity("d", "Test", "xbox", "Bale"), stub);
    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?')
        .Split('&').Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    [Fact]
    public async Task ProfileUsesTypedPersonEndpointAndNormalizesPresentationFields()
    {
        var stub = new StubHandler().Enqueue("""{"id":"person-7","name":"A Person","photo_url":"/art/person.jpg","bio":" A biography... ","birth_date":"1940-04-25","death_date":"2020-03-24","birthplace":"New York"}""");
        using var client = Client(stub);

        var person = await client.People.GetAsync("person-7");

        Assert.Equal("/api/v2/catalog/people/person-7", Assert.Single(stub.Requests).RequestUri!.AbsolutePath);
        Assert.Equal("person-7", person.Id);
        Assert.Equal("A Person", person.Name);
        Assert.Equal("https://silo.example.com/art/person.jpg", person.PhotoUrl!.AbsoluteUri);
        Assert.Equal("A biography.", person.Bio);
        Assert.Equal(new DateOnly(1940, 4, 25), person.BirthDate);
        Assert.Equal(new DateOnly(2020, 3, 24), person.DeathDate);
        Assert.Equal("New York", person.Birthplace);
    }

    [Fact]
    public async Task ProfileRetainsRequestedOpaqueIdWhenServerOmitsIt()
    {
        using var client = Client(new StubHandler().Enqueue("{}"));
        var person = await client.People.GetAsync("opaque-id");
        Assert.Equal("opaque-id", person.Id);
        Assert.Null(person.Bio);
        Assert.Null(person.PhotoUrl);
        Assert.Null(person.BirthDate);
        Assert.Null(person.DeathDate);
        Assert.Equal("?", person.Initials);
    }

    [Fact]
    public async Task WorksUseSixtyItemPersonCursorPagesWithoutSearchQueryOrOffset()
    {
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"content_id":"m1","type":"movie"}],"total":100,"page":{"has_more":true,"next_cursor":"opaque +/=?&"}}""")
            .Enqueue("""{"items":[{"content_id":"m2","type":"movie"}],"total":100,"page":{"has_more":false}}""");
        using var client = Client(stub);

        var first = await client.People.WorksAsync("person-7", PersonMediaFilter.Movies, ArtworkWidths.Tv1080);
        var last = await client.People.WorksAsync("person-7", PersonMediaFilter.Movies, ArtworkWidths.Tv1080, first.Continuation);

        Assert.Equal("m1", Assert.Single(first.Cards).ContentId);
        Assert.Equal(100, first.Total);
        Assert.Equal("m2", Assert.Single(last.Cards).ContentId);
        Assert.False(last.HasMore);
        var query = Query(stub.Requests[0]);
        Assert.Equal("person", query["source"]);
        Assert.Equal("person-7", query["person_id"]);
        Assert.Equal("movie", query["type"]);
        Assert.Equal("60", query["limit"]);
        Assert.Equal("208", query["poster_width"]);
        Assert.False(query.ContainsKey("q"));
        Assert.False(query.ContainsKey("cursor"));
        Assert.False(query.ContainsKey("seek"));
        Assert.Equal("opaque +/=?&", Query(stub.Requests[1])["cursor"]);
    }

    [Fact]
    public async Task AllWorksDrainHiddenReadingPagesAndHonorAudiobookVisibility()
    {
        const string visiblePage = """{"items":[{"content_id":"a","type":"audiobook"},{"content_id":"s","type":"series"}],"page":{"has_more":false}}""";
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"content_id":"b","type":"ebook"},{"content_id":"c","type":"comic"},{"content_id":"m","type":"manga"}],"page":{"has_more":true,"next_cursor":"after-reading"}}""")
            .Enqueue(visiblePage)
            .Enqueue(visiblePage);
        using var client = Client(stub);

        Assert.Equal(["a", "s"], (await client.People.WorksAsync("p", PersonMediaFilter.All, ArtworkWidths.Tv1080)).Cards.Select(card => card.ContentId));
        Assert.Equal("after-reading", Query(stub.Requests[1])["cursor"]);
        Assert.False(Query(stub.Requests[0]).ContainsKey("type"));
        Assert.Equal("s", Assert.Single((await client.People.WorksAsync("p", PersonMediaFilter.All, ArtworkWidths.Tv1080, showAudiobooks: false)).Cards).ContentId);
    }

    [Fact]
    public async Task PersonPagingRejectsRepeatedCursor()
    {
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"content_id":"1","type":"movie"}],"page":{"has_more":true,"next_cursor":"same"}}""")
            .Enqueue("""{"items":[{"content_id":"2","type":"movie"}],"page":{"has_more":true,"next_cursor":"same"}}""");
        using var client = Client(stub);
        var first = await client.People.WorksAsync("p", PersonMediaFilter.All, ArtworkWidths.Tv1080);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.People.WorksAsync("p", PersonMediaFilter.All, ArtworkWidths.Tv1080, first.Continuation));
    }

    [Fact]
    public async Task FiltersRequireLibrariesForOptionalTypesAndHideSuccessfulEmptyProbes()
    {
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"type":"audiobooks"}]}""")
            .Enqueue("""{"total":2}""")
            .Enqueue("""{"total":0}""")
            .Enqueue("""{"total":1}""")
            .Enqueue("""{"total":5}""");
        using var client = Client(stub);

        Assert.Equal([PersonMediaFilter.All, PersonMediaFilter.Movies, PersonMediaFilter.Audiobooks], await client.People.FiltersAsync("p"));
        Assert.EndsWith("/user/libraries", stub.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(["movie", "series", "audiobook", "music"], stub.Requests.Skip(1).Select(request => Query(request)["type"]));
        foreach (var request in stub.Requests.Skip(1))
        {
            var query = Query(request);
            Assert.Equal("person", query["source"]);
            Assert.Equal("p", query["person_id"]);
            Assert.Equal("1", query["limit"]);
        }
    }

    [Fact]
    public async Task EmptyLibraryAccessHidesOptionalFiltersEvenWhenTheyHaveContent()
    {
        var stub = new StubHandler().Enqueue("""{"items":[]}""")
            .Enqueue("""{"total":1}""").Enqueue("""{"total":1}""").Enqueue("""{"total":1}""").Enqueue("""{"total":1}""");
        using var client = Client(stub);
        Assert.Equal([PersonMediaFilter.All, PersonMediaFilter.Movies, PersonMediaFilter.Series], await client.People.FiltersAsync("p"));
    }

    [Fact]
    public async Task FailedLibraryAndTypeProbesRetainFiltersRatherThanHideUnknownContent()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.InternalServerError, """{"status":500}""")
            .Enqueue(HttpStatusCode.InternalServerError, """{"status":500}""")
            .Enqueue("""{"total":0}""").Enqueue("""{"total":1}""").Enqueue("""{"total":1}""");
        using var client = Client(stub);
        Assert.Equal([PersonMediaFilter.All, PersonMediaFilter.Movies, PersonMediaFilter.Audiobooks, PersonMediaFilter.Music], await client.People.FiltersAsync("p"));
    }

    [Fact]
    public async Task CastWithPersonIdDoesNotSearch()
    {
        var stub = new StubHandler();
        using var client = Client(stub);
        Assert.Equal("opaque-id", await client.People.ResolveAsync(new CastMember("Name", null, null) { PersonId = "opaque-id" }));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task CastLookupPrefersExactCaseInsensitiveNameThenServerFirstResult()
    {
        var stub = new StubHandler()
            .Enqueue("""{"items":[{"id":"first","name":"Similar Name"},{"id":"exact","name":"NAME"}]}""")
            .Enqueue("""{"items":[{"id":"first","name":"Similar Name"},{"id":"second","name":"Other"}]}""");
        using var client = Client(stub);
        Assert.Equal("exact", await client.People.ResolveAsync(new CastMember("Name", null, null)));
        Assert.Equal("first", await client.People.ResolveAsync(new CastMember("Name", null, null)));
        Assert.Equal("Name", Query(stub.Requests[0])["q"]);
        Assert.Equal("20", Query(stub.Requests[0])["limit"]);
    }

    [Theory]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":[{\"id\":\"partial\",\"name\":\"Name\"}],\"page\":{\"has_more\":true,\"next_cursor\":\"next\"}}")]
    public async Task UnresolvedAndPartialCastSearchesDoNotNavigate(string response)
    {
        using var client = Client(new StubHandler().Enqueue(response));
        Assert.Null(await client.People.ResolveAsync(new CastMember("Name", null, null)));
    }

    [Fact]
    public async Task FailedCastLookupIsHarmless()
    {
        using var client = Client(new StubHandler().Enqueue(HttpStatusCode.Forbidden, """{"status":403}"""));
        Assert.Null(await client.People.ResolveAsync(new CastMember("Name", null, null)));
    }

    [Fact]
    public async Task CanceledWorksDoesNotSendRequests()
    {
        var stub = new StubHandler();
        using var client = Client(stub);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.People.WorksAsync("p", PersonMediaFilter.All, ArtworkWidths.Tv1080, cancellationToken: cancellation.Token));
        Assert.Empty(stub.Requests);
    }
}

public class PersonPresentationTests
{
    private static PersonProfile Profile(string name = "A Person", DateOnly? birth = null, DateOnly? death = null, string? birthplace = null) =>
        new("p", name, null, null, birth, death, birthplace);

    [Theory]
    [InlineData(" a person third ", "AP")]
    [InlineData("single", "S")]
    [InlineData("", "?")]
    public void PortraitInitialsUseAtMostTwoWords(string name, string initials) => Assert.Equal(initials, Profile(name).Initials);

    [Fact]
    public void LivingMetadataUsesBirthdayAwareAgeAndOfficialBadgeOrder()
    {
        var person = Profile(birth: new DateOnly(1940, 4, 25), birthplace: " New York ");
        Assert.Equal(["Born Apr 25, 1940", "85 years old", "New York"], person.Metadata(new DateOnly(2026, 4, 24)));
        Assert.Equal(["Born Apr 25, 1940", "86 years old", "New York"], person.Metadata(new DateOnly(2026, 4, 25)));
    }

    [Fact]
    public void DeceasedMetadataUsesAgeAtDeathInsteadOfCurrentAge()
    {
        var person = Profile(birth: new DateOnly(1940, 4, 25), death: new DateOnly(2020, 3, 24));
        Assert.Equal(["Born Apr 25, 1940", "Died Mar 24, 2020 (age 79)"], person.Metadata(new DateOnly(2026, 10, 31)));
        Assert.Equal(["Died Mar 24, 2020"], Profile(death: new DateOnly(2020, 3, 24)).Metadata(new DateOnly(2026, 10, 31)));
        Assert.Empty(Profile().Metadata(new DateOnly(2026, 10, 31)));
    }

    [Fact]
    public void FutureBirthDoesNotProduceNegativeAge() => Assert.Equal(["Born Jan 1, 2030"], Profile(birth: new DateOnly(2030, 1, 1)).Metadata(new DateOnly(2026, 10, 31)));

    [Theory]
    [InlineData(null, null)]
    [InlineData(" \n ", null)]
    [InlineData(" A biography. ", "A biography.")]
    [InlineData("A biography...", "A biography.")]
    [InlineData("A biography…", "A biography.")]
    [InlineData("A biography. … \u2028...", "A biography.")]
    [InlineData("A biography?…", "A biography?")]
    [InlineData("...", null)]
    [InlineData("Biography.\n\nDescription above from the Wikipedia article, licensed under CC-BY-SA.", "Biography.")]
    public void BioCleanupMatchesOfficialAttributionAndTruncationRules(string? input, string? expected) => Assert.Equal(expected, PersonProfile.CleanBio(input));
}

public class PersonWorksTests
{
    private static MediaCard Card(string id) => new(id, "movie", id, null, null, null, null, null, null, null, null, null, null, [], null, null, null);
    private static SearchResultPage Page(bool more, long total, params MediaCard[] cards) =>
        new(cards, total, total > 0, more ? new SearchContinuation("next", ["next"]) : null);

    [Fact]
    public void PagingDeduplicatesWithoutReplacingExistingCardsAndPreservesKnownTotal()
    {
        var state = new PersonWorks();
        var firstCard = Card("1");
        Assert.True(state.Apply(state.Begin(PersonMediaFilter.All), Page(true, 100, firstCard, Card("2"))));
        Assert.Equal("100 titles", state.CountLabel);
        var more = state.BeginMore()!;
        Assert.Null(state.BeginMore());
        Assert.True(state.Apply(more, Page(false, 100, Card("2"), Card("3"))));
        Assert.Equal(["1", "2", "3"], state.Items.Select(card => card.ContentId));
        Assert.Same(firstCard, state.Items[0]);
        Assert.False(state.HasMore);
        Assert.False(state.IsLoading);
        Assert.True(state.HasLoaded);
        Assert.Null(state.BeginMore());
    }

    [Fact]
    public void FilterGenerationRejectsLatePagesAndFailures()
    {
        var state = new PersonWorks();
        var old = state.Begin(PersonMediaFilter.Movies);
        var current = state.Begin(PersonMediaFilter.Series);
        Assert.False(state.Apply(old, Page(false, 1, Card("old"))));
        Assert.False(state.Fail(old));
        Assert.True(state.IsLoading);
        Assert.True(state.Apply(current, Page(false, 1, Card("current"))));
        Assert.Equal(PersonMediaFilter.Series, state.Filter);
        Assert.Equal("current", Assert.Single(state.Items).ContentId);
        Assert.Equal("1 title", state.CountLabel);
        Assert.Null(state.Error);
    }

    [Fact]
    public void FirstPageFailureRemainsRetryableInsteadOfAppearingAsEmptyFilmography()
    {
        var state = new PersonWorks();
        Assert.True(state.Fail(state.Begin(PersonMediaFilter.All)));
        Assert.False(state.HasLoaded);
        Assert.False(state.IsLoading);
        Assert.NotNull(state.Error);
        Assert.Null(state.CountLabel);
        Assert.Null(state.BeginMore());
        Assert.True(state.Apply(state.Begin(state.Filter), Page(false, 0)));
        Assert.True(state.HasLoaded);
        Assert.Null(state.Error);
    }

    [Fact]
    public void AppendFailureLatchesAutomaticPagingAndManualRetryKeepsCardsAndCursor()
    {
        var state = new PersonWorks();
        state.Apply(state.Begin(PersonMediaFilter.All), Page(true, 0, Card("1")));
        Assert.Equal("1+ titles", state.CountLabel);
        var more = state.BeginMore()!;
        Assert.True(state.Fail(more));
        Assert.Null(state.BeginMore());
        var retry = state.BeginMore(retry: true)!;
        Assert.True(retry.Append);
        Assert.Equal(more.Continuation, retry.Continuation);
        Assert.Null(state.Error);
        Assert.Single(state.Items);
        state.Apply(retry, Page(false, 0, Card("2")));
        Assert.Equal("2 titles", state.CountLabel);
    }

    [Fact]
    public void NoGrowthPageCannotTriggerAnAutomaticPagingLoop()
    {
        var state = new PersonWorks();
        state.Apply(state.Begin(PersonMediaFilter.All), Page(true, 0, Card("1")));
        state.Apply(state.BeginMore()!, Page(true, 0, Card("1")));
        Assert.NotNull(state.Error);
        Assert.Null(state.BeginMore());
        Assert.NotNull(state.BeginMore(retry: true));
    }

    [Fact]
    public void LeavingRouteFencesLateTransportAndPreservesAlreadyLoadedCardsForBack()
    {
        var state = new PersonWorks();
        state.Apply(state.Begin(PersonMediaFilter.All), Page(true, 0, Card("1")));
        var pending = state.BeginMore()!;
        state.StopLoading();
        Assert.False(state.IsLoading);
        Assert.True(state.HasLoaded);
        Assert.True(state.HasMore);
        Assert.False(state.Apply(pending, Page(false, 1, Card("late"))));
        Assert.False(state.Fail(pending));
        Assert.Equal("1", Assert.Single(state.Items).ContentId);
        Assert.NotNull(state.BeginMore());
    }
}
