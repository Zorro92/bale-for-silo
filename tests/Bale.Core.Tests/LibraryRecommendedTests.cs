using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core.Browse;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

/// <summary>TvMixedLibraryScopeTest.kt and shared/catalog/LibraryMediaScope.kt's bounded scope refill.</summary>
public class LibraryRecommendedTests
{
    private static SiloClient Create(HttpMessageHandler handler)
    {
        ServerAddress.TryParse("silo.example.com", out var server);
        return new SiloClient(server, new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), handler);
    }

    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?')
        .Split('&').Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    private static StubHandler Layout(string section) => new StubHandler().Enqueue("{}")
        .Enqueue("{\"sections\":[" + section + "]}");

    [Fact]
    public async Task SeriesShelfRefillsPastMoviesWithoutSendingTypeOverlays()
    {
        var stub = Layout("""{"id":"recent","title":"Recent","item_limit":1,"total_count":1,"items":[{"content_id":"film","type":"movie"}]}""")
            .Enqueue("""{"items":[{"content_id":"film","type":"movie"}],"page":{"has_more":true,"next_cursor":"opaque +/=?&"}}""")
            .Enqueue("""{"items":[{"content_id":"show","type":"series"}],"page":{"has_more":false}}""");
        using var client = Create(stub);

        var result = await client.Browse.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, "series");

        var row = Assert.Single(result.Rows);
        Assert.Equal("show", Assert.Single(row.Cards).ContentId);
        Assert.Equal(new SectionBrowseSource("recent", "Recent", "7", "series"), row.ExploreSource);
        Assert.Null(result.Error);
        Assert.Equal(4, stub.Requests.Count);
        foreach (var request in stub.Requests.Skip(2))
        {
            var query = Query(request);
            Assert.Equal("section", query["source"]);
            Assert.Equal("library", query["scope"]);
            Assert.Equal("7", query["library_id"]);
            Assert.Equal("recent", query["section_id"]);
            Assert.Equal("50", query["limit"]);
            Assert.Equal("208", query["poster_width"]);
            Assert.Equal("372", query["still_width"]);
            Assert.DoesNotContain(new[] { "type", "match", "sort", "offset", "seek" }, query.ContainsKey);
        }
        Assert.False(Query(stub.Requests[2]).ContainsKey("cursor"));
        Assert.Equal("opaque +/=?&", Query(stub.Requests[3])["cursor"]);
    }

    [Fact]
    public async Task InlineProgressLeadsAndKeepsItsPayloadWhenCatalogSamplesOverlap()
    {
        var stub = Layout("""{"id":"continue","section_type":"continue_watching","item_limit":2,"total_count":3,"items":[{"content_id":"episode","type":"episode","series_id":"show","series_title":"Show","season_number":2,"episode_number":3,"position_seconds":73,"duration_seconds":1200,"item_source":"continue_watching"}]}""")
            .Enqueue("""{"items":[{"content_id":"other","type":"episode","series_id":"other-show","season_number":1,"episode_number":4,"position_seconds":42,"duration_seconds":900,"item_source":"continue_watching"},{"content_id":"episode","type":"episode","position_seconds":1},{"content_id":"other","type":"episode"}],"page":{"has_more":true,"next_cursor":"unneeded"}}""");
        using var client = Create(stub);

        var result = await client.Browse.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, "series");

        var row = Assert.Single(result.Rows);
        Assert.Equal(["episode", "other"], row.Cards.Select(card => card.ContentId));
        Assert.Equal(CardShape.Landscape, row.Shape);
        Assert.Equal(73, row.Cards[0].PositionSeconds);
        Assert.Equal("show", row.Cards[0].SeriesId);
        Assert.Equal("S2 E3", row.Cards[0].EpisodeCode);
        Assert.Equal(42, row.Cards[1].PositionSeconds);
        Assert.Equal("other-show", row.Cards[1].SeriesId);
        Assert.Equal("S1 E4", row.Cards[1].EpisodeCode);
        Assert.False(result.Incomplete);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Theory]
    [InlineData("\"customized\":true")]
    [InlineData("\"is_custom\":true")]
    public async Task ProfileOverridesKeepOnlyTheirScopedInlineSliceAndReportIncomplete(string customization)
    {
        var stub = Layout("{\"id\":\"mine\",\"item_limit\":2,\"total_count\":21," + customization +
            ",\"items\":[{\"content_id\":\"show\",\"type\":\"series\"},{\"content_id\":\"film\",\"type\":\"movie\"}]}");
        using var client = Create(stub);

        var result = await client.Browse.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, "series");

        Assert.Equal("show", Assert.Single(Assert.Single(result.Rows).Cards).ContentId);
        Assert.True(result.Incomplete);
        Assert.NotNull(result.Error);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Theory]
    [InlineData(null, 1, 20, 20)]
    [InlineData("movie", 1, 20, 20)]
    [InlineData("series", 20, 1, 1)]
    public async Task UnscopedFullOrProvenExhaustedShelvesDoNotRefill(string? scope, int limit, int total, int count)
    {
        var section = JsonSerializer.Serialize(new
        {
            id = "recent", item_limit = limit, total_count = total,
            items = Enumerable.Range(1, count).Select(id => new { content_id = id.ToString(), type = "movie" }),
        });
        var stub = Layout(section);
        using var client = Create(stub);

        var result = await client.Browse.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, scope);

        Assert.False(result.Incomplete);
        Assert.Equal(scope == "series" ? 0 : 1, result.Rows.Count);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task RefillIsBoundedToEightRawPagesEvenWhenNoneMatch()
    {
        var stub = Layout("""{"id":"recent","item_limit":1,"items":[{"content_id":"film","type":"movie"}]}""");
        for (var index = 0; index < 8; index++)
        {
            stub.Enqueue(JsonSerializer.Serialize(new
            {
                items = new[] { new { content_id = $"film-{index}", type = "movie" } },
                page = new { has_more = true, next_cursor = $"next-{index}" },
            }));
        }
        using var client = Create(stub);

        var result = await client.Browse.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, "series");

        Assert.Empty(result.Rows);
        Assert.True(result.Incomplete);
        Assert.Equal(10, stub.Requests.Count);
    }

    [Theory]
    [InlineData("{\"has_more\":true}", "[{\"content_id\":\"show\",\"type\":\"series\"}]")]
    [InlineData("{\"has_more\":true,\"next_cursor\":\"next\"}", "[]")]
    public async Task BrokenContinuationsStopAndKeepAnyMatchingItems(string page, string items)
    {
        var stub = Layout("""{"id":"recent","item_limit":20,"total_count":100,"items":[]}""")
            .Enqueue("{\"items\":" + items + ",\"page\":" + page + "}");
        using var client = Create(stub);

        var result = await client.Browse.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, "series");

        Assert.True(result.Incomplete);
        Assert.Equal(items == "[]" ? 0 : 1, result.Rows.Count);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task RepeatedCursorStopsAndRetainsTheScopedInlineCards()
    {
        var stub = Layout("""{"id":"recent","item_limit":20,"total_count":100,"items":[{"content_id":"show","type":"series"}]}""")
            .Enqueue("""{"items":[{"content_id":"film","type":"movie"}],"page":{"has_more":true,"next_cursor":"again"}}""")
            .Enqueue("""{"items":[{"content_id":"film","type":"movie"}],"page":{"has_more":true,"next_cursor":"again"}}""");
        using var client = Create(stub);

        var result = await client.Browse.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, "series");

        Assert.True(result.Incomplete);
        Assert.Equal("show", Assert.Single(Assert.Single(result.Rows).Cards).ContentId);
        Assert.Equal(4, stub.Requests.Count);
    }

    [Fact]
    public async Task FailedRefillKeepsMatchingInlineCardsButUnauthorizedStillExpiresTheSession()
    {
        const string section = """{"id":"recent","item_limit":20,"total_count":100,"items":[{"content_id":"show","type":"series"}]}""";
        using var client = Create(Layout(section).Enqueue(HttpStatusCode.ServiceUnavailable, """{"title":"Unavailable","status":503}"""));

        var result = await client.Browse.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, "series");

        Assert.Equal("show", Assert.Single(Assert.Single(result.Rows).Cards).ContentId);
        Assert.True(result.Incomplete);
        using var unauthorized = Create(Layout(section).Enqueue(HttpStatusCode.Unauthorized, """{"title":"Unauthorized","status":401}"""));
        var error = await Assert.ThrowsAsync<Bale.Core.Api.Models.Problem>(() =>
            unauthorized.Browse.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, "series"));
        Assert.Equal(401, error.ResponseStatusCode);
    }

    [Fact]
    public async Task ThreeShelvesRefillConcurrentlyWithoutReorderingShelvesOrTheirCursors()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var threeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var cursors = new Dictionary<string, List<string?>>();
        using var handler = new RouteHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/capabilities")) return "{}";
            if (request.RequestUri.AbsolutePath.EndsWith("/sections")) return JsonSerializer.Serialize(new
            {
                sections = Enumerable.Range(0, 7).Select(id => new
                {
                    id = id.ToString(), featured = id == 6, item_limit = 1, total_count = 1,
                    items = new[] { new { content_id = "film", type = "movie" } },
                }),
            });
            var query = Query(request);
            var id = query["section_id"];
            var cursor = query.GetValueOrDefault("cursor");
            lock (cursors)
            {
                if (!cursors.TryGetValue(id, out var chain)) cursors[id] = chain = [];
                chain.Add(cursor);
                active++;
                peak = Math.Max(peak, active);
                if (active == 3) threeStarted.TrySetResult();
            }
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return JsonSerializer.Serialize(new
                {
                    items = new[] { new { content_id = cursor is null ? "film" : $"show-{id}", type = cursor is null ? "movie" : "series" } },
                    page = new { has_more = cursor is null, next_cursor = cursor is null ? "next" : null },
                });
            }
            finally { lock (cursors) active--; }
        });
        using var client = Create(handler);
        var load = client.Browse.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, "series");
        try
        {
            await threeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, active);
        }
        finally { release.TrySetResult(); }

        var result = await load;

        Assert.Equal(3, peak);
        Assert.Equal(Enumerable.Range(0, 6).Select(id => id.ToString()), result.Rows.Select(row => row.Id));
        Assert.Equal(Enumerable.Range(0, 6).Select(id => $"show-{id}"), result.Rows.SelectMany(row => row.Cards).Select(card => card.ContentId));
        Assert.All(cursors.Values, chain => Assert.Equal(new string?[] { null, "next" }, chain));
        Assert.False(result.Incomplete);
    }

    [Fact]
    public async Task CancelingASupersededLoadStopsItsCursorChain()
    {
        using var cancellation = new CancellationTokenSource();
        var refills = 0;
        using var handler = new RouteHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/capabilities")) return Task.FromResult("{}");
            if (request.RequestUri.AbsolutePath.EndsWith("/sections")) return Task.FromResult("""{"sections":[{"id":"recent","item_limit":1,"items":[{"content_id":"film","type":"movie"}]}]}""");
            refills++;
            cancellation.Cancel();
            return Task.FromResult("""{"items":[{"content_id":"film","type":"movie"}],"page":{"has_more":true,"next_cursor":"next"}}""");
        });
        using var client = Create(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.Browse.LoadLibraryRecommendedAsync("7", ArtworkWidths.Tv1080, "series", cancellation.Token));

        Assert.Equal(1, refills);
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, CancellationToken, Task<string>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await respond(request, cancellationToken), Encoding.UTF8, "application/json") };
    }
}
