using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

/// <summary>Track-label cases from TvPlaybackFormattingTest.kt, plus the shared AutoSubtitleResolverTest.kt.</summary>
public class PlaybackTrackFormattingTests
{
    [Fact]
    public void AudioLabelIncludesLanguageCodecAndLayoutWithAnAutoAnnotation()
    {
        var version = new FileVersion { AudioTracks = [new AudioTrack { Language = "English", Codec = "EAC3", Layout = "5.1", Default = true }] };
        Assert.Equal("Auto: English · EAC3 · 5.1", PlaybackFormatting.AudioValueLabel(version));
        Assert.Equal("English · EAC3 · 5.1", PlaybackFormatting.AudioValueLabel(version, 0));
        version.AudioTracks[0] = new AudioTrack { Language = "eng", Codec = "aac", Layout = "stereo" };
        Assert.Equal("Auto: English · AAC · Stereo", PlaybackFormatting.AudioValueLabel(version));
        Assert.Equal("Unknown", PlaybackFormatting.AudioValueLabel(new FileVersion()));
        Assert.Equal("Unknown", PlaybackFormatting.AudioValueLabel(null));
    }

    [Fact]
    public void AudioResolutionUsesSelectedThenAutomaticThenEffectiveThenDefaultThenFirst()
    {
        var version = new FileVersion
        {
            AudioTracks = [new AudioTrack { Language = "eng", Default = true }, new AudioTrack { Language = "fre" }],
            EffectiveAudioTrackIndex = 1,
        };
        Assert.Equal("Auto: French", PlaybackFormatting.AudioValueLabel(version));
        Assert.Equal("English", PlaybackFormatting.AudioValueLabel(version, 0));
        Assert.Equal("Auto: English", PlaybackFormatting.AudioValueLabel(version, automaticAudioTrackOrdinal: 0));
        Assert.Equal("French", PlaybackFormatting.AudioValueLabel(version, 7, 7));
        version.EffectiveAudioTrackIndex = 7;
        Assert.Equal("Auto: English", PlaybackFormatting.AudioValueLabel(version));
        version.AudioTracks[0].Default = false;
        Assert.Equal("Auto: English", PlaybackFormatting.AudioValueLabel(version));
        Assert.Equal("eng", PlaybackFormatting.ResolvedAudioLanguage(version));
        Assert.Equal("fre", PlaybackFormatting.ResolvedAudioLanguage(version, 1));
        Assert.Null(PlaybackFormatting.ResolvedAudioLanguage(null));
    }

    [Theory]
    [InlineData("e-ac-3", 6L, "EAC3 · 5.1")]
    [InlineData("ac-3", 8L, "AC3 · 7.1")]
    [InlineData("truehd", 2L, "TrueHD · Stereo")]
    [InlineData("dts-hd", 1L, "DTS · Mono")]
    [InlineData("flac", 4L, "FLAC · 4ch")]
    public void AudioNormalizesCodecAndChannelCount(string codec, long channels, string expected) =>
        Assert.Equal(expected, PlaybackFormatting.AudioValueLabel(new FileVersion { AudioTracks = [new AudioTrack { Codec = codec, Channels = channels }] }, 0));

    [Fact]
    public void AudioFallsBackToUsefulTitleThenTrackNumber()
    {
        var version = new FileVersion { AudioTracks = [new AudioTrack { Title = "Director Commentary" }] };
        Assert.Equal("Director Commentary", PlaybackFormatting.AudioValueLabel(version, 0));
        version.AudioTracks[0].Title = "ATSC A/52 AC-3";
        Assert.Equal("Track 1", PlaybackFormatting.AudioValueLabel(version, 0));
    }

    [Fact]
    public void SubtitleAutoWithoutContextIsBareExceptForASingleTrack()
    {
        var version = new FileVersion { SubtitleTracks = [Sub("eng"), Sub("fre")] };
        version.SubtitleTracks[0].Default = true;
        Assert.Equal("Auto", PlaybackFormatting.SubtitleValueLabel(version));
        version.SubtitleTracks.RemoveAt(1);
        Assert.Equal("English", PlaybackFormatting.SubtitleValueLabel(version));
        Assert.Equal("Auto", PlaybackFormatting.SubtitleValueLabel(new FileVersion()));
        Assert.Equal("Off", PlaybackFormatting.SubtitleValueLabel(null, -1));
        Assert.Equal("On", PlaybackFormatting.SubtitleValueLabel(null, 0));
        Assert.Equal("On", PlaybackFormatting.SubtitleValueLabel(version, 7));
    }

    [Fact]
    public void SubtitleSelectionUsesCombinedSpaceNotStreamOrCatalogIndex()
    {
        var version = new FileVersion
        {
            SubtitleTracks = [Sub("dan", index: 2), Sub("eng", index: 3), Sub("dan", external: true), Sub("swe", external: true)],
        };
        Assert.Equal([2, 3, 0, 1], PlaybackFormatting.CombinedSubtitleSelectionIndexes(version.SubtitleTracks));
        Assert.Equal("English", PlaybackFormatting.SubtitleValueLabel(version, 3));
        Assert.Equal("Swedish", PlaybackFormatting.SubtitleValueLabel(version, 1));
    }

