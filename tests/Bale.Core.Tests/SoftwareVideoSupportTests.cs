using Bale.Core.Playback;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

public class SoftwareVideoSupportTests
{
    [Theory]
    [InlineData(true, "Xbox Series X", true)]
    [InlineData(true, "Xbox Series S", true)]
    [InlineData(true, "Xbox Series X DevKit", true)]
    [InlineData(true, null, true)] // Unknown form keeps the claim.
    [InlineData(true, "", true)]
    [InlineData(true, "Unknown", true)]
    [InlineData(true, "Xbox One", false)]
    [InlineData(true, "Xbox One S", false)]
    [InlineData(true, "Xbox One X", false)]
    [InlineData(true, "xbox one x devkit", false)]
    [InlineData(false, "Xbox Series X", false)] // No AVX2 excludes it whatever the form says.
    [InlineData(false, null, false)]
    public void OnlyAvx2ConsolesThatAreNotXboxOneClaimSoftwareVideo(bool avx2, string? form, bool expected) =>
        Assert.Equal(expected, SoftwareDecodePolicy.SoftwareVideoSupported(avx2, form));

    [Fact]
    public void WithoutSoftwareVideoDropsAv1ButKeepsHardwareVideoAndAudioFallbacks()
    {
        var device = SoftwareDecodePolicy.WithoutSoftwareVideo(ConsoleEvidence.Default);
        Assert.Equal(new[] { "h264", "hevc" }, device.VideoCodecs);
        Assert.Equal(new[] { "h264", "hevc" }, device.VideoDecode.Select(decode => decode.Codec));
        Assert.All(device.VideoDecode, decode => Assert.True(decode.Hardware));
        Assert.Equal(ConsoleEvidence.Default.AudioDecodeCodecs, device.AudioDecodeCodecs);
        Assert.Contains("av1", ConsoleEvidence.Default.VideoCodecs); // The shared default is untouched.
    }

    [Fact]
    public void AStartWithoutSoftwareVideoMentionsAv1NowhereAndDropsTheSoftwareDecodeFeature()
    {
        var device = SoftwareDecodePolicy.WithoutSoftwareVideo(ConsoleEvidence.Default);
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt-id", null, PlaybackSelection.Auto, device,
            PlaybackTestSupport.Identity);
        Assert.DoesNotContain("av1", start.ClientCapabilities.CodecsVideo);
        Assert.DoesNotContain(start.ClientCapabilities.VideoDecode, decode => decode.Codec == "av1");
        Assert.All(start.ClientPlaybackContext.Deliveries.Values, delivery => Assert.DoesNotContain("av1", delivery.VideoCodecs));
        Assert.DoesNotContain(PlaybackProtocol.SoftwareVideoDecodeFeature, start.ClientFeatures);
    }

    [Fact]
    public void SoftwareRouteWithNoVideoStreamFailsSoTheOpenReplansToATranscode()
    {
        Assert.False(SoftwareDecodePolicy.VideoDecodersAllowed(true, []));
        // A system route keeps its old behavior: no video stream is not a decoder violation.
        Assert.True(SoftwareDecodePolicy.VideoDecodersAllowed(false, []));
    }

    [Theory]
    [InlineData(true, "av1", true, false, true)]
    [InlineData(true, " AV1 ", true, false, true)]
    [InlineData(true, "av1", false, true, false)] // Software route opened on the system decoder.
    [InlineData(true, "av1", false, false, false)] // FFmpeg D3D11 or unknown engine.
    [InlineData(true, "hevc", true, false, false)] // Only allowlisted codecs may software-decode.
    [InlineData(true, null, true, false, false)]
    [InlineData(false, "hevc", false, true, true)]
    [InlineData(false, "hevc", true, false, false)] // ForceSystemDecoder fell back to FFmpeg.
    [InlineData(false, "av1", true, false, false)]
    public void EveryVideoStreamMustUseTheRouteDecoder(bool softwareVideo, string? codec, bool software, bool system, bool expected)
    {
        Assert.Equal(expected, SoftwareDecodePolicy.VideoDecodersAllowed(softwareVideo, [(codec, software, system)]));
        // One bad stream among good ones still fails.
        var good = softwareVideo ? ("av1", true, false) : ("hevc", false, true);
        Assert.Equal(expected, SoftwareDecodePolicy.VideoDecodersAllowed(softwareVideo, [good, (codec, software, system)]));
    }
}
