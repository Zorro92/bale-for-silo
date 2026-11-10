using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackRoutePolicyTests
{
    [Fact]
    public void OwnerAllowlistContainsOnlyAv1AndAllSoftwareClaimsObeyIt()
    {
        Assert.Equal(new[] { "av1" }, SoftwareDecodePolicy.VideoCodecs);
        Assert.All(ConsoleEvidence.Default.VideoDecode.Where(decode => !decode.Hardware),
            decode => Assert.Contains(decode.Codec, SoftwareDecodePolicy.VideoCodecs));
        var av1 = Assert.Single(ConsoleEvidence.Default.VideoDecode, decode => !decode.Hardware);
        Assert.Equal("dav1d", av1.DecoderName);
        Assert.Equal(new[] { 8, 10 }, av1.BitDepths);
        Assert.Equal(1920, av1.MaxWidth);
        Assert.Equal(1080, av1.MaxHeight);
        Assert.Equal(30, av1.MaxFrameRate);
        Assert.Null(av1.MaxBitrateKbps);
        Assert.Equal(8, ConsoleEvidence.Default.MaxChannels);
        Assert.Empty(ConsoleEvidence.Default.AudioPassthroughCodecs);
        Assert.Empty(ConsoleEvidence.Default.PassthroughEntries);
    }

    [Theory]
    [InlineData("h264")]
    [InlineData("hevc")]
    [InlineData("vp9")]
    [InlineData("mpeg2video")]
    public void BuilderRejectsAnyOtherSoftwareVideoClaim(string codec)
    {
        var device = ConsoleEvidence.Default with { VideoDecode = [new(codec, "invalid", [], [], [8], 1920, 1080, 30, null, false)] };
        Assert.Throws<ArgumentException>(() => PlaybackStartBuilder.Build("file", "profile", "attempt", null,
            PlaybackSelection.Auto, device, PlaybackTestSupport.Identity));
    }

    [Theory]
    [InlineData("original_http", "av1", "eac3", true, true)]
    [InlineData("progressive", "av1", "aac", true, true)]
    [InlineData("original_http", "hevc", "dts", true, false)]
    [InlineData("original_http", "", "flac", true, false)]
    [InlineData("original_http", "h264", "truehd", true, false)]
    [InlineData("original_http", "hevc", "eac3", false, false)]
    [InlineData("progressive", "h264", "aac", false, false)]
    [InlineData("original_http", "vp9", "aac", false, false)]
    [InlineData("original_http", "mpeg2video", "aac", false, false)]
    [InlineData("original_http", "unknown", "unknown", false, false)]
    [InlineData("unknown", "av1", "dts", false, false)]
    [InlineData("hls", "av1", "dts", false, false)]
    [InlineData("server_transcode_hls", "av1", "truehd", false, false)]
    public void RoutesOnlyTheAllowedVideoOrFallbackAudio(string delivery, string video, string audio, bool ffmpeg, bool softwareVideo)
    {
        var route = PlaybackRoutePolicy.Decide(Plan(delivery, video, audio), false);
        Assert.Equal(ffmpeg, route.UseFfmpeg);
        Assert.Equal(softwareVideo, route.SoftwareVideo);
        Assert.Equal(3, route.AudioOrdinal);
    }

    [Theory]
    [InlineData("dts")]
    [InlineData("dts_hd")]
    [InlineData("truehd")]
    [InlineData("flac")]
    [InlineData("opus")]
    [InlineData("vorbis")]
    [InlineData("alac")]
    public void EveryClaimedAudioFallbackUsesFfmpegButNeverSoftwareHevc(string audio)
    {
        Assert.Contains(audio, ConsoleEvidence.Default.AudioDecodeCodecs);
        var route = PlaybackRoutePolicy.Decide(Plan("original_http", "hevc", audio), false);
        Assert.True(route.UseFfmpeg);
        Assert.True(route.SoftwareAudio);
        Assert.False(route.SoftwareVideo);
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("ac3")]
    [InlineData("eac3")]
    [InlineData("mp3")]
    [InlineData("pcm")]
    [InlineData("pcm_s24le")]
    [InlineData("EC-3")]
    public void SystemAudioNeverMovesOrdinaryHevcOntoFfmpeg(string audio) =>
        Assert.False(PlaybackRoutePolicy.Decide(Plan("original_http", "hevc", audio), false).UseFfmpeg);

    [Fact]
    public void HlsDetectionAndDebugOffAlwaysOverrideCodecPolicy()
    {
        var plan = Plan("original_http", "av1", "dts");
        Assert.False(PlaybackRoutePolicy.Decide(plan, true).UseFfmpeg);
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, ffmpegEnabled: false).UseFfmpeg);
        plan.Stream = new StreamV3 { Protocol = "HLS" };
        Assert.False(PlaybackRoutePolicy.Decide(plan, false).UseFfmpeg);
    }

    [Theory]
    [InlineData("original_http", "h264", "ac3")]
    [InlineData("original_http", "hevc", "eac3")]
    [InlineData("progressive", "h264", "aac")]
    public void DebugForceMovesDirectSystemCodecsOntoFfmpegWithSystemVideo(string delivery, string video, string audio)
    {
        var plan = Plan(delivery, video, audio);
        Assert.False(PlaybackRoutePolicy.Decide(plan, false).UseFfmpeg);
        var route = PlaybackRoutePolicy.Decide(plan, false, forceFfmpeg: true);
        Assert.True(route.UseFfmpeg);
        Assert.False(route.SoftwareVideo);
        Assert.False(route.SoftwareAudio);
        // HLS and the debug off switch still win over the force.
        Assert.False(PlaybackRoutePolicy.Decide(plan, true, forceFfmpeg: true).UseFfmpeg);
        Assert.False(PlaybackRoutePolicy.Decide(plan, false, ffmpegEnabled: false, forceFfmpeg: true).UseFfmpeg);
        Assert.False(PlaybackRoutePolicy.Decide(Plan("server_transcode_hls", video, audio), false, forceFfmpeg: true).UseFfmpeg);
    }

    [Fact]
    public void OriginalUsesTheSelectedSourceCodecAndProgressiveUsesItsActualRecipe()
    {
        var plan = Plan("original_http", "av1", "dts");
        plan.EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "hevc", AudioCodec = "aac" };
        Assert.True(PlaybackRoutePolicy.Decide(plan, false).SoftwareVideo);
        plan.Delivery = "progressive";
        Assert.False(PlaybackRoutePolicy.Decide(plan, false).UseFfmpeg);
        plan.EffectiveRecipe.AudioCodec = "flac";
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.True(route.UseFfmpeg);
        Assert.False(route.SoftwareVideo);
        plan.Delivery = "server_remux_progressive";
        Assert.Equal(0, PlaybackRoutePolicy.Decide(plan, false).AudioOrdinal);
    }

    [Theory]
    [InlineData("original_http", 3840L, 2160L)]
    [InlineData("progressive", 1280L, 720L)]
    [InlineData("server_remux_progressive", 1280L, 720L)]
    public void VideoDimensionsFollowTheSameSourceAsTheVideoCodec(string delivery, long width, long height)
    {
        var plan = Plan(delivery, "av1", "aac");
        plan.Source!.Width = 3840;
        plan.Source.Height = 2160;
        plan.EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "av1", Width = 1280, Height = 720 };
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Equal(width, route.VideoWidth);
        Assert.Equal(height, route.VideoHeight);
        Assert.Equal(delivery == "original_http" ? SoftwareDecodeTuning.Above1080pDav1dMaxFrameDelay : (int?)null,
            SoftwareDecodeTuning.ForVideo(route.SoftwareVideo, route.VideoWidth, route.VideoHeight).Dav1dMaxFrameDelay);
    }

    [Theory]
    [InlineData("progressive", null, null, 3840L, 2160L)]
    [InlineData("progressive", 1280L, null, 1280L, 2160L)]
    [InlineData("progressive", null, 720L, 3840L, 720L)]
    [InlineData("server_remux_progressive", null, null, 3840L, 2160L)]
    [InlineData("server_remux_progressive", 1280L, null, 1280L, 2160L)]
    [InlineData("server_remux_progressive", null, 720L, 3840L, 720L)]
    public void MissingRecipeDimensionsFallBackToTheSourceIndependently(string delivery, long? recipeWidth, long? recipeHeight,
        long width, long height)
    {
        var plan = Plan(delivery, "av1", "aac");
        plan.Source!.Width = 3840;
        plan.Source.Height = 2160;
        plan.EffectiveRecipe = new EffectiveRecipeV3 { Width = recipeWidth, Height = recipeHeight };
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Equal(width, route.VideoWidth);
        Assert.Equal(height, route.VideoHeight);
        plan.EffectiveRecipe = null;
        route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Equal(3840, route.VideoWidth);
        Assert.Equal(2160, route.VideoHeight);
    }

    [Fact]
    public void OriginalDoesNotUseRecipeDimensionsWhenSourceDimensionsAreUnknown()
    {
        var plan = Plan("original_http", "av1", "aac");
        plan.EffectiveRecipe = new EffectiveRecipeV3 { Width = 3840, Height = 2160 };
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Null(route.VideoWidth);
        Assert.Null(route.VideoHeight);
    }

    [Fact]
    public void RecipeDimensionsDoNotRequireASource()
    {
        var plan = new PlaybackPlan
        {
            Delivery = "progressive",
            EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "av1", Width = 3840, Height = 2160 },
        };
        var route = PlaybackRoutePolicy.Decide(plan, false);
        Assert.Equal(3840, route.VideoWidth);
        Assert.Equal(2160, route.VideoHeight);
    }

    [Theory]
    [InlineData("original_http")]
    [InlineData("progressive")]
    public void MissingSourceAndRecipeLeaveDimensionsUnknown(string delivery)
    {
        var route = PlaybackRoutePolicy.Decide(new PlaybackPlan { Delivery = delivery }, false);
        Assert.Null(route.VideoWidth);
        Assert.Null(route.VideoHeight);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    public void PlannedAudioOrdinalMapsDemuxOrderToBestStreamFirstOrder(long ordinal, int index) =>
        Assert.Equal(index, PlaybackRoutePolicy.AudioTrackIndex(ordinal, [7, 1, 4]));

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(3L)]
    [InlineData(long.MaxValue)]
    public void MissingOrOutOfRangePlannedAudioFailsInsteadOfPlayingTheDefault(long? ordinal) =>
        Assert.Throws<InvalidDataException>(() => PlaybackRoutePolicy.AudioTrackIndex(ordinal, [7, 1, 4]));

    [Fact]
    public void InvalidStreamIdentitiesFailAndVideoOnlyNeedsNoAudioSelection()
    {
        Assert.Equal(-1, PlaybackRoutePolicy.AudioTrackIndex(null, []));
        Assert.Throws<InvalidDataException>(() => PlaybackRoutePolicy.AudioTrackIndex(0, []));
        Assert.Throws<InvalidDataException>(() => PlaybackRoutePolicy.AudioTrackIndex(0, [1, 1]));
        Assert.Throws<InvalidDataException>(() => PlaybackRoutePolicy.AudioTrackIndex(0, [-1]));
        Assert.Throws<InvalidDataException>(() => PlaybackRoutePolicy.AudioTrackIndex(1, [1, 7], expectedAudioCount: 3));
        Assert.Equal(1, PlaybackRoutePolicy.AudioTrackIndex(0, [7, 1, 4], expectedAudioCount: 3));
    }

    internal static PlaybackPlan Plan(string delivery, string video, string audio) => new()
    {
        PlanId = "plan", PlanAttemptKey = "attempt-key", Delivery = delivery,
        Source = new PlaybackSource { VideoCodec = video, AudioCodec = audio },
        SelectedTracks = new SelectedTracksV3 { Audio = new TrackIdentityV3 { Id = "audio-3", Index = 3 } },
    };
}
