using System.Net;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Bale.Core.Requests;

namespace Bale.Core.Tests;

public class RequestsFeedTests
{
    private const string Enabled = """{"requests_enabled":true,"allowed":true,"state":"available","follow_supported":true}""";
    private const string EmptyMine = """{"items":[],"page":{"has_more":false}}""";
    private const string Created = """{"id":"r1","media_type":"movie","tmdb_id":1,"title":"A title","status":"pending","state":"pending","outcome":"active","updated_at":"2026-11-02T03:00:00Z"}""";
    private const string Detail = """{"media_type":"movie","tmdb_id":1,"title":"A title","availability":"missing","request":{"requestable":true}}""";

    private static SiloConnection Connect(StubHandler handler) => new(new Uri("https://example.com/silo/"),
        new ClientIdentity("device", "TV", "xbox", "Bale"), transport: handler);

    [Fact]
    public async Task StartsHiddenPreservesCapabilityOnFailureAndResetsPerProfile()
    {
        var handler = new StubHandler().Enqueue(Enabled).Enqueue(HttpStatusCode.Forbidden, """{"detail":"No"}""")
            .Enqueue("""{"requests_enabled":true,"state":"available"}""");
        using var connection = Connect(handler);
        var feed = new RequestsFeed(connection.Api);
        Assert.False(feed.Capability.Enabled);
        Assert.False(feed.Capability.Resolved);
        Assert.True((await feed.RefreshCapabilityAsync()).Enabled);
        Assert.True((await feed.RefreshCapabilityAsync()).Enabled);
        feed.Reset();
        Assert.False(feed.Capability.Enabled);
        Assert.False(feed.Capability.FollowSupported);
        Assert.False((await feed.RefreshCapabilityAsync()).Enabled);
        Assert.True(feed.Capability.Resolved);
    }

