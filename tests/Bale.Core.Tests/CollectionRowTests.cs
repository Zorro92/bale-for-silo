using System.Net;
using System.Text;
using Bale.Core.Browse;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

/// <summary>Android TV db87f835f: CollectionRowTest.kt, TvCollectionMarqueeTest.kt and the Home/library row rules.</summary>
public class CollectionRowTests
{
    private const string Row = """
        {"id":"picks","title":"Our Picks","section_type":"collection_posters","featured":true,"total_count":5,"items":[],"collections":[
          {"id":"10","source":"library","title":"Heists","item_count":3,"library_id":"1","poster_url":"/p/10.jpg","backdrop_url":"/b/10.jpg"},
          {"id":"11","source":"library","title":"Reading","item_count":1,"library_id":"9","poster_url":"/p/11.jpg"},
          {"id":"10","source":"user","title":"Mine","item_count":0,"creator_profile_id":"me"},
          {"id":"12","source":"user","title":"Shared","item_count":1200,"creator_profile_id":"other","poster_url":"/p/12.jpg"},
          {"id":"13","source":"library","title":"No library"},
          {"id":"","source":"user","title":"Blank"},
          {"id":"10","source":"library","title":"Duplicate","library_id":"1"}
        ]}
        """;

    private static (BrowseFeed Feed, List<string> Paths) Create(Func<string, string?> route, string? profile = "me")
    {
        ServerAddress.TryParse("silo.example.com", out var server);
        var paths = new List<string>();
        var connection = new SiloConnection(server, new ClientIdentity("d", "x", "xbox", "Bale"), transport: new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (paths) paths.Add(path);
            return route(path);
        }));
        return (new BrowseFeed(connection.Api, server) { ActiveProfileId = () => profile }, paths);
    }

    private static string? Home(string path, string? libraries = """{"items":[{"id":"1","type":"movies"},{"id":"9","type":"ebooks"}]}""") => path switch
    {
        "/api/v2/home/capabilities" => "{}",
        "/api/v2/home/sections" => "{\"sections\":[" + Row + "]}",
        "/api/v2/user/libraries" => libraries,
        "/api/v2/profiles" => """{"items":[{"id":"me","name":"Me"},{"id":"other","name":"Alex"}]}""",
        _ => null,
    };

    [Fact]
    public async Task HomeRowShowsOpenableCardsWithoutEbookLibrariesExploreAllOrHero()
    {
        var (feed, _) = Create(path => Home(path));

        var row = Assert.Single(await feed.LoadHomeAsync(ArtworkWidths.Tv1080));

        Assert.True(row.IsCollectionRow);
        Assert.False(row.Featured);
        Assert.False(row.OffersExploreAll);
        Assert.Equal(CardShape.Poster, row.Shape);
        Assert.Equal(["Heists", "Mine", "Shared"], row.Cards.Select(card => card.Title));
        Assert.Equal(["collection:library:10", "collection:user:10", "collection:user:12"], row.Cards.Select(card => card.ContentId));
        Assert.Equal(["3 items", "0 items", "by Alex"], row.Cards.Select(card => card.Subtitle));
        Assert.Equal("3 collections", row.CountText);
        var heists = row.Cards[0];
        Assert.Equal(new CollectionTarget("10", false, "1", null), heists.Collection);
        Assert.Equal("https://silo.example.com/b/10.jpg", heists.BackdropUrl!.ToString());
        // Without a backdrop the hero uses the poster (tvMarqueeItem).
        Assert.Equal(row.Cards[2].ImageUrl, row.Cards[2].BackdropUrl);
        Assert.Null(row.Cards[1].ImageUrl);
        Assert.True(row.Cards[1].Collection!.UserCollection);
    }

    [Fact]
    public async Task UnknownLibrariesHoldServerCollectionsBackOnHome()
    {
        var (feed, _) = Create(path => path == "/api/v2/user/libraries" ? null : Home(path));

        var row = Assert.Single(await feed.LoadHomeAsync(ArtworkWidths.Tv1080));

        Assert.Equal(["Mine", "Shared"], row.Cards.Select(card => card.Title));
    }

    [Fact]
    public async Task OwnCollectionsNeedNoProfileListAndUnknownMakersGetCounts()
    {
        var (feed, paths) = Create(path => path == "/api/v2/profiles" ? null : Home(path));

        var row = Assert.Single(await feed.LoadHomeAsync(ArtworkWidths.Tv1080));

        Assert.Equal("1,200 items", row.Cards[2].Subtitle);
        Assert.Contains("/api/v2/profiles", paths);

        var (single, singlePaths) = Create(path => Home(path), profile: "other");
        var cards = Assert.Single(await single.LoadHomeAsync(ArtworkWidths.Tv1080)).Cards;
        Assert.Equal("by Me", cards[1].Subtitle);
        Assert.Equal("1,200 items", cards[2].Subtitle);
    }

    [Fact]
    public async Task HomeWithoutCollectionRowsMakesNoExtraRequests()
    {
        var (feed, paths) = Create(path => path switch
        {
            "/api/v2/home/capabilities" => "{}",
            "/api/v2/home/sections" => """{"sections":[{"id":"ra","title":"Recent","items":[{"content_id":"m","type":"movie"}]}]}""",
            _ => null,
        });

        Assert.Single(await feed.LoadHomeAsync(ArtworkWidths.Tv1080));
        Assert.DoesNotContain("/api/v2/user/libraries", paths);
        Assert.DoesNotContain("/api/v2/profiles", paths);
    }

    [Theory]
    [InlineData(null, new[] { "3 items", "1 item", "0 items", "by Alex" })]
    [InlineData("movie", new[] { null, null, null, "by Alex" })]
    public async Task LibraryRowKeepsEveryLibraryAndHidesCountsWhenScoped(string? scope, string?[] captions)
    {
        var (feed, _) = Create(path => path == "/api/v2/library/7/sections" ? "{\"sections\":[" + Row + "]}" : Home(path));

        var result = await feed.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, scope);

        var row = Assert.Single(result.Rows);
        Assert.Equal(["Heists", "Reading", "Mine", "Shared"], row.Cards.Select(card => card.Title));
        Assert.Equal(captions, row.Cards.Select(card => card.Subtitle));
        Assert.False(row.OffersExploreAll);
        Assert.Null(result.Error);
    }

    [Fact]
    public void OwnerNameOnlyForAnotherProfilesPersonalCollection()
    {
        var owners = new CollectionOwners("me", new Dictionary<string, string> { ["other"] = "Alex", ["blank"] = " " });
        Assert.Equal("Alex", owners.OwnerName(new("1", true, null, "other")));
        Assert.Null(owners.OwnerName(new("1", true, null, "me")));
        Assert.Null(owners.OwnerName(new("1", false, "4", "other")));
        Assert.Null(owners.OwnerName(new("1", true, null, "blank")));
        Assert.Null(owners.OwnerName(new("1", true, null, "missing")));
        Assert.Null(new CollectionOwners(null, owners.NamesById).OwnerName(new("1", true, null, "other")));
    }

    private sealed class Handler(Func<HttpRequestMessage, string?> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request) is { } body
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }
}
