using System.Collections.Concurrent;
using System.Net;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox;
using Bale.Xbox.Services;
using Windows.Media.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

[Collection("Player seek")]
public class PlaybackPgsLifecycleTests
{
    private static PlaybackPlan Plan(double source = 1500, double origin = 1492)
    {
        var plan = PlaybackMediaTestSupport.Plan();
        plan.Delivery = "server_remux_hls";
        plan.Stream!.Protocol = "hls";
        plan.Timeline = new() { SourceStartSeconds = source, PlayerStartSeconds = source - origin, TimelineOffsetSeconds = origin };
        plan.Source!.DurationSeconds = 3600;
        plan.Subtitle = new()
        {
            Mode = "render", TrackId = "file:42:subtitle:0",
            Artifact = new() { Format = "sup", MimeType = "application/octet-stream", Url = "/api/v2/stream/session/subtitles/0.sup?file_id=42" },
        };
        return plan;
    }
    private static MediaPlaybackSession Session(double position) => new(position)
    {
        CanSeek = true, Ranges = [(0, 3600)], PlaybackState = MediaPlaybackState.Paused,
    };

    [Fact]
    public async Task WindowsFollowMountedSourceTimeWhenPausedSeekingAndStartingOver()
    {
        using var handler = new PgsHandler();
        using var client = Create(handler);
        var installation = new Installation();
        var reporter = client.Playback.CreateProgressReporter(installation, new InMemoryPlaybackJournal(), "login");
        using var player = new PlayerPage(client, Start(), Plan(), installation, reporter, Session(8));
        player.BeginPgs();
        await player.WaitPgsAsync();
        Assert.Equal(new(1470,1650), player.PgsCoverage);
        Assert.Equal(1_500_000, player.SubtitleClocks.Last());
        Assert.True(player.IsPaused);
        player.Session.Position = TimeSpan.FromSeconds(38); player.TickPgs();
        await player.WaitPgsAsync();
        Assert.Single(handler.Requests); // A covered forward seek needs no fetch.
        player.Session.Position = TimeSpan.FromSeconds(0); player.TickPgs();
        Assert.Single(handler.Requests); // Covered backward seek retains its cue history.
        var clears = player.PgsClears;
        player.Session.Position = TimeSpan.FromSeconds(113); player.TickPgs(); // Source 1605: prefetch.
        await player.WaitPgsAsync();
        Assert.Equal(new(1575,1755),player.PgsCoverage);
        Assert.Equal(clears,player.PgsClears); // The current cue stays on screen during overlap.
        player.Session.Position = TimeSpan.FromSeconds(308); player.TickPgs();
        await player.WaitPgsAsync();
        Assert.Equal(new(1770,1950), player.PgsCoverage);
        Assert.Equal(1_800_000, player.SubtitleClocks.Last());
        player.ClearPgsForTest();
        Assert.Equal(0UL,player.PgsBoundToken);

        using var startOver = new PlayerPage(client,Start(),Plan(0,0),installation,reporter,Session(0));
        startOver.BeginPgs(); await startOver.WaitPgsAsync();
        Assert.Equal(new(0,180),startOver.PgsCoverage);
        Assert.Equal(0,startOver.SubtitleClocks.Last());
    }

    [Fact]
    public async Task ADecodeSupersededByASeekCannotCommitItsWindow()
    {
        using var handler = new PgsHandler();
        using var client = Create(handler);
        var reporter = client.Playback.CreateProgressReporter(new Installation(),new InMemoryPlaybackJournal(),"login");
        using var player = new PlayerPage(client,Start(),Plan(),new Installation(),reporter,Session(8));
        var decoding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        PgsSubtitleWindow.BeforeDecode = range =>
        {
            if (range.Start != 1470) return;
            decoding.TrySetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
        };
        try
        {
            player.BeginPgs();
            await decoding.Task.WaitAsync(TimeSpan.FromSeconds(10));
            player.Session.Position = TimeSpan.FromSeconds(308); player.TickPgs();
            resume.Set();
            await player.WaitPgsAsync();
            Assert.Equal(new(1770,1950),Assert.Single(player.PgsCommits));
        }
        finally { resume.Set(); PgsSubtitleWindow.BeforeDecode = null; }
    }

    [Fact]
    public async Task SeekingBackIntoRetainedCoverageRetiresAnIrrelevantPrefetch()
    {
        using var handler = new PgsHandler();
        using var client = Create(handler);
        var reporter = client.Playback.CreateProgressReporter(new Installation(),new InMemoryPlaybackJournal(),"login");
        using var player = new PlayerPage(client,Start(),Plan(),new Installation(),reporter,Session(8));
        player.BeginPgs(); await player.WaitPgsAsync();
        var decoding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        PgsSubtitleWindow.BeforeDecode = range =>
        {
            decoding.TrySetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
        };
        PgsSubtitleWindow.Disposed = range => disposed.TrySetResult();
        try
        {
            player.Session.Position = TimeSpan.FromSeconds(113); player.TickPgs(); // Source 1605.
            await decoding.Task.WaitAsync(TimeSpan.FromSeconds(10));
            player.Session.Position = TimeSpan.FromSeconds(0); player.TickPgs(); // Source 1492.
            resume.Set();
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new(1470,1650),player.PgsCoverage);
            Assert.Equal(new(1470,1650),Assert.Single(player.PgsCommits));
        }
        finally { resume.Set(); PgsSubtitleWindow.BeforeDecode = null; PgsSubtitleWindow.Disposed = null; }
    }

    [Fact]
    public async Task ClearingAMountCancelsItsFetchAndNextMountGetsAFreshToken()
    {
        using var handler = new PgsHandler();
        using var client = Create(handler);
        var reporter = client.Playback.CreateProgressReporter(new Installation(),new InMemoryPlaybackJournal(),"login");
        using var player = new PlayerPage(client,Start(),Plan(),new Installation(),reporter,Session(8));
        player.BeginPgs(); await player.WaitPgsAsync();
        var first = player.PgsBoundToken;
        player.ClearPgsForTest(); player.BeginPgs(); await player.WaitPgsAsync();
        Assert.NotEqual(first,player.PgsBoundToken);
        Assert.Equal(2,handler.Requests.Count);
        Assert.Equal(2,player.PgsCommits.Count);
    }

    private sealed class PgsHandler : HttpMessageHandler
    {
        internal ConcurrentQueue<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
        }
    }
}
