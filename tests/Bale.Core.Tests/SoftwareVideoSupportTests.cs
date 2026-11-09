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
}
