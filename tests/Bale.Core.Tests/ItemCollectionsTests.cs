using System.Net;
using System.Text;
using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Core.Connection;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

/// <summary>
/// Android TV cb2c32d8a: ItemCollectionsRowTest.kt, TvMediaTypeFiltersTest.kt, TvItemCollectionsRow.kt returnIndex and
/// TvAppNavigation.kt onOpenCollection; server faa406d listCatalogItemCollections.
/// </summary>
public class ItemCollectionsTests
{
    private const string Capable = """{"revision":"r","state":"available","provider":"postgres","item_collections":true}""";
    private const string Libraries = """{"items":[{"id":"3","type":"movies"},{"id":"4","type":"shows"},{"id":"9","type":"ebooks"}]}""";
    private const string CollectionsPath = "/api/v2/catalog/items/movie:spirited-away/collections";

    private sealed record Seen(string Path, string Query);

    private static string Card(string id, string title = "", string source = "library", string? library = "3", string? creator = null, long count = 3) =>
        $$"""{"id":"{{id}}","source":"{{source}}","title":"{{(title.Length > 0 ? title : "Collection " + id)}}","item_count":{{count}}"""
        + (library is null ? "" : $$""","library_id":"{{library}}" """.TrimEnd())
        + (creator is null ? "" : $$""","creator_profile_id":"{{creator}}" """.TrimEnd())
        + $$""","poster_url":"/p/{{id}}.jpg"}""";

    private static string Title(string id, string type = "movie") =>
        $$"""{"content_id":"{{type}}:{{id}}","type":"{{type}}","title":"Title {{id}}","year":2001,"poster_url":"/p/{{id}}.jpg"}""";

    private static string Body(IEnumerable<string> cards, IEnumerable<string>? preview = null) =>
        $$"""{"items":[{{string.Join(",", cards)}}],"preview_items":[{{string.Join(",", preview ?? [])}}]}""";

    private static ItemDetail Detail(string type = "movie", string id = "movie:spirited-away") =>
        new(id, type, "Spirited Away", null, null, null, null, null, [], null, [], [], null, [], null, id, null, null, new(false, false, false), []);

    private static (DetailFeed Feed, LibraryBrowseFeed Library, List<Seen> Seen) Create(Func<string, string?> route, string? profile = "me")
    {
        ServerAddress.TryParse("silo.example.com", out var server);
        var seen = new List<Seen>();
        var connection = new SiloConnection(server, new ClientIdentity("d", "x", "xbox", "Bale"), transport: new Handler(request =>
        {
            var uri = request.RequestUri!;
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            lock (seen) seen.Add(new(path, uri.Query));
            return route(path);
        }));
        var browse = new BrowseFeed(connection.Api, server) { ActiveProfileId = () => profile };
        return (new DetailFeed(connection.Api, browse), new LibraryBrowseFeed(connection.Api, browse), seen);
    }

    /// <summary>A server answering the capability, <paramref name="collections"/> on the title's read, and the profile's libraries.</summary>
    private static Func<string, string?> Server(string? collections, string? capability = Capable, string? libraries = Libraries) => path => path switch
    {
        "/api/v2/catalog/search/capabilities" => capability,
        "/api/v2/user/libraries" => libraries,
        "/api/v2/profiles" => """{"items":[{"id":"me","name":"Me"},{"id":"other","name":"Alex"}]}""",
        _ when path.EndsWith("/collections", StringComparison.Ordinal) => collections,
        _ => null,
    };

