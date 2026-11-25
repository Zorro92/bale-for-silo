using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox;
using Windows.Media.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>
/// A seek kept across a quality or track change (silo-server 4a73f48, d22e55f): the replacement starts at the viewer's
/// newest seek, not where the change was asked, including a seek still in flight. PlayerPage.Seek.cs runs as is against the
/// seek stub; the replan call sites (PlayerPage.BufferingQuality.cs, PlayerPage.Tracks.cs) compile only on Windows and are
/// pinned by source.
/// </summary>
[Collection("Player seek")]
public class PlaybackReplanSeekTests
{
    private static PlaybackPlan Original(double sourceStart = 2) => new()
    {
        PlanId = "plan", PlanAttemptKey = "key", Delivery = "original_http",
        Stream = new() { Url = "/api/v2/stream/session/file", Container = "mp4", Protocol = "http" },
        Timeline = new() { SourceStartSeconds = sourceStart, PlayerStartSeconds = sourceStart, CanSeekAnywhere = true },
        Source = new() { DurationSeconds = 1000 },
    };

    private static PlaybackPlan Progressive(double sourceStart, double origin) => new()
    {
        PlanId = "plan", PlanAttemptKey = "key", Delivery = PlaybackProgressive.Delivery,
        Stream = new() { Url = "/api/v2/stream/session/remux.mp4", Container = "mp4", Protocol = "http" },
        Timeline = new() { SourceStartSeconds = sourceStart, PlayerStartSeconds = sourceStart - origin, TimelineOffsetSeconds = origin },
        Source = new() { DurationSeconds = 1000 },
    };

    private static PlaybackPlan Hls(double? windowStart, double? windowEnd) => new()
    {
        PlanId = "plan", PlanAttemptKey = "key", Delivery = "server_transcode_hls",
        Stream = new() { Url = "/api/v2/playback/transcode/s/master.m3u8", Container = "mpegts", Protocol = "hls" },
        Timeline = new() { SourceStartSeconds = 300, PlayerStartSeconds = 300, SeekWindowStartSeconds = windowStart, SeekWindowEndSeconds = windowEnd },
        Source = new() { DurationSeconds = 1000 },
    };

    [Fact]
    public void TheReplacementStartsAtASeekItCanReach()
    {
        // An original seeks anywhere in the file; a plan already anchored within the web's 1 s settle tolerance starts there.
        Assert.True(PlaybackReplanSeek.StartsAt(Original(), 600));
        Assert.True(PlaybackReplanSeek.StartsAt(Progressive(sourceStart: 600, origin: 596), 600.8));
        // A forward-only remux anchored elsewhere can't seek: the seek follows it as a reanchor.
        Assert.False(PlaybackReplanSeek.StartsAt(Progressive(sourceStart: 120, origin: 118), 600));
        // HLS only inside the server's window.
        Assert.True(PlaybackReplanSeek.StartsAt(Hls(0, 1000), 600));
        Assert.False(PlaybackReplanSeek.StartsAt(Hls(250, 500), 600));
        Assert.False(PlaybackReplanSeek.StartsAt(Hls(null, null), 600));
        Assert.True(PlaybackReplanSeek.StartsAt(Hls(null, null), 300.5));
        Assert.False(PlaybackReplanSeek.StartsAt(Original(), double.NaN));
        Assert.False(PlaybackReplanSeek.StartsAt(Original(), 1001));
    }

    [Fact]
    public async Task SkipsDuringAChangeMoveItsReplacementInsteadOfBeingDropped()
    {
        using var fixture = await Fixture.CreateAsync();
        var player = fixture.Player;
        var requested = await player.BeginReplanAsync();
        Assert.Equal(2, requested, 6);
        // Two presses while the change is in flight: the player is busy, so both wait, the second from the first's target.
        player.Skip(forward: true);
        player.Skip(forward: true);
        Assert.True(player.SkipsPending);
        Assert.Equal(62, await player.MountReplanAsync(Original(), requested), 6);
        player.EndReplan();
        await player.SkipsDoneAsync();
        var mount = Assert.Single(player.Mounts);
        Assert.Equal(62, mount.PlayerStart, 6);
        // The replacement took the seek: no second seek on its session.
        Assert.Equal(0, player.Session.NativeSeeks);
        Assert.Equal(62, player.Source, 6);
    }

    [Fact]
    public async Task ARefusedChangeHandsTheSeekToThePlayingStream()
    {
        using var fixture = await Fixture.CreateAsync();
        var player = fixture.Player;
        var session = player.Session;
        await player.BeginReplanAsync();
        player.Skip(forward: true);
        player.EndReplan();
        await player.SkipsDoneAsync();
        Assert.Empty(player.Mounts);
        Assert.Same(session, player.Session);
        Assert.Equal(32, player.Source, 6);
    }

    [Fact]
    public async Task ASeekAfterTheReplacementMountedFollowsIt()
    {
        using var fixture = await Fixture.CreateAsync();
        var player = fixture.Player;
        var requested = await player.BeginReplanAsync();
        player.Skip(forward: true);
        Assert.Equal(32, await player.MountReplanAsync(Original(), requested), 6);
        // Pressed while the replacement opens: it builds on the replacement's start and runs once the change lets go.
        player.Skip(forward: true);
        player.EndReplan();
        await player.SkipsDoneAsync();
        Assert.Equal(32, Assert.Single(player.Mounts).PlayerStart, 6);
        Assert.Equal(1, player.Session.NativeSeeks);
        Assert.Equal(62, player.Source, 6);
    }

