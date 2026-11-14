using System.Net;
using System.Text;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox;
using Bale.Xbox.Services;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class DebugNavigationFileTests
{
    private const string Cached = """
        {"content_id":"movie-1","type":"movie","versions":[
          {"file_id":"file-hd","resolution":"1080p","codec_video":"h264","audio_tracks":[{},{}]}]}
        """;
    private const string Fresh = """
        {"content_id":"movie-1","type":"movie","versions":[
          {"file_id":"file-hd","resolution":"1080p","codec_video":"h264"},
          {"file_id":"file-uhd","resolution":"2160p","codec_video":"hevc","hdr":true,
           "video_tracks":[{"codec":"hevc","width":3840,"height":2160,"bit_depth":10,"dv_profile":8}]}]}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactPickRefreshesPreloadedInventoryAndSurvivesSummaryAndPlay(bool pretendHdr)
    {
        var handler = new StubHandler().Enqueue(Cached).Enqueue(Fresh);
        using var client = Create(handler);
        Session.Client = client;
        var cached = await client.Detail.GetAsync("movie-1");
        var page = new DetailPage(cached);
        page.Selections.Select(cached, PlaybackSelectorKind.Audio, new("", "", false, TrackIndex: 1));
        var request = new DebugNavigationRequest("movie-1", "file-uhd", 0);

        var result = await page.DebugSelectPlaybackAsync(request, CancellationToken.None);

        Assert.Equal("200 OK", result.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, sent => Assert.Equal("/api/v2/catalog/items/movie-1", sent.RequestUri!.AbsolutePath));
        Assert.Equal(new PlaybackSelection("file-uhd"), page.Selection); // Clear old file's tracks, like the picker.
        Assert.Contains("file=file-uhd", page.DebugSelection);
        Assert.Equal(2, page.Inventory.Versions.Count);
        Assert.Equal(1, page.Summaries);
        // The debug refresh is page-local; the production cache and its version objects are unchanged.
        Assert.Same(cached, await client.Detail.GetAsync("movie-1"));
        Assert.Single(cached.Versions);

        await page.DebugOpenPlayerAsync(request);
        var handoff = page.Handoff!.Value;
        Assert.Same(page.Inventory, handoff.Detail);
        Assert.Equal("file-uhd", handoff.Selection.FileId);
        Assert.Equal(0, handoff.Start);
        Assert.True(handoff.Exact);
        // A small/SDR display and missing DV claims must not erase an explicit pick: the server may transcode.
        var device = DebugHdrPlayback.Capabilities(pretendHdr, ConsoleEvidence.Default);
        Assert.Empty(device.Hdr.DolbyVisionProfiles);
        var selected = PlaybackFormatting.SelectVersion(handoff.Detail.Versions, new(1080, device.VideoDecode), handoff.Selection.FileId);
        Assert.Equal("file-uhd", selected!.FileId);
        Assert.Equal("file-uhd", PlaybackStartBuilder.Build(handoff.Detail, handoff.Selection, "profile", "attempt", 0,
            device, Identity).FileId);
        Assert.Contains(PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Version, page.Inventory, page.Selection),
            option => option.FileId == "file-uhd");
    }

    [Theory]
    [InlineData("file-missing")]
    [InlineData("file-hd")]
    public async Task UnknownOrRemovedFileIs404OnlyAfterFreshReadAndNeverFallsBack(string file)
    {
        var handler = new StubHandler().Enqueue(Cached).Enqueue(Fresh.Replace("file-hd", "file-replaced"));
        using var client = Create(handler);
        Session.Client = client;
        var page = new DetailPage(await client.Detail.GetAsync("movie-1"));

        var result = await page.DebugSelectPlaybackAsync(new("movie-1", file, null), CancellationToken.None);

        Assert.Equal("404 Not Found", result.Status);
        Assert.Contains("checked fresh server detail", result.Body);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(PlaybackSelection.Auto, page.Selection);
        Assert.Null(page.Handoff);
        Assert.Equal(0, page.Summaries);
    }

    [Fact]
    public async Task NoFileKeepsTheNormalCachedAutoPath()
    {
        var handler = new StubHandler().Enqueue(Cached);
        using var client = Create(handler);
        Session.Client = client;
        var cached = await client.Detail.GetAsync("movie-1");
        var page = new DetailPage(cached);
        Assert.Equal("200 OK", (await page.DebugSelectPlaybackAsync(new("movie-1", null, null), CancellationToken.None)).Status);
        Assert.Single(handler.Requests);
        Assert.Same(cached, page.Inventory);
        Assert.Equal(PlaybackSelection.Auto, page.Selection);
    }

    [Fact]
    public async Task DeletedContentReturns404FromTheAuthoritativeRead()
    {
        var handler = new StubHandler().Enqueue(Cached).Enqueue(HttpStatusCode.NotFound, "{}");
        using var client = Create(handler);
        Session.Client = client;
        var page = new DetailPage(await client.Detail.GetAsync("movie-1"));
        var result = await page.DebugSelectPlaybackAsync(new("movie-1", "file-uhd", null), CancellationToken.None);
        Assert.Equal("404 Not Found", result.Status);
        Assert.Contains("target no longer exists", result.Body);
        Assert.Equal(PlaybackSelection.Auto, page.Selection);
    }

    [Theory]
    [InlineData("other-content", false)]
    [InlineData("movie-1", true)]
    public async Task NeverSelectsFromAnotherContentOrAContainerResponse(string responseId, bool container)
    {
        var response = Fresh.Replace("movie-1", responseId);
        if (container) response = response.Replace("\"type\":\"movie\"", "\"type\":\"series\"");
        var handler = new StubHandler().Enqueue(Cached).Enqueue(response);
        using var client = Create(handler);
        Session.Client = client;
        var page = new DetailPage(await client.Detail.GetAsync("movie-1"));
        var result = await page.DebugSelectPlaybackAsync(new("movie-1", "file-uhd", null), CancellationToken.None);
        Assert.Equal("409 Conflict", result.Status);
        Assert.Equal(PlaybackSelection.Auto, page.Selection);
        Assert.Single(page.Inventory.Versions);
    }

    [Theory]
    [InlineData("series-1")]
    [InlineData("episode-1")]
    public async Task SeriesAndEpisodeRoutesRefreshOnlyTheActualPlayEpisode(string requestedId)
    {
        var handler = new StubHandler().Enqueue(Cached).Enqueue(Fresh.Replace("movie-1", "episode-1").Replace("movie", "episode"));
        using var client = Create(handler);
        Session.Client = client;
        var model = await client.Detail.LoadAsync("movie-1");
        var owner = model with { ContentId = "series-1", Type = "series", PlayTargetId = "episode-1" };
        var page = new DetailPage(owner, model with { ContentId = "episode-1", Type = "episode" });

        var result = await page.DebugSelectPlaybackAsync(new(requestedId, "file-uhd", null), CancellationToken.None);

        Assert.Equal("200 OK", result.Status);
        Assert.Equal("/api/v2/catalog/items/episode-1", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("episode-1", page.Inventory.ContentId);
        Assert.Same(owner, page.Owner);
        Assert.Equal("file-uhd", page.Selection.FileId);
        Assert.Equal(PlaybackSelection.Auto, page.Selections.For(owner));
    }

    [Fact]
    public async Task ExactPickCanCrossTheNormalPickersEditionScope()
    {
        var response = Fresh
            .Replace("\"file_id\":\"file-hd\"", "\"file_id\":\"file-hd\",\"edition_key\":\"standard\",\"edition_raw\":\"Standard\"")
            .Replace("\"file_id\":\"file-uhd\"", "\"file_id\":\"file-uhd\",\"edition_key\":\"alternate\",\"edition_raw\":\"Alternate\"");
        var handler = new StubHandler().Enqueue(response).Enqueue(response);
        using var client = Create(handler);
        Session.Client = client;
        var detail = await client.Detail.GetAsync("movie-1");
        var page = new DetailPage(detail);
        page.Selections.Select(detail, PlaybackSelectorKind.Version, new("", "", false, FileId: "file-hd"));
        Assert.DoesNotContain(PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Version, detail, page.Selection),
            option => option.FileId == "file-uhd"); // Normal Version is deliberately scoped to Standard.

        Assert.Equal("200 OK", (await page.DebugSelectPlaybackAsync(new("movie-1", "file-uhd", null), CancellationToken.None)).Status);

        Assert.Equal("file-uhd", page.Selection.FileId);
        Assert.Contains(PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Version, page.Inventory, page.Selection),
            option => option.FileId == "file-uhd");
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("target")]
    [InlineData("generation")]
    [InlineData("tracks")]
    [InlineData("profile")]
    [InlineData("client")]
    [InlineData("cancel")]
    [InlineData("flags")]
    public async Task RefreshCannotApplyAfterNavigationProfileOrChoiceChanges(string change)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Create(new RefreshHandler(async cancellation =>
        {
            entered.SetResult();
            await finish.Task.WaitAsync(cancellation);
            return Fresh;
        }));
        using var otherClient = Create(new StubHandler());
        Session.Client = client;
        var owner = new ItemDetail("movie-1", "movie", "", null, null, null, null, null, [], null, [], [], null, [], null,
            "movie-1", null, null, new(false, false, false), []);
        var page = new DetailPage(owner);
        using var cancel = new CancellationTokenSource();
        var pending = page.DebugSelectPlaybackAsync(new("movie-1", "file-uhd", null), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (change)
        {
            case "navigation": page.Leave(); break;
            case "target": page.ChangeTarget("episode-2"); break;
            case "generation": page.ReloadPlayback(); break;
            case "tracks": page.ChangeTracks(); break;
            case "profile": client.UseProfile("other-profile"); break;
            case "client": Session.Client = otherClient; break;
            case "cancel": cancel.Cancel(); break;
            case "flags": page.ChangeFlags(); break;
        }
        finish.SetResult();
        if (change == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else
        {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(change == "flags" ? "200 OK" : "409 Conflict", result.Status);
        }
        if (change == "flags")
        {
            Assert.Equal(new(true, true, true), page.Owner.Flags);
            Assert.Equal("file-uhd", page.Selection.FileId);
        }
        else
        {
            Assert.Empty(page.Inventory.Versions);
            Assert.Equal(PlaybackSelection.Auto, page.Selection);
            Assert.Equal(0, page.Summaries);
        }
    }

    [Fact]
    public async Task PlannerRefusalIsAPlayabilityConflictWithoutLeakingServerProse()
    {
        var handler = new StubHandler().Enqueue(Capabilities).Enqueue("""
            {"outcome":"adaptation_unavailable","terminal":{"reason":"no_compatible_plan","message":"private diagnostic"}}
            """);
        using var client = Create(handler);
        var decision = await client.Playback.StartAsync(Start(new("file-uhd")), new Installation());
        var failure = DebugNavigationRequest.PlaybackFailure(decision)!.Value;
        Assert.Equal("409 Conflict", failure.Status);
        Assert.Contains("not playable with the current client claims", failure.Body);
        Assert.DoesNotContain("private diagnostic", failure.Body);
        Assert.DoesNotContain("unknown", failure.Body);
        Assert.False(PlaybackMessages.ValidatePlayable(decision).IsPlayable);
        Assert.Null(DebugNavigationRequest.PlaybackFailure(new() { Outcome = "playable", SessionId = "session", PlaybackPlan = new() }));
        Assert.Null(DebugNavigationRequest.PlaybackFailure(new())); // Invalid server replies keep the generic 500.
    }

    private sealed class RefreshHandler(Func<CancellationToken, Task<string>> read) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(await read(cancellationToken), Encoding.UTF8, "application/json"),
        };
    }
}