    [Fact]
    public async Task SeveralCollectionsShowEveryCardUnderAlsoIn()
    {
        var (feed, _, seen) = Create(Server(Body(Enumerable.Range(1, 15).Select(index => Card("c" + index)))));

        var row = Assert.IsType<ItemCollectionsRow>(await feed.CollectionsAsync(Detail()));

        Assert.False(row.IsPreview);
        Assert.Equal("Also in", row.Title);
        Assert.Equal(Enumerable.Range(1, 15).Select(index => "collection:library:c" + index), row.Cards.Select(card => card.ContentId));
        Assert.All(row.Cards, card => Assert.Equal("3 items", card.Subtitle));
        Assert.Equal(new CollectionTarget("c1", false, "3", null), row.Cards[0].Collection);
        Assert.Equal("https://silo.example.com/p/c1.jpg", row.Cards[0].ImageUrl!.ToString());
        var read = Assert.Single(seen, request => request.Path.EndsWith("/collections", StringComparison.Ordinal));
        Assert.Equal(CollectionsPath, read.Path);
        // CatalogRepository sizes the cards as a poster rail; Bale's title page has no library to send.
        Assert.Contains("poster_width=260", read.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("library_id", read.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneCollectionWithOtherTitlesPreviewsUpToSixUnderItsNameThenExploreAll()
    {
        var (feed, library, _) = Create(Server(Body([Card("ghibli", "Studio Ghibli", library: "4")], Enumerable.Range(1, 8).Select(index => Title("t" + index)))));

        var row = Assert.IsType<ItemCollectionsRow>(await feed.CollectionsAsync(Detail()));

        Assert.True(row.IsPreview);
        Assert.Equal("Studio Ghibli", row.Title);
        Assert.Equal(Enumerable.Range(1, 6).Select(index => "movie:t" + index), row.Cards.Select(card => card.ContentId));
        Assert.Equal("2001", row.Cards[0].Subtitle);
        Assert.Equal(new CollectionTarget("ghibli", false, "4", null), row.Collection!.Collection);
        Assert.Equal(6, row.ReturnIndex(ItemCollectionsRow.ExploreAllKey));

        // Explore all opens the collection unscoped, with its library's Sort and Filter choices.
        var source = row.Open(row.Collection, library);
        Assert.Equal("Studio Ghibli", source.Title);
        Assert.True(source.IsCollection);
        Assert.Equal(new LibraryBrowseQuery(null, null, source.Query.Filter, "ghibli", false), source.Query);
        Assert.Equal("series", source.ControlsMediaType);
        Assert.Equal(LibrarySortOption.CollectionOrder, source.SortOptions[0]);
    }

    [Fact]
    public async Task OneCollectionHoldingOnlyThisTitleShowsItsCardUnderAlsoIn()
    {
        var (feed, _, _) = Create(Server(Body([Card("solo")])));

        var row = Assert.IsType<ItemCollectionsRow>(await feed.CollectionsAsync(Detail()));

        Assert.False(row.IsPreview);
        Assert.Equal(["collection:library:solo"], row.Cards.Select(card => card.ContentId));
    }

    [Fact]
    public async Task NoCollectionShowsNoRowAndReadsNoLibraries()
    {
        var (feed, _, seen) = Create(Server(Body([])));

        Assert.Null(await feed.CollectionsAsync(Detail()));
        Assert.DoesNotContain(seen, request => request.Path == "/api/v2/user/libraries");
    }

    [Theory]
    [InlineData("""{"revision":"r","state":"available","provider":"postgres"}""")]
    [InlineData("""{"revision":"r","state":"available","provider":"postgres","item_collections":false}""")]
    public async Task ServerWithoutTheReadIsNeverAsked(string capability)
    {
        var (feed, _, seen) = Create(Server(Body([Card("c1"), Card("c2")]), capability));

        Assert.Null(await feed.CollectionsAsync(Detail()));
        Assert.DoesNotContain(seen, request => request.Path.EndsWith("/collections", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyMoviesAndSeriesAsk()
    {
        var (feed, _, seen) = Create(Server(Body([Card("c1"), Card("c2")])));

        foreach (var type in new[] { "episode", "season", "audiobook" })
        {
            Assert.Null(await feed.CollectionsAsync(Detail(type)));
        }

        Assert.Empty(seen);
        Assert.False(Assert.IsType<ItemCollectionsRow>(await feed.CollectionsAsync(Detail("series", "series:one"))).IsPreview);
        Assert.Contains(seen, request => request.Path == "/api/v2/catalog/items/series:one/collections");
    }

    [Fact]
    public async Task AFailedReadThrowsSoThePageKeepsWhatItShows()
    {
        var (failing, _, _) = Create(Server(null));
        await Assert.ThrowsAnyAsync<Exception>(() => failing.CollectionsAsync(Detail()));

        // decodeItemCollectionsV2: both members are required.
        var (partial, _, _) = Create(Server("""{"items":[]}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => partial.CollectionsAsync(Detail()));

        // Unverified server cards could be an ebook library's.
        var (unknown, _, _) = Create(Server(Body([Card("c1"), Card("c2")]), libraries: null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unknown.CollectionsAsync(Detail()));
    }

    [Fact]
    public async Task PersonalCollectionsNeedNoLibraryList()
    {
        var (feed, library, seen) = Create(Server(Body([Card("mine", source: "user", library: null, creator: "me"), Card("ours", source: "user", library: null)]), libraries: null));

        var row = Assert.IsType<ItemCollectionsRow>(await feed.CollectionsAsync(Detail()));

        Assert.Equal(["collection:user:mine", "collection:user:ours"], row.Cards.Select(card => card.ContentId));
        Assert.DoesNotContain(seen, request => request.Path == "/api/v2/user/libraries");
        Assert.DoesNotContain(seen, request => request.Path == "/api/v2/profiles");
        var source = row.Open(row.Cards[0], library);
        Assert.Equal(new LibraryBrowseQuery(null, null, source.Query.Filter, "mine", true), source.Query);
        Assert.Null(source.ControlsMediaType);
    }

    [Fact]
    public async Task AnotherProfilesCollectionNamesItsMaker()
    {
        var (feed, _, seen) = Create(Server(Body([Card("c1", count: 1), Card("shared", source: "user", library: null, creator: "other", count: 1200)])));

        var row = Assert.IsType<ItemCollectionsRow>(await feed.CollectionsAsync(Detail()));

        Assert.Equal(["1 item", "by Alex"], row.Cards.Select(card => card.Subtitle));
        Assert.Contains(seen, request => request.Path == "/api/v2/profiles");
    }

    [Fact]
    public async Task UnopenableAndRepeatedCardsAndBlankTitlesAreLeftOut()
    {
        var cards = new[]
        {
            Card("c1"), Card("", source: "user", library: null), Card("c2", library: null), Card("c3", source: "elsewhere"),
            Card("c1", "Duplicate"),
        };
        var (feed, _, _) = Create(Server(Body(cards, ["""{"content_id":" ","type":"movie","title":"Blank"}""", Title("t1"), Title("t1")])));

        var row = Assert.IsType<ItemCollectionsRow>(await feed.CollectionsAsync(Detail()));

        // One openable collection is left, so its titles preview, each once.
        Assert.True(row.IsPreview);
        Assert.Equal("Collection c1", row.Title);
        Assert.Equal(["movie:t1"], row.Cards.Select(card => card.ContentId));
    }

    [Fact]
    public void PreviewDropsReadingTitlesAndFallsBackToTheCollectionCard()
    {
        var collection = new SectionCollectionCard { Id = "c1", Source = SectionCollectionCard_source.User, Title = "Weekend" };
        static CatalogItem Item(string id, string type) => new() { ContentId = id, Type = type, Title = id };

        var mixed = new ItemCollections.Preview(collection, [Item("m1", "movie"), Item("b1", "ebook"), Item("s1", "series"), Item("a1", "audiobook")]);
        var shown = Assert.IsType<ItemCollections.Preview>(mixed.VisibleOnTv());
        Assert.Equal(["m1", "s1", "a1"], shown.Items.Select(item => item.ContentId));

        var onlyBooks = new ItemCollections.Preview(collection, [Item("b1", "ebook"), Item("c1", "comics")]);
        Assert.Same(collection, Assert.Single(Assert.IsType<ItemCollections.AlsoIn>(onlyBooks.VisibleOnTv()).Cards));

        var alsoIn = new ItemCollections.AlsoIn([collection]);
        Assert.Same(alsoIn, alsoIn.VisibleOnTv());
    }

    [Fact]
    public void RowKeepsOnlyServerCollectionsOfLibrariesTvShows()
    {
        static SectionCollectionCard Card(string id, SectionCollectionCard_source source, string? libraryId = null) =>
            new() { Id = id, Source = source, Title = id, LibraryId = libraryId };
        var movies = Card("c-movies", SectionCollectionCard_source.Library, "1");
        var books = Card("c-books", SectionCollectionCard_source.Library, "2");
        var mine = Card("c-mine", SectionCollectionCard_source.User);
        var shown = new HashSet<string> { "1" };

        var kept = Assert.IsType<ItemCollections.AlsoIn>(new ItemCollections.AlsoIn([movies, books, mine]).WithTvShownCollections(shown));
        Assert.Equal([movies, mine], kept.Cards);
        Assert.Null(new ItemCollections.AlsoIn([books]).WithTvShownCollections(shown));

        static ItemCollections.Preview Preview(SectionCollectionCard collection) => new(collection, [new CatalogItem { ContentId = "m1", Type = "movie" }]);
        Assert.Null(Preview(books).WithTvShownCollections(shown));
        var preview = Preview(movies);
        Assert.Same(preview, preview.WithTvShownCollections(shown));
        Assert.True(preview.HasServerCollection);
        Assert.False(Preview(mine).HasServerCollection);
    }

    [Fact]
    public async Task EbookLibraryCollectionsStayOffTheRow()
    {
        var (feed, _, _) = Create(Server(Body([Card("books", library: "9"), Card("films")])));

        var row = Assert.IsType<ItemCollectionsRow>(await feed.CollectionsAsync(Detail()));
        Assert.Equal(["collection:library:films"], row.Cards.Select(card => card.ContentId));

        var (only, _, _) = Create(Server(Body([Card("books", library: "9")], [Title("t1")])));
        Assert.Null(await only.CollectionsAsync(Detail()));
    }

    [Fact]
    public async Task PreviewOfReadingTitlesOnlyShowsItsCollectionUnderAlsoIn()
    {
        var (feed, _, _) = Create(Server(Body([Card("shelf", "Shelf")], [Title("b1", "ebook")])));

        var row = Assert.IsType<ItemCollectionsRow>(await feed.CollectionsAsync(Detail()));

        Assert.False(row.IsPreview);
        Assert.Equal("Also in", row.Title);
        Assert.Equal(["collection:library:shelf"], row.Cards.Select(card => card.ContentId));
    }

    [Fact]
    public async Task ReturnLandsOnTheCardThatOpenedAPage()
    {
        var (preview, _, _) = Create(Server(Body([Card("c1")], [Title("t1"), Title("t2")])));
        var titles = Assert.IsType<ItemCollectionsRow>(await preview.CollectionsAsync(Detail()));
        Assert.Equal(1, titles.ReturnIndex("movie:t2"));
        Assert.Equal(2, titles.ReturnIndex(ItemCollectionsRow.ExploreAllKey));
        Assert.Equal(-1, titles.ReturnIndex("movie:gone"));

        var (several, _, _) = Create(Server(Body([Card("c1"), Card("c2", source: "user", library: null)])));
        var cards = Assert.IsType<ItemCollectionsRow>(await several.CollectionsAsync(Detail()));
        Assert.Equal(1, cards.ReturnIndex("collection:user:c2"));
        Assert.Equal(-1, cards.ReturnIndex(ItemCollectionsRow.ExploreAllKey));
    }

    [Fact]
    public async Task ServerCollectionsOpenWithTheirLibrarysSortAndFilterChoices()
    {
        var (feed, library, _) = Create(Server(Body([Card("films"), Card("shows", library: "4")])));
        var row = Assert.IsType<ItemCollectionsRow>(await feed.CollectionsAsync(Detail()));

        Assert.Equal(["3=movies", "4=shows"], row.LibraryTypes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
        var films = row.Open(row.Cards[0], library);
        Assert.Equal("movie", films.ControlsMediaType);
        Assert.Equal(new LibraryBrowseQuery(null, null, films.Query.Filter, "films", false), films.Query);
        Assert.Equal("series", row.Open(row.Cards[1], library).ControlsMediaType);
        Assert.Throws<ArgumentException>(() => row.Open(new MediaCard("m", "movie", "M", null, null, null, null, null, null, null, null, null, null, [], null, null, null), library));
    }

    [Theory]
    [InlineData("movies", "movie")]
    [InlineData("Film", "movie")]
    [InlineData("shows", "series")]
    [InlineData("tv", "series")]
    [InlineData("audiobooks", "audiobook")]
    [InlineData("mixed", null)]
    [InlineData("music", null)]
    [InlineData(null, null)]
    public void LibraryTypePicksTheCollectionPagesControls(string? libraryType, string? controls) =>
        Assert.Equal(controls, ItemCollectionsRow.ControlsMediaType(libraryType));

    [Fact]
    public void ACollectionsTabScopeStillWinsOverTheControlsType()
    {
        var (_, library, _) = Create(_ => null);
        var collection = new LibraryCollection("c1", "Heists", null, false);

        var scoped = LibraryCatalogSource.Collection("7", "movie", collection, library, "series");
        Assert.Equal("movie", scoped.ControlsMediaType);
        Assert.Equal(new LibraryBrowseQuery("7", "movie", scoped.Query.Filter, "c1", false), scoped.Query);
        Assert.Null(LibraryCatalogSource.Collection("7", null, collection, library).ControlsMediaType);
    }

    private sealed class Handler(Func<HttpRequestMessage, string?> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request) is { } body
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}", Encoding.UTF8, "application/problem+json") });
    }
}