    [Theory]
    [InlineData("fre", null, true, "French (Forced)")]
    [InlineData("eng", "English SDH", false, "English (SDH)")]
    [InlineData("eng", "EN - HI", false, "English")]
    [InlineData(null, "cc", false, "Track 1 (SDH)")]
    [InlineData(null, "Signs", false, "Signs")]
    public void SubtitleBadgesAndCustomTitlesMatchOfficial(string? language, string? title, bool forced, string expected)
    {
        var track = Sub(language, title: title);
        track.Forced = forced;
        Assert.Equal(expected, PlaybackFormatting.SubtitleValueLabel(new FileVersion { SubtitleTracks = [track] }, 0));
    }

    [Theory]
    [InlineData("subrip", "SRT")]
    [InlineData("ass", "ASS")]
    [InlineData("ssa", "SSA")]
    [InlineData("webvtt", "WebVTT")]
    [InlineData("hdmv_pgs_subtitle", "PGS")]
    [InlineData("dvd_subtitle", "VobSub")]
    [InlineData("mov_text", "TX3G")]
    public void SubtitleCodecNamesAreCompact(string codec, string expected) =>
        Assert.Equal("English · " + expected, PlaybackFormatting.SubtitleValueLabel(new FileVersion { SubtitleTracks = [Sub("eng", codec)] }, 0));

    [Fact]
    public void SubtitleNeverExposesAReleaseFilenameAsItsTitle()
    {
        var version = new FileVersion { SubtitleTracks = [Sub(null, "srt", "Reasonable Doubt (2014) [Bluray-1080p x264]-GROUP.da.srt")] };
        Assert.Equal("Track 1 · SRT", PlaybackFormatting.SubtitleValueLabel(version));
    }

    [Theory]
    [InlineData("fr", "auto", "eng", false, "Auto - French")]
    [InlineData("en", "auto", "eng", false, "Auto - None")]
    [InlineData("en", "auto", "eng", true, "Auto - English (Forced)")]
    [InlineData("en", "off", "jpn", false, "Auto - None")]
    [InlineData("", "auto", "jpn", false, "Auto - None")]
    [InlineData(null, "auto", "jpn", false, "Auto - None")]
    [InlineData(null, "always", "jpn", false, "Auto - French")]
    [InlineData("en", "always", "eng", true, "Auto - English")]
    [InlineData("fr", "off", "eng", true, "Auto - English (Forced)")]
    public void SubtitleAutoPreviewUsesOfficialLanguageModeAndForcedRules(string? preferred, string mode, string audio, bool forced, string expected)
    {
        var forcedEnglish = Sub("eng");
        forcedEnglish.Forced = true;
        var version = new FileVersion { SubtitleTracks = [forcedEnglish, Sub("fre"), Sub("eng")] };
        Assert.Equal(expected, PlaybackFormatting.SubtitleValueLabel(version, autoContext: new SubtitleAutoContext(preferred, mode, forced, audio)));
    }

    [Theory]
    [InlineData("dvb_subtitle", null)]
    [InlineData("vobsub", null)]
    [InlineData("srt", "English SDH")]
    public void SubtitleAutoPrefersReadableFullDialogueOverBurnInAndSdh(string codec, string? title)
    {
        var version = new FileVersion { SubtitleTracks = [Sub("eng", codec, title), Sub("eng", "subrip")] };
        Assert.Equal("Auto - English · SRT", PlaybackFormatting.SubtitleValueLabel(version, autoContext: new SubtitleAutoContext("en", AudioLanguage: "jpn")));
    }

    [Fact]
    public void SubtitleAutoUsesExactSeriesSignatureBeforeLanguageRules()
    {
        var track = Sub("eng", "srt", "English SDH");
        var version = new FileVersion { SubtitleTracks = [Sub("eng", "srt"), track] };
        var signature = new WatchSubtitleSignature { Source = "embedded", Language = "en", Codec = "srt", Label = "English SDH", HearingImpaired = true };
        var context = new SubtitleAutoContext("fr", TrackSignature: signature);
        Assert.Equal("Auto - English (SDH) · SRT", PlaybackFormatting.SubtitleValueLabel(version, autoContext: context));
        Assert.Equal("Auto - None", PlaybackFormatting.SubtitleValueLabel(version, autoContext: context with { Mode = "off" }));
    }

    [Theory]
    [InlineData("ces", "cs-CZ")]
    [InlineData("gre", "el")]
    [InlineData("cym", "cy")]
    public void SubtitleAutoComparesAllIsoAliasesAndRegionalTags(string catalogLanguage, string preference)
    {
        var version = new FileVersion { SubtitleTracks = [Sub(catalogLanguage)] };
        Assert.Equal(0, PlaybackFormatting.AutoResolvedSubtitleOrdinal(version.SubtitleTracks, new SubtitleAutoContext(preference, "always")));
    }

    private static VersionSubtitleTrack Sub(string? language, string? codec = null, string? title = null, long index = 0, bool external = false) =>
        new() { Language = language, Codec = codec, Title = title, Index = index, External = external };
}
