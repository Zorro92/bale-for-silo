using System.Net;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Microsoft.Kiota.Abstractions;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class PlaybackStartTests
{
    [Fact]
    public async Task StartUsesV2SnakeCaseStringIdsAndTheAuthenticatedConnection()
    {
        var stub = new StubHandler().Enqueue(Capabilities).Enqueue(HttpStatusCode.Created, Decision);
        using var client = Create(stub);

        var response = await client.Playback.StartAsync(Start(position: 12.5), new Installation());

        Assert.Equal("session", response.SessionId);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Post }, stub.Requests.Select(request => request.Method));
        Assert.Equal("/api/v2/playback/start", stub.Requests[1].RequestUri!.AbsolutePath);
        Assert.All(stub.Requests, request =>
        {
            Assert.Equal("Bearer test-access", request.Headers.Authorization!.ToString());
            Assert.Equal("profile", Assert.Single(request.Headers.GetValues("X-Profile-Id")));
            Assert.Equal("test-profile-proof", Assert.Single(request.Headers.GetValues("X-Profile-Token")));
            Assert.Equal("device", Assert.Single(request.Headers.GetValues("X-Silo-Device-Id")));
        });
        using var json = JsonDocument.Parse(stub.RequestBodies[1]!);
        var body = json.RootElement;
        Assert.Equal(3, body.GetProperty("protocol_version").GetInt32());
        Assert.Equal("file", body.GetProperty("file_id").GetString());
        Assert.Equal("profile", body.GetProperty("profile_id").GetString());
        Assert.Equal("attempt-id", body.GetProperty("playback_attempt_id").GetString());
        Assert.Equal("installation", body.GetProperty("installation_id").GetString());
        Assert.Equal(12.5, body.GetProperty("start_position").GetDouble());
        Assert.Equal("auto", body.GetProperty("quality_preference").GetString());
        Assert.Equal("preserve", body.GetProperty("subtitle_fidelity_preference").GetString());
        Assert.Equal("server", body.GetProperty("progress_persistence").GetString());
        Assert.False(body.GetProperty("metered").GetBoolean());
        Assert.False(body.TryGetProperty("allow_alternate_versions", out _));
        Assert.False(body.TryGetProperty("fileId", out _));
        Assert.Equal(new[] { "playback_plan_v3", "client_video_transformations_v1", "device_quirks_v1", "seek_reanchor_v1", "direct_stream_resume_v1", "software_video_decode_v1" },
            body.GetProperty("client_features").EnumerateArray().Select(value => value.GetString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public async Task AutoAndDetailOffOmitTheServerTrackPair(int? subtitle)
    {
        // TvPlaybackSelectorRow.kt keeps Off=-1 locally; PlaybackSessionManager.kt must omit it on v3.
        var selection = new PlaybackSelection(SubtitleIndex: subtitle);
        var stub = new StubHandler().Enqueue(Capabilities).Enqueue(HttpStatusCode.Created, Decision);
        using var client = Create(stub);
        await client.Playback.StartAsync(Start(selection), new Installation());
        using var json = JsonDocument.Parse(stub.RequestBodies[1]!);
        var body = json.RootElement;
        Assert.False(body.TryGetProperty("audio_track_id", out _));
        Assert.False(body.TryGetProperty("audio_track_index", out _));
        Assert.False(body.TryGetProperty("subtitle_track_id", out _));
        Assert.False(body.TryGetProperty("subtitle_track_index", out _));
        Assert.Equal(subtitle, selection.SubtitleIndex);
        Assert.Equal(0, body.GetProperty("start_position").GetDouble());
    }

    [Fact]
    public async Task ExplicitVersionAndTrackOrdinalsAreNotRenumbered()
    {
        var stub = new StubHandler().Enqueue(Capabilities).Enqueue(HttpStatusCode.Created, Decision);
        using var client = Create(stub);
        await client.Playback.StartAsync(Start(new("picked-file", 0, 7), position: null), new Installation());
        using var json = JsonDocument.Parse(stub.RequestBodies[1]!);
        var body = json.RootElement;
        Assert.Equal("picked-file", body.GetProperty("file_id").GetString());
        Assert.Equal(0, body.GetProperty("audio_track_index").GetInt32());
        Assert.Equal("file:picked-file:audio:0", body.GetProperty("audio_track_id").GetString());
        Assert.Equal(7, body.GetProperty("subtitle_track_index").GetInt32());
        Assert.Equal("file:picked-file:subtitle:7", body.GetProperty("subtitle_track_id").GetString());
        Assert.False(body.TryGetProperty("start_position", out _));
    }

    [Fact]
    public void DetailAutoUsesTheExistingVersionResolver()
    {
        ItemDetail detail = new("item", "movie", "Movie", null, null, null, null, null,
            [], null, [], [], null, [], null, "item", null, null, new(false, false, false), [])
        {
            Versions = [new FileVersion { FileId = "best", Resolution = "2160p" }, new FileVersion { FileId = "last", Resolution = "1080p" }],
            LastFileId = "last",
        };
        var start = PlaybackStartBuilder.Build(detail, PlaybackSelection.Auto, "profile", "attempt-id", null, ConsoleEvidence.Default, Identity);
        Assert.Equal("last", start.FileId);
        Assert.Equal("best", PlaybackStartBuilder.Build(detail, new("best"), "profile", "attempt-id", 0, ConsoleEvidence.Default, Identity).FileId);
        Assert.Throws<ArgumentException>(() => PlaybackStartBuilder.Build(detail, new("gone"), "profile", "attempt-id", 0, ConsoleEvidence.Default, Identity));
    }

    [Fact]
    public void DeviceVideoDecodeDefaultsToEmpty()
    {
        var device = new DeviceCapabilities([], [], [], [], 2, new(), new());
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt-id", null, PlaybackSelection.Auto, device, Identity);

        Assert.Empty(device.VideoDecode);
        Assert.Empty(start.ClientCapabilities.VideoDecode);
    }

    [Fact]
    public void BuilderCopiesDeviceVideoDecodeEntries()
    {
        var device = ConsoleEvidence.Default with
        {
            VideoDecode = [new("h264", "test decoder", ["high"], [40], [8], 1280, 720, 30, 5000, true)],
        };
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt-id", null, PlaybackSelection.Auto, device, Identity);

        Assert.Same(device.VideoDecode, start.ClientCapabilities.VideoDecode);
    }

    [Fact]
    public async Task ConsoleClaimsOnlyTheMeasuredBaselineInEveryDelivery()
    {
        var stub = new StubHandler().Enqueue(Capabilities).Enqueue(HttpStatusCode.Created, Decision);
        using var client = Create(stub);
        await client.Playback.StartAsync(Start(), new Installation());
        using var json = JsonDocument.Parse(stub.RequestBodies[1]!);
        var caps = json.RootElement.GetProperty("client_capabilities");
        Assert.Equal(new[] { "mp4", "mkv" }, Strings(caps.GetProperty("containers")));
        Assert.Equal(new[] { "h264", "hevc", "av1" }, Strings(caps.GetProperty("codecs_video")));
        Assert.Equal(new[] { "aac", "ac3", "eac3", "mp3", "pcm", "dts", "dts_hd", "truehd", "flac", "opus", "vorbis", "alac" }, Strings(caps.GetProperty("codecs_audio")));
        Assert.False(caps.GetProperty("hdr").GetBoolean());
        Assert.Empty(caps.GetProperty("audio_passthrough").GetProperty("passthrough_codecs").EnumerateArray());
        Assert.Equal("platform_attested", caps.GetProperty("video_evidence").GetString());
        Assert.Empty(caps.GetProperty("codecs_video_hardware").EnumerateArray());
        var videoDecode = caps.GetProperty("video_decode").EnumerateArray().ToArray();
        Assert.Equal(new[] { "h264", "hevc", "av1" }, videoDecode.Select(entry => entry.GetProperty("codec").GetString()));
        Assert.Equal(new[] { 8 }, videoDecode[0].GetProperty("bit_depths").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(new[] { 8, 10 }, videoDecode[1].GetProperty("bit_depths").EnumerateArray().Select(value => value.GetInt32()));
        Assert.All(videoDecode.Take(2), entry =>
        {
            Assert.Equal("Media Foundation", entry.GetProperty("decoder_name").GetString());
            Assert.Empty(entry.GetProperty("profiles").EnumerateArray());
            Assert.Empty(entry.GetProperty("levels").EnumerateArray());
            Assert.Equal(1920, entry.GetProperty("max_width").GetInt32());
            Assert.Equal(1080, entry.GetProperty("max_height").GetInt32());
            Assert.Equal(60, entry.GetProperty("max_frame_rate").GetDouble());
            Assert.Equal(25000, entry.GetProperty("max_bitrate_kbps").GetInt32());
            Assert.True(entry.GetProperty("hardware").GetBoolean());
        });
        var av1 = videoDecode[2];
        Assert.Equal("dav1d", av1.GetProperty("decoder_name").GetString());
        Assert.False(av1.GetProperty("hardware").GetBoolean());
        Assert.Equal(new[] { "main" }, av1.GetProperty("profiles").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { 8, 10 }, av1.GetProperty("bit_depths").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(1920, av1.GetProperty("max_width").GetInt32());
        Assert.Equal(1080, av1.GetProperty("max_height").GetInt32());
        Assert.Equal(30, av1.GetProperty("max_frame_rate").GetDouble());
        Assert.False(av1.TryGetProperty("max_bitrate_kbps", out _));
        var context = json.RootElement.GetProperty("client_playback_context");
        Assert.Equal("tv", context.GetProperty("form_factor").GetString());
        Assert.Equal("0.1.0", context.GetProperty("app_version").GetString());
        Assert.Equal("xbox", context.GetProperty("device").GetProperty("platform").GetString());
        Assert.False(context.TryGetProperty("platform", out _));
        Assert.False(context.TryGetProperty("features", out _));
        Assert.Equal("unknown", context.GetProperty("output").GetProperty("display").GetProperty("hdr_evidence").GetString());
        var deliveries = context.GetProperty("deliveries");
        Assert.Equal(new[] { "original_http", "progressive", "hls" }, deliveries.EnumerateObject().Select(property => property.Name));
        foreach (var property in deliveries.EnumerateObject())
        {
            var delivery = property.Value;
            Assert.Equal(property.Name == "hls" ? new[] { "h264", "hevc" } : new[] { "h264", "hevc", "av1" },
                Strings(delivery.GetProperty("video_codecs")));
            Assert.Equal(property.Name == "hls" ? SoftwareDecodePolicy.SystemAudioCodecs : ConsoleEvidence.Default.AudioDecodeCodecs,
                Strings(delivery.GetProperty("audio_decode_codecs")));
            Assert.Empty(delivery.GetProperty("audio_passthrough_codecs").EnumerateArray());
            Assert.Equal(8, delivery.GetProperty("max_channels").GetInt32());
            var hdr = delivery.GetProperty("hdr_details");
            Assert.False(hdr.GetProperty("hdr10").GetBoolean());
            Assert.False(hdr.GetProperty("hdr10_plus").GetBoolean());
            Assert.False(hdr.GetProperty("hlg").GetBoolean());
            Assert.Empty(hdr.GetProperty("dolby_vision_profiles").EnumerateArray());
            Assert.Empty(delivery.GetProperty("transformations").EnumerateArray());
        }
        Assert.False(deliveries.GetProperty("progressive").GetProperty("enabled").GetBoolean());
        Assert.Equal(new[] { "m3u8", "hls" }, Strings(deliveries.GetProperty("hls").GetProperty("containers")));
    }

    [Fact]
    public async Task ReplanUsesTheSameTypedContextAndInjectsInstallationId()
    {
        var stub = new StubHandler().Enqueue(Decision);
        using var client = Create(stub);
        var start = Start();
        var request = new PlaybackReplanRequestV3("attempt-id", "replan-id", "failed-plan", "plan-attempt", "opaque-key",
            ["opaque-key"], 1, 45.25, new(new("file:file:audio:0", 0)), start.ClientFeatures,
            start.ClientCapabilities, start.ClientPlaybackContext)
        {
            Failure = new("decoder_failed", DecoderName: "platform"),
        };
        await client.Playback.ReplanAsync("session", request, new Installation());
        Assert.Equal("/api/v2/playback/session/replan", stub.Requests[0].RequestUri!.AbsolutePath);
        using var json = JsonDocument.Parse(stub.RequestBodies[0]!);
        var body = json.RootElement;
        Assert.Equal("installation", body.GetProperty("installation_id").GetString());
        Assert.Equal("replan-id", body.GetProperty("replan_request_id").GetString());
        Assert.Equal("opaque-key", body.GetProperty("plan_attempt_key").GetString());
        Assert.Equal(45.25, body.GetProperty("position_seconds").GetDouble());
        Assert.Equal(0, body.GetProperty("selected_tracks").GetProperty("audio").GetProperty("index").GetInt32());
        Assert.Equal("decoder_failed", body.GetProperty("failure").GetProperty("classification").GetString());
        Assert.Equal("platform", body.GetProperty("failure").GetProperty("decoder_name").GetString());
        Assert.True(body.GetProperty("client_playback_context").GetProperty("deliveries").TryGetProperty("original_http", out _));
    }

    [Fact]
    public async Task AdmissionRejectsUnsupportedOrWrongInstallationBeforeStarting()
    {
        var stub = new StubHandler().Enqueue(Capabilities);
        using var client = Create(stub);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Playback.StartAsync(Start(), new Installation("other")));
        Assert.Single(stub.Requests);
        stub.Enqueue(Capabilities.Replace("sequenced_progress_v1", "other_feature"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Playback.StartAsync(Start(), new Installation()));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task StartIsSingleAttemptIncludingAuthRejection()
    {
        var stub = new StubHandler().Enqueue(Capabilities)
            .Enqueue(HttpStatusCode.Unauthorized, """{"title":"Unauthorized","status":401}""");
        using var client = Create(stub, refresh: true);
        await Assert.ThrowsAnyAsync<ApiException>(() => client.Playback.StartAsync(Start(), new Installation()));
        Assert.Equal(2, stub.Requests.Count);
        Assert.DoesNotContain(stub.Requests, request => request.RequestUri!.AbsolutePath.Contains("refresh"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidPositionsAreRejectedLocally(double position) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Start(position: position));

    private static IEnumerable<string?> Strings(JsonElement array) => array.EnumerateArray().Select(element => element.GetString());
}
