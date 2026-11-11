using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox;
using Microsoft.Kiota.Serialization.Json;
using Windows.Media.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

[Collection("Player seek")]
public class PlaybackHlsStartOverTests
{
    [Theory]
    [InlineData("server_remux_hls", 900, false)]
    [InlineData("server_remux_hls", 1560, true)]
    [InlineData("server_transcode_hls", 900, false)]
    [InlineData("server_transcode_hls", 1560, true)]
    public async Task StartOverSkipsReanchorAnUnknownWindowAndCanRewindBeforeTheNewOrigin(
        string delivery, double savedResume, bool paused)
    {
        using var handler = new HlsHandler(delivery);
        using var client = Create(handler);
        var installation = new Installation();
        var start = Start(new(AudioIndex: 1), PlaybackStartPosition.ForStartRequest(0, savedResume));
        var decision = await client.Playback.StartAsync(start, installation);
        var plan = decision.PlaybackPlan!;
        var timeline = PlaybackTimeline.From(plan.Timeline);
        Assert.Equal(0, handler.StartPosition);
        Assert.Equal(0, timeline.SourceStart(start.StartPosition, 0));
        Assert.Equal(0, timeline.PlayerStart(0));
        Assert.Equal(0, timeline.TimelineOffsetSeconds);

        var journal = new InMemoryPlaybackJournal();
        var reporter = client.Playback.CreateProgressReporter(installation, journal, "login");
        await reporter.AttachSessionAsync("session", 0);
        // NaturalDuration can exist while the adaptive source has not published seekable ranges.
        // Duration is not evidence that this growing HLS response can serve a native seek.
        var session = new MediaPlaybackSession(13.9)
        {
            CanSeek = true, NaturalDuration = TimeSpan.FromSeconds(plan.Source!.DurationSeconds!.Value),
            PlaybackState = paused ? MediaPlaybackState.Paused : MediaPlaybackState.Playing,
        };
        using var player = new PlayerPage(client, start, plan, installation, reporter, session);

        await player.SkipAsync(forward: true);
        Assert.Equal(43.9, player.Source, 6);
        Assert.Equal(0, session.UnprovenSeeks);
        Assert.Equal(43.9, Assert.Single(handler.Reanchors), 6);
        var first = Assert.Single(player.Mounts);
        var firstOrigin = delivery == "server_remux_hls" ? 40 : 42;
        Assert.Equal(firstOrigin, first.Transport.Timeline.TimelineOffsetSeconds);
        Assert.Equal(43.9 - firstOrigin, first.PlayerStart, 6);
        Assert.Equal(paused, first.Paused);

        // 33.9 is before that produced stream's source origin (40); it must reanchor again.
        await player.SkipAsync(forward: false);
        Assert.Equal(33.9, player.Source, 6);
        Assert.Equal(2, handler.Reanchors.Count);
        Assert.Equal(33.9, handler.Reanchors[1], 6);
        Assert.Equal(32, player.Mounts[1].Transport.Timeline.TimelineOffsetSeconds);
        Assert.Equal(1.9, player.Mounts[1].PlayerStart, 6);
        Assert.Equal(paused, player.IsPaused);
        Assert.Collection(handler.Progress, value => Assert.Equal(43.9, value, 6), value => Assert.Equal(33.9, value, 6));
        Assert.Equal(33.9, Assert.Single(await journal.ReadAsync()).LastPosition!.Position, 6);
    }

