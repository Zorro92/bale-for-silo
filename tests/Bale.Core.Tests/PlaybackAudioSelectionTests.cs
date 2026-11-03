using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

/// <summary>TrackSelectionPresetsFfmpegTest.kt's automatic audio cases, plus channel/ranking boundaries.</summary>
public class PlaybackAudioSelectionTests
{
    private static readonly ClientAudioCapabilities Dolby = new(["aac", "ac3", "eac3", "pcm"]);

    [Theory]
    [InlineData("  es  ", "en", "es")]
    [InlineData("", "  ja ", "ja")]
    [InlineData(" \t ", "fr", "fr")]
    [InlineData(null, "es", "es")]
    [InlineData(null, " ", null)]
    [InlineData(null, null, null)]
    public void SettingsLanguageWinsThenProfileWithBlankValuesIgnored(string? settings, string? profile, string? expected) =>
        Assert.Equal(expected, PlaybackAudioSelection.EffectivePreferredAudioLanguage(settings, profile));

    [Fact]
    public void PreferredLanguageFallsFromUnsupportedTrueHdToEac3()
    {
        AudioTrack[] tracks = [Track("ja", "truehd", 8), Track("jpn", "eac3", 6), Track("en", "aac", 2, isDefault: true)];
        Assert.Equal(1, Select(tracks, "ja"));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("eng")]
    public void EnglishIsTheFallbackEvenWhenAnotherLanguageIsDefault(string? preferred)
    {
        AudioTrack[] tracks = [Track("de", "aac", 2, isDefault: true), Track("eng", "eac3", 6)];
        Assert.Equal(1, Select(tracks, preferred));
    }

    [Fact]
    public void AdaptablePreferredLanguageBeatsDirectlyCompatibleEnglish()
    {
        AudioTrack[] tracks = [Track("fr", "truehd", 8, "Main"), Track("en", "aac", 2, isDefault: true)];
        Assert.Equal(0, Select(tracks, "fr"));
    }

    [Fact]
    public void AdaptablePreferredMainMixBeatsCompatibleCommentary()
    {
        AudioTrack[] tracks = [Track("fr", "truehd", 8, "Main"), Track("fra", "aac", 2, "Director Commentary")];
        Assert.Equal(0, Select(tracks, "fr"));
    }

    [Fact]
    public void TrueHdSevenPointOneWinsWhenOutputSupportsIt()
    {
        AudioTrack[] tracks = [Track("en", "eac3", 6), Track("en", "truehd", 8)];
        var capabilities = Dolby with { Passthrough = new(["truehd", "eac3"], 8) };
        Assert.Equal(1, Select(tracks, "en", capabilities));
    }

    [Theory]
    [InlineData("Director Commentary")]
    [InlineData("Audio description")]
    [InlineData("Descriptive audio")]
    [InlineData("Isolated Score")]
    public void QualityDoesNotPromoteCommentaryOverMainMix(string title)
    {
        AudioTrack[] tracks = [Track("en", "truehd", 8, title), Track("en", "eac3", 6, "Main")];
        Assert.Equal(1, Select(tracks, "en", new(["truehd", "eac3"])));
        Assert.Equal(0, Select([tracks[0]], "en", new(["truehd"])));
    }

    [Fact]
    public void NoPreferredOrEnglishTrackUsesCompatibleDefaultBeforeQuality()
    {
        AudioTrack[] tracks = [Track("ja", "eac3", 6), Track("de", "aac", 2, isDefault: true), Track("it", "truehd", 8)];
        Assert.Equal(1, Select(tracks, "fr"));
        tracks[1].Default = false;
        Assert.Equal(0, Select(tracks, "fr"));
        tracks[1].Default = true;
        tracks[2].Default = true;
        Assert.Equal(2, Select(tracks, "fr", new([]) { Passthrough = new(["truehd"], 8) }));
    }

    [Fact]
    public void NoCompatibleTrackStillChoosesAnAdaptableSourceAndHonorsDefaultOutsideLanguageTiers()
    {
        AudioTrack[] tracks = [Track("ja", "truehd", 8), Track("de", "dts_hd", 6, isDefault: true)];
        Assert.Equal(1, Select(tracks, "fr", new([])));
        tracks[1].Default = false;
        Assert.Equal(0, Select(tracks, "fr", new([])));
        Assert.Equal(1, Select(tracks, "de", new([])));
    }

    [Fact]
    public void TiesUseChannelsThenBitrateThenDefaultThenEarlierCatalogOrdinal()
    {
        AudioTrack[] tracks = [Track("en", "ac3", 2, isDefault: true), Track("en", "ac3", 6)];
        Assert.Equal(1, Select(tracks));
        tracks[0].Channels = 6;
        tracks[1].Bitrate = 640_000;
        Assert.Equal(1, Select(tracks));
        tracks[0].Bitrate = 640_000;
        Assert.Equal(0, Select(tracks));
        tracks[0].Default = false;
        Assert.Equal(0, Select(tracks));
    }