    [Fact]
    public async Task AStreamThatCannotReachTheSeekStartsWhereAskedAndTheSeekFollows()
    {
        using var fixture = await Fixture.CreateAsync();
        var player = fixture.Player;
        var requested = await player.BeginReplanAsync();
        player.Skip(forward: true);
        // The change was asked at 2 and its remux can't seek: it mounts at its origin, the seek stays queued.
        Assert.Equal(2, await player.MountReplanAsync(Progressive(sourceStart: 2, origin: 0), requested), 6);
        Assert.True(player.SkipsPending);
        Assert.Equal(0, Assert.Single(player.Mounts).PlayerStart, 6);
    }

    [Fact]
    public async Task ASeekStillInFlightWhenTheChangeBeginsIsWhereItResumes()
    {
        using var fixture = await Fixture.CreateAsync();
        var player = fixture.Player;
        // A local seek to 75 the watchdog still follows: the clock reads 2, the viewer asked for 75.
        player.UnlandedSeek = 75;
        var requested = await player.BeginReplanAsync();
        Assert.Equal(75, requested, 6);
        Assert.Equal(75, await player.MountReplanAsync(Original(), requested), 6);
        player.EndReplan();
        await player.SkipsDoneAsync();
        Assert.Equal(75, Assert.Single(player.Mounts).PlayerStart, 6);
        Assert.Equal(0, player.Session.NativeSeeks);
    }

    [Fact]
    public async Task ADebugSeekDuringAChangeIsKeptNotRefused()
    {
        using var fixture = await Fixture.CreateAsync();
        var player = fixture.Player;
        await player.BeginReplanAsync();
        var result = await player.DebugSeekAsync(new(100, false));
        Assert.Equal("202 Accepted", result.Status);
        player.EndReplan();
        await player.SkipsDoneAsync();
        Assert.Equal(100, player.Source, 6);
        // With no change in flight a busy player still refuses at once.
        await player.Lifecycle.WaitAsync();
        try { Assert.Equal("409 Conflict", (await player.DebugSeekAsync(new(200, false))).Status); }
        finally { player.Lifecycle.Release(); }
    }

    [Fact]
    public void TheQualityAndTrackChangesKeepSeeksAroundTheirReplan()
    {
        foreach (var file in new[] { "PlayerPage.BufferingQuality.cs.txt", "PlayerPage.Tracks.cs.txt" })
        {
            var source = Read(file);
            var begin = source.IndexOf("BeginReplanSeeks();", StringComparison.Ordinal);
            var position = source.IndexOf("var position = ReplanSeekPosition(attempt);", StringComparison.Ordinal);
            var take = source.IndexOf("var resume = TakeReplanSeek(attempt.Plan, position);", StringComparison.Ordinal);
            var mount = source.IndexOf("timeline.PlayerForSource(resume) ?? timeline.PlayerStartSeconds), paused);", StringComparison.Ordinal);
            var release = source.IndexOf("_lifecycle.Release();\n            // After the release: a seek the replacement did not take runs on whatever plays now.\n            EndReplanSeeks();", StringComparison.Ordinal);
            Assert.True(begin > 0 && position > begin && take > position && mount > take && release > mount, file);
            Assert.Contains("PlaybackProgressive.MountSourceStart(attempt.Plan, resume);", source);
            Assert.DoesNotContain("var position = SourcePosition(attempt);", source);
        }
        // A Restore that waits for the player keeps seeks from before it gets it.
        var quality = Read("PlayerPage.BufferingQuality.cs.txt");
        Assert.Contains("BeginReplanSeeks();\n            await _lifecycle.WaitAsync();", quality);
        // Bumpers and scrub commits are accepted while a change is in flight, as skips are.
        var controls = Read("PlayerPage.Controls.cs.txt");
        Assert.Equal(2, controls.Split("|| !_skipProcessing && _replanFinished is null && attempt.Media is not { Opened: true, Started: true, FailureQueued: false }) return;").Length - 1);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    private sealed class Fixture(SiloClient client, PlayerPage player) : IDisposable
    {
        internal PlayerPage Player => player;

        internal static async Task<Fixture> CreateAsync()
        {
            var client = Create(new ProgressHandler());
            var installation = new Installation();
            var reporter = client.Playback.CreateProgressReporter(installation, new InMemoryPlaybackJournal(), "login");
            await reporter.AttachSessionAsync("session");
            var session = new MediaPlaybackSession(2) { CanSeek = true, CompleteVod = true };
            var player = new PlayerPage(client, Start(), Original(), installation, reporter, session)
            {
                OpenSession = (_, start) => new(start) { CanSeek = true, CompleteVod = true },
            };
            return new(client, player);
        }

        public void Dispose()
        {
            player.Dispose();
            client.Dispose();
        }
    }

    private sealed class ProgressHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.EndsWith("/progress", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    outcome = "applied",
                    accepted = new { sequence = root.GetProperty("sequence").GetInt64(),
                        position = root.GetProperty("position").GetDouble(), is_paused = root.GetProperty("is_paused").GetBoolean() },
                }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