    [Theory]
    [InlineData("server_remux_hls", false)]
    [InlineData("server_transcode_hls", false)]
    [InlineData("server_transcode_hls", true)]
    public async Task StartOverSkipsUseNativeVodOrReanchorGrowingHlsDespiteSeekableRanges(string delivery, bool completeVod)
    {
        using var handler = new HlsHandler(delivery, completeVod);
        using var client = Create(handler);
        var start = Start(new(AudioIndex: 1), PlaybackStartPosition.ForStartRequest(0, 1560));
        var installation = new Installation();
        var plan = (await client.Playback.StartAsync(start, installation)).PlaybackPlan!;
        var reporter = client.Playback.CreateProgressReporter(installation, new InMemoryPlaybackJournal(), "login");
        await reporter.AttachSessionAsync("session", 0);
        using var player = new PlayerPage(client, start, plan, installation, reporter, new(13.9)
        {
            CanSeek = true, NaturalDuration = TimeSpan.FromSeconds(plan.Source!.DurationSeconds!.Value), CompleteVod = completeVod,
            Ranges = completeVod ? [] : [(0, 180)],
        });
        bool[] forward = [true, true, false, false, false];
        double[] expected = [43.9, 73.9, 63.9, 53.9, 43.9];
        for (var i = 0; i < forward.Length; i++)
        {
            await player.SkipAsync(forward[i]);
            Assert.Equal(expected[i], player.Source, 6);
        }
        Assert.Equal(completeVod ? 0 : expected.Length, handler.Reanchors.Count);
        Assert.Equal(completeVod ? 0 : expected.Length, player.Mounts.Count);
        Assert.Equal(expected.Length, handler.Progress.Count);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], handler.Progress[i], 6);
    }

    [Fact]
    public async Task ResumeUsesTheRemuxOffsetExactlyOnce()
    {
        using var handler = new HlsHandler("server_remux_hls");
        using var client = Create(handler);
        var start = Start(new(AudioIndex: 1), PlaybackStartPosition.ForStartRequest(null, 1500));
        var installation = new Installation();
        var plan = (await client.Playback.StartAsync(start, installation)).PlaybackPlan!;
        var timeline = PlaybackTimeline.From(plan.Timeline);
        Assert.Equal(1493, handler.StartPosition);
        Assert.Equal(1488, timeline.TimelineOffsetSeconds);
        Assert.Equal(5, timeline.PlayerStart(null));
        var reporter = client.Playback.CreateProgressReporter(installation, new InMemoryPlaybackJournal(), "login");
        await reporter.AttachSessionAsync("session", 1493);
        using var player = new PlayerPage(client, start, plan, installation, reporter, new(13.9)
        {
            CanSeek = true, Ranges = [(0, 100)],
        });
        Assert.Equal(1501.9, player.Source, 6);
        await player.SkipAsync(forward: true);
        Assert.Equal(1531.9, player.Source, 6);
        await player.SkipAsync(forward: false);
        Assert.Equal(1521.9, player.Source, 6);
        Assert.Collection(handler.Reanchors, value => Assert.Equal(1531.9, value, 6), value => Assert.Equal(1521.9, value, 6));
    }

    [Theory]
    [InlineData(18.596, 0, 48.596, 874.07, false)]
    [InlineData(913.77, 0, 903.77, 1519.82, false)]
    [InlineData(1529.774, 0, 1519.774, 2281.81, true)]
    [InlineData(18.06, 1710.5, 900, 2800, false)]
    public async Task GrowingHlsDoesNotTrustInRangeSeeksThatJumpToTheProductionEdge(
        double position, double offset, double target, double edge, bool paused)
    {
        using var handler = new HlsHandler("server_remux_hls");
        using var client = Create(handler);
        var installation = new Installation();
        var start = Start(new(AudioIndex: 1), 0);
        var plan = (await client.Playback.StartAsync(start, installation)).PlaybackPlan!;
        plan.Timeline!.TimelineOffsetSeconds = offset;
        plan.Timeline.SourceStartSeconds = offset;
        plan.Timeline.SeekWindowStartSeconds = offset;
        var reporter = client.Playback.CreateProgressReporter(installation, new InMemoryPlaybackJournal(), "login");
        await reporter.AttachSessionAsync("session", offset);
        var original = new MediaPlaybackSession(position)
        {
            CanSeek = true, Ranges = [(0, edge)], LiveEdge = edge,
            PlaybackState = paused ? MediaPlaybackState.Paused : MediaPlaybackState.Playing,
        };
        using var player = new PlayerPage(client, start, plan, installation, reporter, original);
        // Opening can advance before the continuation resumes. It must not native-seek again.
        player.OpenSession = (_, begin) => new(begin + 0.2) { CanSeek = true, Ranges = [(0, edge)], LiveEdge = edge };
        Assert.Equal("Completed", await player.SeekSourceAsync(target));
        Assert.Equal(target, Assert.Single(handler.Reanchors), 6);
        Assert.Equal(0, original.NativeSeeks);
        Assert.Equal(0, player.Session.NativeSeeks);
        Assert.Equal(target + 0.2, player.Source, 6);
        Assert.Equal(paused, player.IsPaused);
        Assert.Equal(player.Source, Assert.Single(handler.Progress), 6);
    }

    // Minimal V3 wire plans follow cmd/playbackfixtures' decision shape and the server's
    // configureCopyRemuxTimelineV3 / configureHLSTimelineV3 contracts. A copied keyframe
    // precedes the requested source time; the player clock begins at source - origin.
    private sealed class HlsHandler(string delivery, bool completeVod = false) : HttpMessageHandler
    {
        public double? StartPosition { get; private set; }
        public List<double> Reanchors { get; } = [];
        public List<double> Progress { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities", StringComparison.Ordinal))
                return Json(Capabilities.Replace("\"sequenced_progress_v1\"", "\"sequenced_progress_v1\",\"seek_reanchor_v1\""));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            if (path.EndsWith("/start", StringComparison.Ordinal))
            {
                StartPosition = root.GetProperty("start_position").GetDouble();
                Assert.Equal(1, root.GetProperty("audio_track_index").GetInt32());
                return Json(DecisionAt(StartPosition.Value), HttpStatusCode.Created);
            }
            if (path.EndsWith("/replan", StringComparison.Ordinal))
            {
                Assert.Equal("seek_reanchor", root.GetProperty("operation").GetString());
                // The real v2 endpoint rejects zero before it even reaches the replan handler.
                Assert.Empty(PlaybackReplanContractTests.EnvelopeErrors(root));
                Assert.Equal(1, root.GetProperty("attempt_count").GetInt32());
                Assert.Empty(root.GetProperty("attempted_plan_keys").EnumerateArray());
                Assert.False(root.TryGetProperty("failure", out _));
                Assert.Equal("plan-0123456789", root.GetProperty("failed_plan_id").GetString());
                Assert.Equal("v3:0123456789abcdef", root.GetProperty("plan_attempt_key").GetString());
                Assert.Equal(1, root.GetProperty("selected_tracks").GetProperty("audio").GetProperty("index").GetInt32());
                var source = root.GetProperty("position_seconds").GetDouble();
                Reanchors.Add(source);
                return Json(DecisionAt(source));
            }
            Assert.EndsWith("/progress", path);
            var position = root.GetProperty("position").GetDouble();
            Progress.Add(position);
            return Json(JsonSerializer.Serialize(new
            {
                outcome = "applied",
                accepted = new { sequence = root.GetProperty("sequence").GetInt64(), position, is_paused = root.GetProperty("is_paused").GetBoolean() },
            }));
        }

        private string DecisionAt(double source)
        {
            var origin = completeVod ? 0 : Math.Floor(source / (delivery == "server_remux_hls" ? 8 : 2))
                * (delivery == "server_remux_hls" ? 8 : 2);
            var plan = new PlaybackPlan
            {
                ProtocolVersion = 3, PlanId = "plan-0123456789", PlanAttemptKey = "v3:0123456789abcdef", SessionId = "session", Delivery = delivery,
                RequestedMediaFileId = "file", EffectiveMediaFileId = "file",
                Stream = new() { Url = $"/api/v2/playback/transcode/session-{Reanchors.Count}/master.m3u8",
                    Protocol = "hls", MimeType = "application/vnd.apple.mpegurl", Container = "hls", HeaderRefresh = "none" },
                Timeline = new()
                {
                    SourceStartSeconds = source, PlayerStartSeconds = source - origin, StreamOriginSeconds = origin,
                    TimelineOffsetSeconds = origin, CanSeekAnywhere = completeVod,
                    SeekWindowStartSeconds = completeVod ? null : origin,
                    SeekRestoration = completeVod ? "player_position" : "source_position",
                },
                // Encoded HLS grows only for unknown/oversized media. Exceed the server's
                // 50,000 two-second segment bound; ordinary known durations use full VOD.
                Source = new() { MediaFileId = "file", DurationSeconds = delivery == "server_transcode_hls" && !completeVod ? 108000 : 10800 },
                EffectiveRecipe = new() { VideoCodec = "h264", AudioCodec = "aac", DynamicRange = "sdr", Height = 1080 },
                SelectedTracks = new() { Audio = new() { Id = "file:file:audio:1", Index = 1 } },
            };
            var decision = new PlaybackDecision
            {
                ProtocolVersion = 3, Outcome = "playable", SessionId = "session", PlaybackPlan = plan,
                ServerFeatures = ["playback_plan_v3", "neutral_playback_v3_contract_v1", PlaybackSeekReanchor.Feature],
            };
            using var writer = new JsonSerializationWriter();
            writer.WriteObjectValue(null, decision);
            using var reader = new StreamReader(writer.GetSerializedContent());
            return reader.ReadToEnd();
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

// PlayerPage and the HDMI stand-in share the process log. Keep these callbacks out of tests
// that assert an exact HDMI log, while leaving the rest of the suite parallel.
[CollectionDefinition("Player seek", DisableParallelization = true)]
public class PlayerSeekCollection;