    [Theory]
    [InlineData("truehd", "eac3_joc")]
    [InlineData("audio/eac3-joc", "eac3")]
    [InlineData("ec-3", "dts_hd")]
    [InlineData("DTS-MA", "flac")]
    [InlineData("flac", "ac4")]
    [InlineData("ac4", "dts")]
    [InlineData("audio/vnd.dts", "ac3")]
    [InlineData("ac-3", "mp4a.40.2")]
    [InlineData("audio/mp4a-latm", "opus")]
    [InlineData("opus", "vorbis")]
    [InlineData("vorbis", "mp3")]
    [InlineData("mpeg", "pcm_s16le")]
    [InlineData("raw", "unknown")]
    public void CodecQualityRanksAndAliasesMatchTheOfficialOrder(string higher, string lower)
    {
        AudioTrack[] tracks = [Track("en", lower, 8, isDefault: true), Track("en", higher, 2)];
        Assert.Equal(1, Select(tracks, "en", new([higher, lower])));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(6, 0)]
    [InlineData(7, 0)]
    [InlineData(8, 1)]
    [InlineData(0, 0)]
    public void AggregatePassthroughLimitsOnlyApplyWithoutExactEntries(int channels, int expected)
    {
        AudioTrack[] tracks = [Track("en", "truehd", channels), Track("en", "eac3", 6)];
        Assert.Equal(expected, Select(tracks, "en", Dolby with { Passthrough = new(["truehd"], 7) }));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(6, 0)]
    [InlineData(7, 1)]
    [InlineData(8, 1)]
    [InlineData(5, 0)]
    [InlineData(0, 0)]
    public void ExactEntriesRefuseUnlistedProbedCountsAndUseTheirOwnMaximum(int channels, int expected)
    {
        // The sink's eight-channel TrueHD must not vouch for EAC3 above its own six-channel limit.
        var capabilities = new ClientAudioCapabilities(["aac"], new(["eac3", "truehd"], 8,
            [new("eac3", [6]), new("truehd", [2, 6, 8])]));
        AudioTrack[] tracks = [Track("en", "eac3", channels), Track("en", "aac", 2)];
        Assert.Equal(expected, Select(tracks, "en", capabilities));
    }

    [Theory]
    [InlineData("eac3_joc", "eac3")]
    [InlineData("dts_hd", "dts")]
    public void PassthroughCanUseACompatibleBaseCodec(string codec, string baseCodec)
    {
        AudioTrack[] tracks = [Track("en", "aac", 2), Track("en", codec, 6)];
        Assert.Equal(1, Select(tracks, "en", new(["aac"], new([baseCodec], 6))));
        Assert.Equal(0, Select(tracks, "en", new(["aac"], new([baseCodec], 2))));
        Assert.Equal(0, Select(tracks, "en", new(["aac"], new([], 8))));
    }

    [Fact]
    public void AacIsAlwaysReachableAndDecoderSupportDoesNotRequirePassthrough()
    {
        AudioTrack[] tracks = [Track("en", "ac3", 6), Track("en", "aac", 2)];
        Assert.Equal(1, Select(tracks, "en", new([])));
        Assert.Equal(0, Select(tracks, "en", new(["ac3"])));
    }

    [Fact]
    public void MissingInputsAndEmptyTracksHaveNoDeviceAnswer()
    {
        Assert.Null(PlaybackAudioSelection.ResolveAutomaticAudioTrackOrdinal(null, "en", Dolby));
        Assert.Null(PlaybackAudioSelection.ResolveAutomaticAudioTrackOrdinal(new FileVersion(), "en", Dolby));
        Assert.Null(PlaybackAudioSelection.ResolveAutomaticAudioTrackOrdinal(new FileVersion { AudioTracks = [Track("en", "aac", 2)] }, "en", null));
    }

    [Fact]
    public void ServerEffectiveOrdinalWinsWithoutCapabilitiesAndInvalidIndexesFallBackToDevicePolicy()
    {
        var version = new FileVersion { AudioTracks = [Track("en", "aac", 2), Track("es", "ac3", 6)], EffectiveAudioTrackIndex = 1 };
        Assert.Equal(1, PlaybackAudioSelection.AutomaticAudioOrdinal(version, "en", null));
        Assert.Equal(1, PlaybackAudioSelection.AutomaticAudioOrdinal(version, "en", Dolby));
        version.EffectiveAudioTrackIndex = 7;
        Assert.Equal(0, PlaybackAudioSelection.AutomaticAudioOrdinal(version, "en", Dolby));
        version.EffectiveAudioTrackIndex = -1;
        Assert.Null(PlaybackAudioSelection.AutomaticAudioOrdinal(version, "en", null));
    }

    private static int? Select(IReadOnlyList<AudioTrack> tracks, string? preferred = "en", ClientAudioCapabilities? capabilities = null) =>
        PlaybackAudioSelection.SelectBestCompatibleAudioTrackOrdinal(tracks, preferred, capabilities ?? Dolby);

    private static AudioTrack Track(string language, string codec, int channels, string? title = null, bool isDefault = false) =>
        new() { Language = language, Codec = codec, Channels = channels, Title = title, Default = isDefault };
}
