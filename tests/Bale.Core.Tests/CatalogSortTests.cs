using System.Net;
using System.Text.Json;
using Bale.Core.Browse;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

/// <summary>
/// silo-android shared CatalogSortTest.kt plus TvLibraryDetailViewModel.kt TvLibrarySortOption
/// (a60dac2d0) and RatingSourcesStore.kt: the web client's sort list per library type, the Rotten
/// Tomatoes gate, the kept active sort, and the wire fields the catalog requests carry.
/// </summary>
public class CatalogSortTests
{
    private static readonly IReadOnlySet<string> AllRatings = new HashSet<string> { "imdb", "tmdb", "rt_critic", "rt_audience" };
    private static readonly IReadOnlySet<string> NoRt = new HashSet<string> { "imdb", "tmdb" };

    private static SiloClient Create(HttpMessageHandler stub)
    {
        ServerAddress.TryParse("silo.example.com", out var server);
        return new SiloClient(server, new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), stub);
    }

    private static IEnumerable<string> Labels(IEnumerable<LibrarySortOption> options) => options.Select(option => option.Label);

    [Fact]
    public void SeriesLibraryListsTheWebClientsSeventeenSortsInOrder()
    {
        var series = LibrarySortOption.For("series", shownRatingSources: AllRatings);
        Assert.Equal(["Title", "Date Added", "Release Date", "Latest Episode Air Date", "Latest Episode Added", "Year", "Content Rating",
            "Duration", "IMDb Rating", "TMDB Rating", "RT Critic Rating", "RT Audience Rating", "Resolution", "Bitrate", "Progress",
            "Date Viewed", "Plays"], Labels(series));
        Assert.Equal(["title", "added_at", "release_date", "last_air_date", "latest_episode_added", "year", "content_rating", "runtime",
            "rating_imdb", "rating_tmdb", "rating_rt_critic", "rating_rt_audience", "resolution", "bitrate", "progress", "date_viewed",
            "plays"], series.Select(option => option.Field));
    }

    [Fact]
    public void MovieLibraryDropsTheEpisodeSorts()
    {
        Assert.Equal(["Title", "Date Added", "Release Date", "Year", "Content Rating", "Duration", "IMDb Rating", "TMDB Rating",
            "RT Critic Rating", "RT Audience Rating", "Resolution", "Bitrate", "Progress", "Date Viewed", "Plays"],
            Labels(LibrarySortOption.For("movie", shownRatingSources: AllRatings)));
    }

    [Fact]
    public void MixedLibrarySidesAndUnscopedListsFollowRelevance()
    {
        Assert.Equal(CatalogSortRelevance.Only(CatalogSortScope.Series), CatalogSortRelevance.ForLibrary("mixed", "series"));
        Assert.Equal(CatalogSortRelevance.Only(CatalogSortScope.Movie), CatalogSortRelevance.ForLibrary("mixed", "movie"));
        Assert.Equal(CatalogSortRelevance.AllVideo, CatalogSortRelevance.ForLibrary("mixed"));
        // Bale's tab scope: a mixed library's Series side is "series"; no scope (Music) is all-video.
        Assert.Equal(CatalogSortRelevance.AllVideo, CatalogSortRelevance.ForLibrary(null));
        var unscoped = LibrarySortOption.For(null, shownRatingSources: AllRatings).Select(option => option.Field).ToList();
        Assert.DoesNotContain("latest_episode_added", unscoped);
        Assert.DoesNotContain("last_air_date", unscoped);
        Assert.DoesNotContain("author", unscoped);
        Assert.Contains("rating_rt_critic", unscoped);
    }

    [Fact]
    public void AudiobookAndReadingLibrariesUseTheirOwnSortsAndNames()
    {
        Assert.Equal(["Title", "Date Added", "Release Date", "Year", "Duration", "Bitrate", "Progress", "Date Viewed", "Plays", "Author",
            "Narrator", "Series"], Labels(LibrarySortOption.For("audiobook", shownRatingSources: AllRatings)));
        var ebook = LibrarySortOption.For("ebook");
        Assert.Contains("Date Read", Labels(ebook));
        Assert.Contains("Reads", Labels(ebook));
        Assert.DoesNotContain("Narrator", Labels(ebook));
        Assert.True(CatalogSortRelevance.ForLibrary("manga").IsReading);
        Assert.False(CatalogSortRelevance.ForLibrary("audiobook").IsReading);
    }

    [Fact]
    public void RottenTomatoesSortsNeedTheirSourceShown()
    {
        Assert.DoesNotContain("RT Critic Rating", Labels(LibrarySortOption.For("series", shownRatingSources: NoRt)));
        Assert.DoesNotContain("RT Audience Rating", Labels(LibrarySortOption.For("series")));
        var critic = LibrarySortOption.For("series", shownRatingSources: new HashSet<string> { "rt_critic" }).Select(option => option.Field).ToList();
        Assert.Contains("rating_rt_critic", critic);
        Assert.DoesNotContain("rating_rt_audience", critic);
        Assert.Equal(16, LibrarySortOption.For("series", shownRatingSources: new HashSet<string> { "rt_audience" }).Count);
        // IMDb and TMDB are not gated.
        Assert.Contains("IMDb Rating", Labels(LibrarySortOption.For("movie")));
        Assert.Contains("RT Critic Rating", Labels(LibrarySortOption.ForPersonal(AllRatings)));
    }

    [Fact]
    public void TheActiveSortStaysListedWhereTheListWouldNotOfferIt()
    {
        var restored = LibrarySortOption.FromWire("latest_episode_added", "movie")!;
        var movie = LibrarySortOption.For("movie", current: restored);
        Assert.Contains(restored, movie);
        Assert.DoesNotContain("last_air_date", movie.Select(option => option.Field));
        // A hidden RT sort the viewer already picked is kept, so the panel can change it.
        var critic = LibrarySortOption.Of(CatalogSortField.RtCriticRating);
        Assert.Contains(critic, LibrarySortOption.For("series", current: critic));
        Assert.Contains(critic, LibrarySortOption.ForPersonal(current: critic));
    }

    [Fact]
    public void CollectionsLeadWithCollectionOrderAndLeaveOutPerViewerSorts()
    {
        var collection = LibrarySortOption.For("series", collection: true, shownRatingSources: AllRatings);
        Assert.Equal(LibrarySortOption.CollectionOrder, collection[0]);
        Assert.Equal(15, collection.Count);
        Assert.DoesNotContain(collection, option => option.SortField?.Personalized == true);
        Assert.DoesNotContain(LibrarySortOption.ForPersonal(AllRatings), option => option.SortField?.Personalized == true);
        Assert.Contains("Progress", Labels(LibrarySortOption.For("series")));
    }

    [Fact]
    public void WireNamesDefaultDirectionsAndDirectionLabelsMatchTheWebList()
    {
        var expected = new (string Wire, bool Descending, string Down, string Up)[]
        {
            ("title", false, "Z–A", "A–Z"), ("added_at", true, "Newest", "Oldest"), ("release_date", true, "Newest", "Oldest"),
            ("last_air_date", true, "Newest", "Oldest"), ("latest_episode_added", true, "Newest", "Oldest"), ("year", true, "Newest", "Oldest"),
            ("content_rating", false, "Z–A", "A–Z"), ("runtime", true, "Longest", "Shortest"), ("rating_imdb", true, "Highest", "Lowest"),
            ("rating_tmdb", true, "Highest", "Lowest"), ("rating_rt_critic", true, "Highest", "Lowest"),
            ("rating_rt_audience", true, "Highest", "Lowest"), ("resolution", true, "Highest", "Lowest"), ("bitrate", true, "Highest", "Lowest"),
            ("progress", true, "Highest", "Lowest"), ("date_viewed", true, "Newest", "Oldest"), ("plays", true, "Most", "Fewest"),
            ("author", false, "Z–A", "A–Z"), ("narrator", false, "Z–A", "A–Z"), ("series", false, "Z–A", "A–Z"),
        };
        Assert.Equal(expected.Select(row => row.Wire), CatalogSortField.All.Select(field => field.Wire));
        foreach (var (wire, descending, down, up) in expected)
        {
            var option = LibrarySortOption.FromWire(wire, null)!;
            Assert.Equal(descending, option.DescendingByDefault);
            Assert.Equal(down, option.DirectionLabel(true));
            Assert.Equal(up, option.DirectionLabel(false));
            // A newly picked sort starts in its own direction; picking it again flips it.
            var picked = new LibraryBrowseFilter { Sort = LibrarySortOption.CollectionOrder }.SelectSort(option);
            Assert.Equal(descending, picked.Descending);
            Assert.Equal(!descending, picked.SelectSort(option).Descending);
        }
        Assert.Equal("Default", LibrarySortOption.CollectionOrder.DirectionLabel(true));
    }

    [Fact]
    public void LegacyAliasesResolveAndUnknownFieldsDoNot()
    {
        Assert.Equal(CatalogSortField.ImdbRating, CatalogSortField.FromWire("rating"));
        Assert.Equal(CatalogSortField.DateAdded, CatalogSortField.FromWire("recently_added"));
        Assert.Equal(CatalogSortField.Title, CatalogSortField.FromWire("sort_title"));
        Assert.Equal(CatalogSortField.LatestEpisodeAdded, CatalogSortField.FromWire(" Latest_Episode_Added "));
        Assert.Null(CatalogSortField.FromWire("unknown"));
        Assert.Null(CatalogSortField.FromWire(""));
    }

    [Theory]
    // Bale's old Rating and Runtime entries were these fields: they restore as IMDb Rating and Duration.
    [InlineData("rating_imdb", "movie", "IMDb Rating", "rating_imdb")]
    [InlineData("runtime", "movie", "Duration", "runtime")]
    [InlineData("rating", "movie", "IMDb Rating", "rating_imdb")]
    // A known field the list wouldn't offer stays in use (a60dac2d0, as Android TV, Apple and Roku).
    [InlineData("latest_episode_added", "movie", "Latest Episode Added", "latest_episode_added")]
    [InlineData("rating_rt_critic", "series", "RT Critic Rating", "rating_rt_critic")]
    [InlineData("date_viewed", "ebook", "Date Read", "date_viewed")]
    // Only a field this client doesn't know falls back to Date Added.
    [InlineData("shuffle_seed", "series", "Date Added", "added_at")]
    [InlineData("", "series", "Date Added", "added_at")]
    public void RememberedSortsRestoreOrFallBack(string stored, string mediaType, string label, string field)
    {
        var json = JsonSerializer.Serialize(new { sort = stored, descending = false, matchAll = true, facets = new { } });
        var restored = LibraryBrowsePreferences.Deserialize(json, mediaType)!;
        Assert.Equal(label, restored.Sort.Label);
        Assert.Equal(field, restored.Sort.Field);
        Assert.False(restored.Descending);
        Assert.Contains(restored.Sort, LibrarySortOption.For(mediaType, current: restored.Sort));
    }

    [Fact]
    public async Task CatalogRequestsCarryTheNewWireFields()
    {
        var page = """{"items":[{"content_id":"s1","type":"series"}],"page":{"has_more":false}}""";
        var stub = new StubHandler().Enqueue(page).Enqueue(page).Enqueue(page);
        using var client = Create(stub);
        var latest = LibrarySortOption.FromWire("latest_episode_added", "series")!;
        var filter = new LibraryBrowseFilter().SelectSort(latest);
        await client.LibraryBrowse.LoadAsync(new("7", "series", filter), ArtworkWidths.Tv1080);
        Assert.Contains("sort=-latest_episode_added", stub.Requests[0].RequestUri!.Query);
        await client.LibraryBrowse.LoadAsync(new("7", "series", filter.SelectSort(latest)), ArtworkWidths.Tv1080);
        Assert.Contains("sort=latest_episode_added", stub.Requests[1].RequestUri!.Query);
        Assert.DoesNotContain("sort=-", stub.Requests[1].RequestUri!.Query);

        // Structured facets go through POST: field and order are separate body members.
        var faceted = new LibraryBrowseFilter().SelectSort(LibrarySortOption.Of(CatalogSortField.RtAudienceRating))
            .ApplyFacets(new LibraryFacetSelection().Toggle(LibraryFacet.Genre, "Drama"));
        await client.LibraryBrowse.LoadAsync(new("7", "series", faceted), ArtworkWidths.Tv1080);
        using var body = JsonDocument.Parse(stub.RequestBodies[2]!);
        Assert.Equal("rating_rt_audience", body.RootElement.GetProperty("sort").GetString());
        Assert.Equal("desc", body.RootElement.GetProperty("order").GetString());
    }

    [Fact]
    public async Task SourceSortOptionsReadTheProbedRatingSources()
    {
        var stub = new StubHandler()
            .Enqueue("""{"allowed":true,"state":"available","revision":"1","sources":[{"source":"imdb","name":"IMDb"},{"source":"rt_critic","name":"RT"}]}""");
        using var client = Create(stub);
        var source = new LibraryCatalogSource("Shows", new("7", "series", new()), client.LibraryBrowse);
        Assert.DoesNotContain("rating_rt_critic", source.SortOptions.Select(option => option.Field));
        var shown = await client.LibraryBrowse.RatingSources.RefreshAsync();
        Assert.Equal("/api/v2/capabilities/ratings", stub.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(["imdb", "rt_critic"], shown.Order(StringComparer.Ordinal));
        var fields = source.SortOptions.Select(option => option.Field).ToList();
        Assert.Contains("rating_rt_critic", fields);
        Assert.DoesNotContain("rating_rt_audience", fields);
        Assert.Equal(LibrarySortOption.ListOrder, LibraryCatalogSource.Personal("watchlist", client.LibraryBrowse).SortOptions[0]);
        Assert.Equal(LibrarySortOption.CollectionOrder,
            LibraryCatalogSource.Collection("7", "series", new("c", "Box", null, false), client.LibraryBrowse).SortOptions[0]);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("not_configured")]
    [InlineData("unsupported")]
    public async Task UnavailableCapabilityShowsNoRatingSources(string state)
    {
        var stub = new StubHandler().Enqueue($$"""{"allowed":true,"state":"{{state}}","revision":"1","sources":[{"source":"rt_critic"}]}""");
        using var client = Create(stub);
        Assert.Empty(await client.LibraryBrowse.RatingSources.RefreshAsync());
    }

    [Fact]
    public async Task FailedProbeKeepsThePreviousAnswerAndProfileSwitchClearsIt()
    {
        var stub = new StubHandler()
            .Enqueue("""{"allowed":true,"state":"available","revision":"1","sources":[{"source":"rt_audience"}]}""")
            .Enqueue(HttpStatusCode.ServiceUnavailable, """{"title":"Unavailable"}""");
        using var client = Create(stub);
        var ratings = client.LibraryBrowse.RatingSources;
        await ratings.RefreshAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => ratings.RefreshAsync());
        Assert.Contains("rt_audience", ratings.Shown);
        client.UseProfile("2");
        Assert.Empty(ratings.Shown);
    }

    [Fact]
    public async Task AnswerArrivingAfterAResetIsDropped()
    {
        var gate = new GateHandler();
        using var client = Create(gate);
        var ratings = client.LibraryBrowse.RatingSources;
        var probe = ratings.RefreshAsync();
        await gate.Started.Task;
        ratings.Reset();
        gate.Release("""{"allowed":true,"state":"available","revision":"1","sources":[{"source":"rt_critic"}]}""");
        await probe;
        Assert.Empty(ratings.Shown);
    }

    private sealed class GateHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<string> _body = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release(string json) => _body.SetResult(json);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            var json = await _body.Task.ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
