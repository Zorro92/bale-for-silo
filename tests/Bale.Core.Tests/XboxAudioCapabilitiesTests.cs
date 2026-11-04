using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class XboxAudioCapabilitiesTests
{
    [Fact]
    public void DetailAndPlanningShareDecodeCapabilitiesButClaimNoPassthrough()
    {
        Assert.Same(ConsoleEvidence.Default.AudioDecodeCodecs, XboxAudioCapabilities.Current.DecodeCodecs);
        Assert.Null(XboxAudioCapabilities.Current.Passthrough);
    }

    [Theory]
    [InlineData("aac", 2)]
    [InlineData("ac3", 6)]
    [InlineData("eac3", 8)]
    [InlineData("pcm_s16le", 2)]
    [InlineData("pcm_s24le", 6)]
    [InlineData("pcm_f32le", 8)]
    public void CompatibleLosslessAudioCanUseTheFallbackInsteadOfForcingALowerQualityTrack(string codec, int channels)
    {
        AudioTrack[] tracks = [new() { Language = "en", Codec = "truehd", Channels = 8 },
            new() { Language = "en", Codec = codec, Channels = channels }];
        Assert.Equal(0, PlaybackAudioSelection.SelectBestCompatibleAudioTrackOrdinal(tracks, "en", XboxAudioCapabilities.Current));
    }

    [Fact]
    public void AudioFallbackPreservesTheOfficialLanguageAndQualityRanking()
    {
        AudioTrack[] tracks = [new() { Language = "spa", Codec = "truehd", Channels = 8 },
            new() { Language = "es", Codec = "dts_hd", Channels = 8 },
            new() { Language = "es", Codec = "dts", Channels = 6 },
            new() { Language = "es", Codec = "ac3", Channels = 6 }];
        Assert.Equal(0, PlaybackAudioSelection.SelectBestCompatibleAudioTrackOrdinal(tracks, "es", XboxAudioCapabilities.Current));
    }
}