    [Fact]
    public async Task ReadsAllMineCursorsWithoutWritingAndCachesCompleteRead()
    {
        var handler = new StubHandler().Enqueue("""{"items":[],"row_order":[]}""")
            .Enqueue("""{"items":[],"page":{"has_more":true,"next_cursor":"next opaque"}}""")
            .Enqueue("""{"items":[{"id":"r1","media_type":"movie","tmdb_id":1,"status":"pending","outcome":"active"}],"page":{"has_more":false}}""");
        using var connection = Connect(handler);
        var feed = new RequestsFeed(connection.Api);
        var rows = await feed.LoadAsync();
        Assert.Equal("Your requests", Assert.Single(rows).Title);
        Assert.Single(await feed.LoadMineAsync());
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains("limit=50", handler.Requests[1].RequestUri!.Query);
        Assert.Contains("cursor=next%20opaque", handler.Requests[2].RequestUri!.Query);
        Assert.Equal("/silo/api/v2/requests/discover", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("""{"items":[],"page":{"has_more":true}}""")]
    [InlineData("""{"items":[],"page":{}}""")]
    [InlineData("""{"page":{"has_more":false}}""")]
    public async Task RejectsIncompleteMineWithoutCachingIt(string json)
    {
        var handler = new StubHandler().Enqueue(json).Enqueue(EmptyMine);
        using var connection = Connect(handler);
        var feed = new RequestsFeed(connection.Api);
        await Assert.ThrowsAsync<InvalidDataException>(() => feed.LoadMineAsync());
        Assert.Empty(await feed.LoadMineAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsRepeatedMineCursor()
    {
        const string page = """{"items":[],"page":{"has_more":true,"next_cursor":"same"}}""";
        var handler = new StubHandler().Enqueue(page).Enqueue(page);
        using var connection = Connect(handler);
        var feed = new RequestsFeed(connection.Api);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => feed.LoadMineAsync());
        Assert.Contains("invalid request pagination", error.Message);
    }

    [Fact]
    public async Task LoadsDetailEvenWhenOptionalOwnReadFails()
    {
        var handler = new StubHandler().Enqueue(Detail).Enqueue(HttpStatusCode.Forbidden, """{"detail":"No"}""");
        using var connection = Connect(handler);
        var state = await new RequestsFeed(connection.Api).LoadDetailAsync("movie", 1);
        Assert.Equal("A title", state.Detail.Title);
        Assert.Null(state.Record);
        Assert.Equal(RequestPrimaryAction.Request, state.PrimaryAction);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task CreatesOnlyOnExplicitCallWithOfficialPayloadAndAppliesServerRecord()
    {
        var handler = new StubHandler().Enqueue(Enabled).Enqueue(HttpStatusCode.Created, Created);
        using var connection = Connect(handler);
        var feed = new RequestsFeed(connection.Api);
        var detail = RequestPresentationTests.Detail();
        Assert.False((await feed.CreateAsync(detail)).Succeeded);
        Assert.Empty(handler.Requests);
        await feed.RefreshCapabilityAsync();
        detail.TvdbId = 42;
        detail.ImdbId = "tt0000001";
        detail.Year = 2026;
        Assert.True((await feed.CreateAsync(detail)).Succeeded);
        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/silo/api/v2/requests", request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.RequestBodies[1]!);
        Assert.Equal("movie", body.RootElement.GetProperty("media_type").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("tmdb_id").GetInt64());
        Assert.Equal(42, body.RootElement.GetProperty("tvdb_id").GetInt64());
        Assert.False(body.RootElement.TryGetProperty("seasons", out _));
        Assert.True(feed.StateFor(detail).CanCancel);
        Assert.Equal("Requested · Pending", feed.StateFor(detail).PrimaryTitle);
        Assert.False((await feed.CreateAsync(detail)).Succeeded);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UnconfirmedSubmitNeverAutomaticallyRetriesAndHoldsAcrossDetailReads()
    {
        var handler = new StubHandler().Enqueue(Enabled).Enqueue(HttpStatusCode.ServiceUnavailable, "{}")
            .Enqueue(Detail).Enqueue(EmptyMine);
        using var connection = Connect(handler);
        var feed = new RequestsFeed(connection.Api);
        await feed.RefreshCapabilityAsync();
        var result = await feed.CreateAsync(RequestPresentationTests.Detail());
        Assert.True(result.Unconfirmed);
        Assert.Equal(RequestPresentation.UnconfirmedSubmit, result.Message);
        var state = await feed.LoadDetailAsync("movie", 1, forceMine: true);
        Assert.True(state.SubmissionUnconfirmed);
        Assert.Equal("Not confirmed yet", state.PrimaryTitle);
        Assert.False((await feed.CreateAsync(state.Detail)).Succeeded);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task CancelSendsEmptyBodyOnlyAfterExplicitCallAndCannotRepeatUncertainOutcome()
    {
        var handler = new StubHandler().Enqueue(Enabled).Enqueue(HttpStatusCode.GatewayTimeout, "{}");
        using var connection = Connect(handler);
        var feed = new RequestsFeed(connection.Api);
        await feed.RefreshCapabilityAsync();
        var record = new MediaRequest { Id = "r1", MediaType = "movie", TmdbId = 1, State = "pending", Status = "pending", Outcome = "active" };
        var result = await feed.CancelAsync(record);
        Assert.True(result.Unconfirmed);
        Assert.Equal(RequestPresentation.UnconfirmedCancel, result.Message);
        Assert.False((await feed.CancelAsync(record)).Succeeded);
        Assert.Equal("/silo/api/v2/requests/r1/cancel", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("{}", handler.RequestBodies[1]);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task FollowAndUnfollowAreExplicitAndPreserveTheStableState()
    {
        var handler = new StubHandler().Enqueue(Enabled)
            .Enqueue("""{"following":true,"requestable":false,"requested_by_viewer":false,"reason":"already_requested","state":"pending"}""")
            .Enqueue(HttpStatusCode.NoContent, string.Empty);
        using var connection = Connect(handler);
        var feed = new RequestsFeed(connection.Api);
        await feed.RefreshCapabilityAsync();
        var detail = RequestPresentationTests.Detail();
        detail.Request = new() { State = "pending", Reason = "already_requested", RequestId = "someone-else" };
        var state = new RequestDetailState(detail, null, true);
        Assert.Equal("We'll let you know when it's available", (await feed.ToggleFollowAsync(state)).Message);
        Assert.Equal("someone-else", detail.Request.RequestId);
        Assert.True(state.IsFollowing);
        Assert.Equal("Notification turned off", (await feed.ToggleFollowAsync(state)).Message);
        Assert.False(state.IsFollowing);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Delete, handler.Requests[2].Method);
        Assert.Equal("/silo/api/v2/requests/follows/movie/1", handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public void DistinguishesDefiniteErrorsFromUncertainTransportOrResponseFailures()
    {
        Assert.False(RequestsFeed.IsUncertain(new Problem { ResponseStatusCode = 409, Type = "urn:silo:problem:conflict" }));
        Assert.True(RequestsFeed.IsUncertain(new Problem { ResponseStatusCode = 504 }));
        Assert.True(RequestsFeed.IsUncertain(new InvalidDataException("Incomplete")));
        Assert.True(RequestsFeed.IsUncertain(new HttpRequestException("Disconnected")));
        Assert.Equal("Not allowed", RequestsFeed.ErrorMessage(new Problem { Detail = "Not allowed" }));
    }
}
