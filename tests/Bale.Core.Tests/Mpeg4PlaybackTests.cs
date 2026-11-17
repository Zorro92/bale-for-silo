using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using FFmpegInteropX;

namespace Bale.Core.Tests;

public class Mpeg4PlaybackTests
{
    [Fact]
    public void ProductionClaimsUnboundedSoftwareMpeg4OnEveryDeliveryAndAviOnlyOnOriginalHttp()
    {
        foreach (var device in new[] { ConsoleEvidence.Default, ConsoleEvidence.ForHardware(
            new HardwareDecodeLimits(H264: new(3840, 2160), HevcMain: new(7680, 4320))) })
        {
            var start = Start(device);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(start, PlaybackJson.Default.PlaybackStartRequestV3));
            var caps = json.RootElement.GetProperty("client_capabilities");
            var decode = Assert.Single(caps.GetProperty("video_decode").EnumerateArray(),
                entry => entry.GetProperty("codec").GetString() == "mpeg4");
            Assert.False(decode.GetProperty("hardware").GetBoolean());
            Assert.Equal("FFmpeg", decode.GetProperty("decoder_name").GetString());
            // Declared like every FFmpeg codec since 2026-11-17 (owner): no profile, depth or size bound.
            Assert.Empty(decode.GetProperty("profiles").EnumerateArray());
            Assert.Empty(decode.GetProperty("levels").EnumerateArray());
            Assert.Empty(decode.GetProperty("bit_depths").EnumerateArray());
            foreach (var bound in new[] { "max_width", "max_height", "max_frame_rate" })
                Assert.True(!decode.TryGetProperty(bound, out var value) || value.ValueKind == JsonValueKind.Null);
            Assert.Contains("mpeg4", Strings(caps.GetProperty("codecs_video")));
            Assert.DoesNotContain("mpeg4", Strings(caps.GetProperty("codecs_video_hardware")));
            Assert.Contains("avi", Strings(caps.GetProperty("containers")));
            Assert.Contains(PlaybackProtocol.SoftwareVideoDecodeFeature, start.ClientFeatures);
            var original = start.ClientPlaybackContext.Deliveries["original_http"];
            Assert.Contains("avi", original.Containers);
            Assert.Contains("mpeg4", original.VideoCodecs);
            Assert.Contains("mp3", original.AudioDecodeCodecs);
            Assert.Contains(PlaybackProtocol.ClientSelectedAudioTrackClaim, original.ValidatedClaims);
            foreach (var delivery in new[] { "progressive", "hls" })
            {
                Assert.DoesNotContain("avi", start.ClientPlaybackContext.Deliveries[delivery].Containers);
                // FFmpeg reads the package, so MPEG-4 Part 2 is claimed there too (owner, 2026-11-16).
                Assert.Contains("mpeg4", start.ClientPlaybackContext.Deliveries[delivery].VideoCodecs);
            }
        }
        var raisedAv1 = ConsoleEvidence.BuildVideoDecode(3840, 2160).Single(entry => entry.Codec == "mpeg4");
        Assert.Null(raisedAv1.MaxWidth);
    }

    [Theory]
    [InlineData("Advanced Simple Profile", 1920, 1080, 8, "30000/1001", true)]
    [InlineData("Simple Profile", 720, 480, 8, "30", true)]
    [InlineData("asp", 720, 480, 8, "24", true)]
    [InlineData(null, 720, 480, 8, "24", true)]
    [InlineData("Advanced Simple Profile", 3840, 2160, 8, "24", true)]
    [InlineData("Advanced Simple Profile", 1920, 1080, 10, "24", true)]
    [InlineData("Advanced Simple Profile", 720, 480, 8, "60", true)]
    public void ProfileSpellingsAndBoundsAgreeWithTheServer(string? profile, int width, int height,
        int depth, string rate, bool expected)
    {
        // Since 2026-11-17 (owner: declare everything FFmpeg decodes) the MPEG-4 software entry has no
        // profile, depth, size or rate bound, so every MPEG-4 file is taken (the console decodes it in FFmpeg).
        var version = new FileVersion
        {
            CodecVideo = "mpeg4", Container = "avi", VideoTracks =
            [new VideoTrack { Codec = "mpeg4", Profile = profile, Width = width, Height = height, BitDepth = depth, FrameRate = rate }],
        };
        Assert.Equal(expected, PlaybackFormatting.Decodes(version, ConsoleEvidence.Default.VideoDecode));
    }

    [Theory]
    [InlineData("avi", "mpeg4", true)]
    [InlineData(" AVI ", "MPEG4", true)]
    [InlineData("mkv", "mpeg4", true)]
    [InlineData("mp4", "mpeg4", true)]
    [InlineData("avi", "h264", false)]
    public void OriginalMpeg4UsesOrdinarySoftwareAndAviAlwaysUsesFfmpeg(string container, string video, bool software)
    {
        var plan = Plan(container, video);
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.True(route.UseFfmpeg);
        Assert.Equal(software, route.SoftwareVideo);
        Assert.False(route.RequiresD3D11);
        Assert.False(route.SoftwareAudio); // MP3 is handed to the system audio decoder.
        var config = FfmpegSource.Configuration(route);
        Assert.Equal(software ? VideoDecoderMode.ForceFFmpegSoftwareDecoder : VideoDecoderMode.ForceSystemDecoder,
            config.Video.VideoDecoderMode);
        Assert.True(config.Audio.SystemDecoderMP3);
        foreach (var key in new[] { "bale_zero_copy", "bale_d3d11_upload", "bale_film_grain_mode", "bale_dav1d_max_frame_delay" })
            Assert.DoesNotContain(key, config.FFmpegOptions.Keys);
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, ffmpegEnabled: false).UseFfmpeg);
        Assert.False(PlaybackRoutePolicy.Decide(plan, true).UseFfmpeg);
        plan.Stream!.Protocol = "hls";
        Assert.False(PlaybackRoutePolicy.Decide(plan, false).UseFfmpeg);
    }

    [Fact]
    public void Mpeg4DoesNotAcquireAv1GrainOptionsAndRejectsUnexpectedVideoEnginesOrCodecs()
    {
        var route = PlaybackRoutePolicy.Decide(Plan(), false);
        var config = FfmpegSource.Configuration(route, new(FilmGrain: DebugFilmGrain.Gpu));
        Assert.DoesNotContain("bale_film_grain_mode", config.FFmpegOptions.Keys);
        Assert.DoesNotContain("bale_zero_copy", config.FFmpegOptions.Keys);
        var source = new FFmpegMediaSource();
        Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(source, route));
        source.VideoStreams.Add(new() { CodecName = "mpeg4", DecoderEngine = DecoderEngine.FFmpegSoftwareDecoder });
        FfmpegSource.ValidateVideoDecoders(source, route);
        Assert.Equal("FFmpeg software", FfmpegSource.DecoderName(source.CurrentVideoStream!.DecoderEngine));
        foreach (var engine in new[] { DecoderEngine.SystemDecoder, DecoderEngine.FFmpegD3D11HardwareDecoder })
        {
            source.VideoStreams[0].DecoderEngine = engine;
            Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(source, route));
        }
        source.VideoStreams[0].DecoderEngine = DecoderEngine.FFmpegSoftwareDecoder;
        source.VideoStreams.Add(new() { CodecName = "av1", DecoderEngine = DecoderEngine.FFmpegSoftwareDecoder });
        // AV1 cannot ride the MPEG-4 route without its own zero-copy configuration (or vice versa).
        Assert.Throws<InvalidDataException>(() => FfmpegSource.ValidateVideoDecoders(source, route));
    }

    [Fact]
    public void Mp3SelectionKeepsTheServerOrdinalWhenFfmpegReordersTracks()
    {
        var selected = -1;
        var index = PlaybackOriginalAudioSelection.Select(1,
            [new AudioTrack { Codec = "mp3", Channels = 2 }, new AudioTrack { Codec = "mp3", Channels = 2 }],
            [new(5, null, "mp3", 2, null, true), new(1, null, "mp3", 2, null, true)],
            ffmpeg: true, value => selected = value, () => selected);
        Assert.Equal(0, index);
        Assert.Equal(0, selected);
    }

    [Fact]
    public void FfmpegOffAndFailureWithdrawMpeg4AndAviWhileSeekKeepsThem()
    {
        var start = Start(ConsoleEvidence.Default);
        var off = Start(DebugFfmpegPlayback.Capabilities("off"));
        AssertWithdrawn(off.ClientCapabilities, off.ClientPlaybackContext);
        var plan = Plan();
        var route = PlaybackRoutePolicy.Decide(plan, false);
        var seek = PlaybackReplan.ForSeek(start, plan, "mount", [], 1, 60);
        Assert.Same(start.ClientCapabilities, seek.ClientCapabilities);
        Assert.Same(start.ClientPlaybackContext, seek.ClientPlaybackContext);
        var failed = PlaybackReplan.ForFailure(start, plan, "mount", [], 1, 60, new("player_failure"), route);
        AssertWithdrawn(failed.ClientCapabilities, failed.ClientPlaybackContext);
        Assert.Contains("server transcode required", failed.Failure!.Message);
        Assert.DoesNotContain(PlaybackProtocol.SoftwareVideoDecodeFeature, failed.ClientFeatures);
        Assert.Contains("mp3", failed.ClientCapabilities.CodecsAudio);
        Assert.Equal(1, failed.SelectedTracks.Audio!.Index);
        Assert.Contains("mpeg4", start.ClientCapabilities.CodecsVideo);
        Assert.Contains("avi", start.ClientPlaybackContext.Deliveries["original_http"].Containers);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, false, false)]
    public void Mpeg4NeedsMeasuredSoftwareSupportButDoesNotDependOnAv1ZeroCopy(bool supported, bool zeroCopy,
        bool av1, bool mpeg4)
    {
        var device = SoftwareDecodePolicy.ForSoftwareVideo(ConsoleEvidence.Default, supported, zeroCopy);
        var start = Start(device);
        Assert.Equal(av1, start.ClientCapabilities.CodecsVideo.Contains("av1"));
        Assert.Equal(mpeg4, start.ClientCapabilities.CodecsVideo.Contains("mpeg4"));
        Assert.Equal(mpeg4, start.ClientCapabilities.VideoDecode.Any(entry => entry.Codec == "mpeg4"));
        Assert.Equal(av1 || mpeg4, start.ClientFeatures.Contains(PlaybackProtocol.SoftwareVideoDecodeFeature));
    }

    private static void AssertWithdrawn(ClientCodecCapabilities caps, ClientPlaybackContext context)
    {
        Assert.DoesNotContain("mpeg4", caps.CodecsVideo);
        Assert.DoesNotContain(caps.VideoDecode, entry => entry.Codec == "mpeg4");
        Assert.DoesNotContain("avi", caps.Containers);
        Assert.All(context.Deliveries.Values, delivery =>
        {
            Assert.DoesNotContain("mpeg4", delivery.VideoCodecs);
            Assert.DoesNotContain("avi", delivery.Containers);
        });
    }

    private static IEnumerable<string?> Strings(JsonElement array) => array.EnumerateArray().Select(entry => entry.GetString());

    private static PlaybackStartRequestV3 Start(DeviceCapabilities device) => PlaybackStartBuilder.Build("file", "profile",
        "attempt", null, new(AudioIndex: 1), device, PlaybackTestSupport.Identity);

    private static PlaybackPlan Plan(string container = "avi", string video = "mpeg4") => new()
    {
        PlanId = "plan", PlanAttemptKey = "plan-key", Delivery = "original_http",
        Source = new PlaybackSource { Container = container, VideoCodec = video, AudioCodec = "mp3", Width = 1920, Height = 1080 },
        Stream = new StreamV3 { Container = container, Protocol = "http_progressive" },
        SelectedTracks = new SelectedTracksV3 { Audio = new TrackIdentityV3 { Id = "file:file:audio:1", Index = 1 } },
    };
}
