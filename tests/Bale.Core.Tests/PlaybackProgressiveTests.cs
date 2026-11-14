using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox;
using Bale.Xbox.Services;
using Microsoft.Kiota.Serialization.Json;
using Windows.Media.Playback;
using static Bale.Core.Tests.PlaybackMediaTestSupport;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>server_remux_progressive: request claims, the forward-only stream and seek reanchoring.</summary>
[Collection("Player seek")]
public class PlaybackProgressiveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static PlaybackPlan ProgressivePlan(double source = 120, double origin = 118) => new()
    {
        Delivery = PlaybackProgressive.Delivery,
        Stream = new StreamV3
        {
            Url = $"/api/v2/stream/session?seek={origin}&remux_range=refuse",
            Protocol = "http_progressive", MimeType = "video/mp4", Container = "mp4",
        },
        Source = new PlaybackSource { DurationSeconds = 1800, VideoCodec = "hevc", AudioCodec = "truehd", Container = "m2ts" },
        EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "hevc", AudioCodec = "aac" },
        Timeline = Timeline(source, origin),
    };

    // silo-server playback_v3.go configureCopyRemuxTimelineV3.
    private static TimelineV3 Timeline(double source, double origin) => new()
    {
        SourceStartSeconds = source, PlayerStartSeconds = Math.Max(0, source - origin), StreamOriginSeconds = origin,
        TimelineOffsetSeconds = origin, SeekWindowStartSeconds = origin, CanSeekAnywhere = false, SeekRestoration = "source_position",
    };

    private static IEnumerable<string?> Strings(JsonElement array) => array.EnumerateArray().Select(element => element.GetString());

    [Fact]
    public async Task StartEnablesProgressiveWithItsRangeRefusalFeatureAndHonestMp4Claims()
    {
        var stub = new StubHandler().Enqueue(Capabilities).Enqueue(HttpStatusCode.Created, Decision);
        using var client = Create(stub);
        // A full hardware probe adds VP9/VC-1/MPEG-2: verified only as original files through the demuxer.
        var device = ConsoleEvidence.ForHardware(new HardwareDecodeLimits(new(3840, 2160), new(3840, 2160), new(3840, 2160),
            new(3840, 2160), new(3840, 2160), new(1920, 1080), new(1920, 1080)));
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", 0, new(), device, Identity);
        await client.Playback.StartAsync(start, new Installation());
        using var json = JsonDocument.Parse(stub.RequestBodies[1]!);
        var deliveries = json.RootElement.GetProperty("client_playback_context").GetProperty("deliveries");
        var progressive = deliveries.GetProperty("progressive");
        Assert.True(progressive.GetProperty("enabled").GetBoolean());
        Assert.True(progressive.GetProperty("supported_on_device").GetBoolean());
        Assert.False(progressive.TryGetProperty("failure_reason", out var reason) && reason.ValueKind == JsonValueKind.String);
        Assert.Equal(new[] { "progressive_range_refusal_v1" }, Strings(progressive.GetProperty("features")));
        Assert.Empty(Strings(deliveries.GetProperty("original_http").GetProperty("features")));
        Assert.Empty(Strings(deliveries.GetProperty("hls").GetProperty("features")));
        // candidate_routes_v3.go writes fragmented MP4 only.
        Assert.Equal(new[] { "mp4" }, Strings(progressive.GetProperty("containers")));
        var video = Strings(progressive.GetProperty("video_codecs")).ToArray();
        Assert.Contains("vp9", Strings(deliveries.GetProperty("original_http").GetProperty("video_codecs")));
        Assert.Equal(new[] { "h264", "hevc", "av1" }, video);
        // TrueHD, DTS and PCM are not plain MP4 copies: the server converts them to AAC instead.
        var audio = Strings(progressive.GetProperty("audio_decode_codecs")).ToArray();
        Assert.Equal(new[] { "aac", "ac3", "eac3", "mp3", "flac", "opus", "alac" }, audio);
        Assert.DoesNotContain("truehd", audio);
        Assert.DoesNotContain("dts", audio);
        Assert.Empty(progressive.GetProperty("validated_claims").EnumerateArray());
        Assert.Empty(progressive.GetProperty("transformations").EnumerateArray());
        Assert.Empty(progressive.GetProperty("subtitles").GetProperty("native_embedded").EnumerateArray());
        // PGS .sup windows follow source time on progressive just as on HLS.
        Assert.True(progressive.GetProperty("subtitles").GetProperty("embedded_bitmap").GetBoolean());
    }

    [Fact]
    public void WithoutFfmpegTheProgressiveExecutorIsWithdrawn()
    {
        var device = SoftwareDecodePolicy.WithoutFallbacks(ConsoleEvidence.Default);
        Assert.False(device.ProgressiveSupported);
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", 0, new(), device, Identity);
        var progressive = start.ClientPlaybackContext.Deliveries["progressive"];
        Assert.False(progressive.Enabled);
        Assert.False(progressive.SupportedOnDevice);
        Assert.Equal("progressive_executor_unavailable", progressive.FailureReason);
        Assert.Empty(progressive.Features);
        Assert.True(ConsoleEvidence.Default.ProgressiveSupported);
    }

    [Fact]
    public void SeekReplanKeepsProgressiveAndItsFeatureButFailureRecoveryWithdrawsIt()
    {
        var start = Start(new(AudioIndex: 1), 120);
        var plan = ProgressivePlan();
        plan.PlanId = "plan";
        plan.PlanAttemptKey = "key";
        var seek = PlaybackReplan.ForSeek(start, plan, "mount", [], 1, 300);
        Assert.Equal("seek_reanchor", seek.Operation);
        Assert.Same(start.ClientPlaybackContext.Deliveries["progressive"], seek.ClientPlaybackContext.Deliveries["progressive"]);
        Assert.Contains(PlaybackProgressive.RangeRefusalFeature, seek.ClientPlaybackContext.Deliveries["progressive"].Features);
        Assert.Empty(seek.AttemptedPlanKeys);

        var failure = PlaybackReplan.ForFailure(start, plan, "mount", [], 1, 300, new("player_failure", "MediaFailed"),
            PlaybackRoutePolicy.Decide(plan, false));
        Assert.Equal("failure_recovery", failure.Operation);
        Assert.Equal(new[] { "key" }, failure.AttemptedPlanKeys);
        var withdrawn = failure.ClientPlaybackContext.Deliveries["progressive"];
        Assert.False(withdrawn.Enabled);
        Assert.False(withdrawn.SupportedOnDevice);
        Assert.Equal("progressive_executor_failed", withdrawn.FailureReason);
        // HLS (and the original) carry the replacement exactly as before progressive existed.
        var hls = failure.ClientPlaybackContext.Deliveries["hls"];
        Assert.True(hls.Enabled);
        Assert.Equal(start.ClientPlaybackContext.Deliveries["hls"].VideoCodecs, hls.VideoCodecs);
        Assert.Equal(start.ClientPlaybackContext.Deliveries["hls"].AudioDecodeCodecs, hls.AudioDecodeCodecs);
        Assert.True(failure.ClientPlaybackContext.Deliveries["original_http"].Enabled);

        // A failed non-progressive plan leaves progressive available to the replacement.
        var original = ProgressivePlan();
        original.Delivery = "original_http";
        original.PlanId = "plan";
        original.PlanAttemptKey = "key";
        var other = PlaybackReplan.ForFailure(start, original, "mount", [], 1, 300, new("player_failure", "MediaFailed"));
        Assert.True(other.ClientPlaybackContext.Deliveries["progressive"].Enabled);
    }

    [Fact]
    public void DebugProgressiveModeKeepsOnlyTheProgressiveDelivery()
    {
        var previous = DebugPlaybackDelivery.Mode;
        try
        {
            Assert.True(DebugPlaybackDelivery.TrySet("progressive"));
            Assert.Equal("progressive", DebugPlaybackDelivery.Mode);
            Assert.False(DebugPlaybackDelivery.TrySet("Progressive"));
            Assert.Equal("progressive", DebugPlaybackDelivery.Mode);
        }
        finally { DebugPlaybackDelivery.TrySet(previous); }
        var start = Start(new(AudioIndex: 1), 0);
        var forced = DebugPlaybackDelivery.Apply("progressive", start);
        foreach (var pair in start.ClientPlaybackContext.Deliveries)
        {
            var actual = forced.ClientPlaybackContext.Deliveries[pair.Key];
            if (pair.Key == "progressive") Assert.Same(pair.Value, actual);
            else Assert.Equal(pair.Value with { Enabled = false, SupportedOnDevice = false }, actual);
        }
        Assert.Equal(start with { ClientPlaybackContext = forced.ClientPlaybackContext }, forced);
    }

    [Theory]
    [InlineData("hevc", "aac", false)]
    [InlineData("h264", "eac3", false)]
    [InlineData("av1", "opus", true)]
    public void ProgressiveAlwaysTakesTheFfmpegInputAndHlsNever(string video, string audio, bool software)
    {
        var plan = ProgressivePlan();
        plan.EffectiveRecipe = new() { VideoCodec = video, AudioCodec = audio };
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.True(route.UseFfmpeg);
        Assert.Equal(software, route.SoftwareVideo);
        // The remux carries only the server-selected audio stream.
        Assert.Equal(0, route.AudioOrdinal);
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, ffmpegEnabled: false).UseFfmpeg);
        Assert.False(PlaybackRoutePolicy.Decide(plan, true).UseFfmpeg);
    }

    [Fact]
    public void EverySeekReanchorsEvenWhenThePlanClaimsASeekableWindow()
    {
        var plan = ProgressivePlan(600, 596);
        plan.Timeline!.CanSeekAnywhere = true;
        plan.Timeline.SeekWindowEndSeconds = 1800;
        Assert.False(new PlaybackSeek(700).CanSeekInMount(plan, isHls: false, canSeek: true));
        Assert.False(new PlaybackSeek(597).CanSeekInMount(plan, isHls: false, canSeek: true));
        plan.Delivery = "original_http";
        Assert.True(new PlaybackSeek(700).CanSeekInMount(plan, isHls: false, canSeek: true));
    }

    [Theory]
    // Resume: Bale rewinds, the server resolves the keyframe before it.
    [InlineData(1493, 1488, 5)]
    // Start over: origin zero, nothing to skip.
    [InlineData(0, 0, 0)]
    public void MountsOpenAtTheOriginAndReportItsSourceTime(double requested, double origin, double playerStart)
    {
        var plan = ProgressivePlan(requested, origin);
        var timeline = PlaybackTimeline.From(plan.Timeline);
        Assert.Equal(playerStart, timeline.PlayerStart(null), 6);
        Assert.Equal(0, timeline.PlayerStart(0), 6);
        Assert.Equal(0, PlaybackProgressive.MountPlayerStart(plan, timeline.PlayerStart(null)));
        Assert.Equal(origin, PlaybackProgressive.MountSourceStart(plan, timeline.SourceStart(requested, null)), 6);
        // Player 12 s into this mount is origin + 12 in source (progress, subtitles, transport).
        Assert.Equal(origin + 12, timeline.SourceForPlayer(12)!.Value, 6);
        // Every other delivery keeps the server's player start.
        plan.Delivery = "server_remux_hls";
        Assert.Equal(playerStart, PlaybackProgressive.MountPlayerStart(plan, timeline.PlayerStart(null)), 6);
        Assert.Equal(requested, PlaybackProgressive.MountSourceStart(plan, requested), 6);
    }

    [Theory]
    [InlineData(PlaybackProgressive.Delivery, 0L, 59_999L, false)]
    [InlineData(PlaybackProgressive.Delivery, 0L, 60_000L, true)]
    [InlineData(PlaybackProgressive.Delivery, null, 600_000L, false)]
    [InlineData("server_remux_hls", 0L, 600_000L, false)]
    public void PlayingAgainAfterAMinutePausedReopens(string delivery, long? pausedAt, long now, bool reopen)
    {
        var plan = ProgressivePlan();
        plan.Delivery = delivery;
        Assert.Equal(reopen, PlaybackProgressive.ShouldReopenAfterIdle(plan, pausedAt, now));
    }

    [Theory]
    [InlineData("/api/v2/stream/session?seek=118.5&remux_range=refuse", true)]
    [InlineData("/api/v2/stream/session?remux_range=refuse&st=token", true)]
    [InlineData("/api/v2/stream/session?remux_range=allow", false)]
    [InlineData("/api/v2/stream/session?remux_range=refuse&remux_range=refuse", false)]
    [InlineData("/api/v2/stream/session?remux_range=", false)]
    public void PlanUrlsMayCarryTheRangeRefusalMarkerOnce(string url, bool allowed)
    {
        var server = new Uri("https://silo.example.com");
        Assert.Equal(allowed, PlaybackMediaUrl.Resolve(url, server) is not null);
        Assert.Null(PlaybackMediaUrl.ResolveSubtitle("/api/v2/stream/session/subtitles/1.srt?remux_range=refuse", server));
    }

    [Fact]
    public async Task StreamSendsNoRangeAndReadsTheWholeResponseForward()
    {
        var handler = new RangeHandler();
        using var client = Create(handler);
        var media = client.Playback.OpenMedia(ProgressivePlan());
        Assert.True(media.IsProgressive);
        Assert.False(media.IsHls);
        await using var stream = await media.OpenProgressiveAsync(default);
        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        var read = new MemoryStream();
        var buffer = new byte[100_000];
        int count;
        while ((count = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout)) > 0) read.Write(buffer, 0, count);
        Assert.Equal(Data, read.ToArray());
        Assert.True(stream.Completed);
        Assert.False(stream.Faulted);
        Assert.Equal(Data.Length, stream.Position);
        Assert.Equal(Data.Length, stream.DownloadedBytes);
        Assert.Equal(0, stream.BufferedBytes);
        var request = Assert.Single(handler.Requests);
        // Never a Range, not even bytes=0-: the server answers 200 from the plan's origin.
        Assert.Null(request.Headers.Range);
        Assert.Contains("remux_range=refuse", request.RequestUri!.Query);
    }

    [Fact]
    public async Task AnInterruptedResponseEndsTheDataWithoutThrowingAndIsNotTheEnd()
    {
        var handler = new RangeHandler { FailAfterBytes = (_, _) => 200_000 };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(ProgressivePlan()).OpenProgressiveAsync(default);
        var total = 0;
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout)) > 0) total += count;
        Assert.Equal(200_000, total);
        Assert.True(stream.Faulted);
        Assert.False(stream.Completed);
        // FFmpeg keeps reading at the end: it must keep getting a clean zero, never an exception.
        Assert.Equal(0, await stream.ReadAsync(buffer));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RefusalIsTypedAndOnlyAnOkAnswerIsAProgressiveStream()
    {
        foreach (var (status, expected) in new[]
        {
            (HttpStatusCode.Conflict, typeof(ProgressiveRangeRefusedException)),
            (HttpStatusCode.PartialContent, typeof(InvalidDataException)),
            (HttpStatusCode.Gone, typeof(HttpRequestException)),
        })
        {
            var handler = new RangeHandler { ConfigureResponse = (response, _) => response.StatusCode = status };
            using var client = Create(handler);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => client.Playback.OpenMedia(ProgressivePlan()).OpenProgressiveAsync(default));
            Assert.IsType(expected, error);
            if (status == HttpStatusCode.Conflict) Assert.Equal(HttpStatusCode.Conflict, ((HttpRequestException)error).StatusCode);
            Assert.True(Assert.Single(handler.Bodies).IsDisposed);
        }
        using var direct = Create(new RangeHandler());
        var original = direct.Playback.OpenMedia(Plan());
        Assert.False(original.IsProgressive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => original.OpenProgressiveAsync(default));
    }

    [Fact]
    public async Task DisposalAndMountCancellationEndAPendingReadPromptly()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RangeHandler { BeforeReadAsync = (_, ct) => gate.Task.WaitAsync(ct) };
        using var client = Create(handler);
        using var mount = new CancellationTokenSource();
        var stream = await client.Playback.OpenMedia(ProgressivePlan()).OpenProgressiveAsync(mount.Token);
        var pending = stream.ReadAsync(new byte[1024]).AsTask();
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);
        mount.Cancel();
        Assert.Equal(0, await pending.WaitAsync(Timeout));
        Assert.True(stream.Faulted);
        stream.Dispose();
        Assert.Equal(0, await stream.ReadAsync(new byte[16]));
        await Assert.Single(handler.Bodies).Disposal.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ReadAheadIsBoundedWhileTheDemuxerDoesNotRead()
    {
        var payload = new byte[48 * 1024 * 1024];
        var handler = new RangeHandler { Payload = payload };
        using var client = Create(handler);
        await using var stream = await client.Playback.OpenMedia(ProgressivePlan()).OpenProgressiveAsync(default);
        const long limit = (long)ProgressiveHttpStream.ChunkSize * (ProgressiveHttpStream.BufferChunks + 1);
        var deadline = DateTime.UtcNow + Timeout;
        while (stream.BufferedBytes < (long)ProgressiveHttpStream.ChunkSize * ProgressiveHttpStream.BufferChunks && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Task.Delay(100);
        // A paused reader stops the response (the server's stall window then applies).
        Assert.InRange(stream.DownloadedBytes, 1, limit);
        Assert.InRange(stream.BufferedBytes, 1, limit);
        Assert.False(stream.Completed);
        var buffer = new byte[ProgressiveHttpStream.ChunkSize];
        Assert.Equal(buffer.Length, await stream.ReadAsync(buffer));
        Assert.True(buffer.AsSpan().SequenceEqual(payload.AsSpan(0, buffer.Length)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipsReanchorAndMountAtEachNewOriginReportingSourceTime(bool paused)
    {
        using var handler = new ProgressiveHandler();
        using var client = Create(handler);
        var installation = new Installation();
        var start = Start(new(AudioIndex: 1), PlaybackStartPosition.ForStartRequest(null, 1500));
        var plan = (await client.Playback.StartAsync(start, installation)).PlaybackPlan!;
        Assert.Equal(1493, handler.StartPosition);
        var timeline = PlaybackTimeline.From(plan.Timeline);
        Assert.Equal(1492, timeline.TimelineOffsetSeconds);
        var playerStart = PlaybackProgressive.MountPlayerStart(plan, timeline.PlayerStart(null));
        Assert.Equal(0, playerStart);
        var reporter = client.Playback.CreateProgressReporter(installation, new InMemoryPlaybackJournal(), "login");
        await reporter.AttachSessionAsync("session", PlaybackProgressive.MountSourceStart(plan, 1493));
        // FFmpeg's non-seekable input: Windows reports no seekable range and no native seek may happen.
        var original = new MediaPlaybackSession(playerStart + 6)
        {
            CanSeek = false, PlaybackState = paused ? MediaPlaybackState.Paused : MediaPlaybackState.Playing,
        };
        using var player = new PlayerPage(client, start, plan, installation, reporter, original);
        player.OpenSession = (_, begin) => new(begin) { CanSeek = false };
        Assert.Equal(1498, player.Source, 6);

        await player.SkipAsync(forward: true);
        Assert.Equal(1528, Assert.Single(handler.Reanchors), 6);
        var first = Assert.Single(player.Mounts);
        Assert.Equal(0, first.PlayerStart);
        Assert.Equal(paused, first.Paused);
        Assert.True(first.Transport.IsProgressive);
        Assert.Equal(1528, first.Transport.Timeline.TimelineOffsetSeconds);
        Assert.Equal(1528, player.Source, 6);

        // Back 10 s: before the new origin, so the server reopens again at the keyframe before it.
        await player.SkipAsync(forward: false);
        Assert.Equal(1518, handler.Reanchors[1], 6);
        Assert.Equal(1516, player.Mounts[1].Transport.Timeline.TimelineOffsetSeconds);
        Assert.Equal(0, player.Mounts[1].PlayerStart);
        Assert.Equal(1516, player.Source, 6);
        Assert.Equal(paused, player.IsPaused);

        // A direct absolute seek (debug seek) takes the same reanchor path.
        Assert.Equal("Completed", await player.SeekSourceAsync(42.5));
        Assert.Equal(42.5, handler.Reanchors[2], 6);
        Assert.Equal(40, player.Source, 6);

        Assert.Equal(0, original.NativeSeeks);
        Assert.All(player.Mounts, mount => Assert.Equal(0, mount.PlayerStart));
        Assert.Equal(0, player.Session.NativeSeeks);
        Assert.Collection(handler.Progress, value => Assert.Equal(1528, value, 6), value => Assert.Equal(1516, value, 6),
            value => Assert.Equal(40, value, 6));
        Assert.All(handler.ReplanFeatures, features => Assert.Contains(PlaybackProgressive.RangeRefusalFeature, features));
    }

    // Plans follow candidate_routes_v3.go progressiveCandidate and configureCopyRemuxTimelineV3:
    // a 4-second GOP, origin at the keyframe before the requested source time.
    private sealed class ProgressiveHandler : HttpMessageHandler
    {
        public double? StartPosition { get; private set; }
        public List<double> Reanchors { get; } = [];
        public List<double> Progress { get; } = [];
        public List<string?[]> ReplanFeatures { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities", StringComparison.Ordinal))
                return Json(Capabilities.Replace("\"sequenced_progress_v1\"", "\"sequenced_progress_v1\",\"seek_reanchor_v1\""));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            var progressive = root.TryGetProperty("client_playback_context", out var context)
                ? context.GetProperty("deliveries").GetProperty("progressive") : default;
            if (path.EndsWith("/start", StringComparison.Ordinal))
            {
                Assert.True(progressive.GetProperty("enabled").GetBoolean());
                StartPosition = root.GetProperty("start_position").GetDouble();
                return Json(DecisionAt(StartPosition.Value), HttpStatusCode.Created);
            }
            if (path.EndsWith("/replan", StringComparison.Ordinal))
            {
                Assert.Equal("seek_reanchor", root.GetProperty("operation").GetString());
                Assert.Empty(PlaybackReplanContractTests.EnvelopeErrors(root));
                Assert.Empty(root.GetProperty("attempted_plan_keys").EnumerateArray());
                Assert.False(root.TryGetProperty("failure", out _));
                Assert.True(progressive.GetProperty("enabled").GetBoolean());
                ReplanFeatures.Add(Strings(progressive.GetProperty("features")).ToArray());
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
            var origin = Math.Floor(source / 4) * 4;
            var plan = new PlaybackPlan
            {
                ProtocolVersion = 3, PlanId = "plan-0123456789", PlanAttemptKey = "v3:0123456789abcdef", SessionId = "session",
                Delivery = PlaybackProgressive.Delivery, RequestedMediaFileId = "file", EffectiveMediaFileId = "file",
                Stream = new()
                {
                    Url = $"/api/v2/stream/session?seek={origin.ToString(System.Globalization.CultureInfo.InvariantCulture)}&remux_range=refuse",
                    Protocol = "http_progressive", MimeType = "video/mp4", Container = "mp4", HeaderRefresh = "none",
                },
                Timeline = Timeline(source, origin),
                Source = new() { MediaFileId = "file", DurationSeconds = 10800 },
                EffectiveRecipe = new() { VideoCodec = "hevc", AudioCodec = "eac3", DynamicRange = "sdr", Height = 2160 },
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
