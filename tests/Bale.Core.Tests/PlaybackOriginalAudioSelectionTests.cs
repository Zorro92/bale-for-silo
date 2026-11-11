using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackOriginalAudioSelectionTests
{
    private static AudioTrack Catalog(string? language = "en", string? codec = "aac", long channels = 2, string? title = null) =>
        new() { Language = language, Codec = codec, Channels = channels, EmbeddedTitle = title };

    private static MountedPlaybackAudioTrack Mounted(int? stream, string? language = "en", string? codec = "aac",
        long channels = 2, string? title = null, bool decodable = true) => new(stream, language, codec, channels, title, decodable);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    public void FfmpegMapsCatalogOrdinalThroughSourceStreamOrderBeforeSelecting(long ordinal, int expected)
    {
        // Video/subtitles/attachments occupy the gaps. The default (stream 7) moved first.
        AudioTrack[] catalog = [Catalog("en"), Catalog("nl"), Catalog("ja")];
        MountedPlaybackAudioTrack[] mounted = [Mounted(7, "ja"), Mounted(1, "en"), Mounted(4, "nl")];
        var selected = -1;
        var calls = new List<int>();
        Assert.Equal(expected, PlaybackOriginalAudioSelection.Select(ordinal, catalog, mounted, true,
            index => { calls.Add(index); selected = index; }, () => selected));
        Assert.Equal(new[] { expected }, calls);
        Assert.Equal(expected, selected);
    }

    [Fact]
    public void FfmpegCanDistinguishOtherwiseIdenticalMainAndCommentaryByCompleteStreamOrder()
    {
        AudioTrack[] catalog = [Catalog(), Catalog()];
        Assert.Equal(0, PlaybackOriginalAudioSelection.ResolveIndex(1, catalog, [Mounted(4), Mounted(1)], true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingCatalogOrDroppedMountedTrackCannotShiftTheOrdinal(bool ffmpeg)
    {
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(0, null, [Mounted(1)], ffmpeg));
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(1,
            [Catalog(), Catalog(), Catalog()], [Mounted(1), Mounted(7)], ffmpeg));
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(0, [Catalog()], [], ffmpeg));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(2L)]
    [InlineData(long.MaxValue)]
    public void AbsentOrInvalidPlanIndexCannotPlayTheDefault(long? ordinal)
    {
        foreach (var ffmpeg in new[] { true, false })
            Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(ordinal,
                [Catalog(), Catalog("nl")], [Mounted(1), Mounted(2, "nl")], ffmpeg));
    }

    [Fact]
    public void InvalidOrDuplicateFfmpegStreamIdsCannotProveSourceOrder()
    {
        foreach (var ids in new int?[][] { [1, 1], [-1, 2], [null, 2] })
            Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(0,
                [Catalog(), Catalog()], ids.Select(id => Mounted(id)).ToArray(), true));
    }

    [Fact]
    public void FfmpegRejectsAnInventoryContradictingTheCatalogEvenWhenCountsMatch()
    {
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(1,
            [Catalog("en"), Catalog("nl")], [Mounted(1, "nl"), Mounted(2, "en")], true));
    }

    [Fact]
    public void FfmpegDisplayLanguagesAreNotMistakenForConflictingLanguageTags()
    {
        Assert.Equal(0, PlaybackOriginalAudioSelection.ResolveIndex(1,
            [Catalog("en"), Catalog("nl")], [Mounted(4, "Nederlands"), Mounted(1, "English")], true));
    }

    [Fact]
    public void FfmpegDtsCodecNameMatchesTheCatalogHdFamily()
    {
        Assert.Equal(0, PlaybackOriginalAudioSelection.ResolveIndex(0,
            [Catalog(codec: "dts_hd")], [Mounted(1, codec: "dts")], true));
    }

    [Fact]
    public void NativeMapsByUniqueIdentityRatherThanAssumingWindowsOrderOrParsingItsOpaqueIds()
    {
        Assert.Equal(0, PlaybackOriginalAudioSelection.ResolveIndex(1,
            [Catalog("en"), Catalog("nld", "e-ac-3", 6)],
            [Mounted(null, "nl", "Dolby_DDPlus", 6), Mounted(null, "eng")], false));
    }

    [Fact]
    public void NativeCanProveTheSelectedIdentityWhenAnUnrelatedTrackWasOmitted()
    {
        Assert.Equal(0, PlaybackOriginalAudioSelection.ResolveIndex(1,
            [Catalog("en", "truehd"), Catalog("nl")], [Mounted(null, "nl")], false));
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(0,
            [Catalog("en", "truehd"), Catalog("nl")], [Mounted(null, "nl")], false));
    }

    [Fact]
    public void NativeIdentityRetainsRegionalLanguageDifferences()
    {
        Assert.Equal(1, PlaybackOriginalAudioSelection.ResolveIndex(0,
            [Catalog("pt-BR"), Catalog("pt-PT")], [Mounted(null, "pt_PT"), Mounted(null, "por_BR")], false));
    }

    [Fact]
    public void NativeUsesEmbeddedTitlesToDistinguishSameLanguageCommentary()
    {
        Assert.Equal(0, PlaybackOriginalAudioSelection.ResolveIndex(1,
            [Catalog(title: "main"), Catalog(title: "commentary")],
            [Mounted(null, title: "Commentary"), Mounted(null, title: "Main")], false));
    }

    [Fact]
    public void NativeFailsClosedForAmbiguousIdentitiesOnEitherSide()
    {
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(1,
            [Catalog(), Catalog()], [Mounted(null), Mounted(null)], false));
        // Only one mounted match for Dutch, but it could be either source mix: the other mounted
        // row's language disagrees with both. Do not mistake this incomplete identity for proof.
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(1,
            [Catalog("nl"), Catalog("nl")], [Mounted(null, "en"), Mounted(null, "nl")], false));
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(0,
            [Catalog(null), Catalog(null)], [Mounted(null, null), Mounted(null, null)], false));
    }

    [Theory]
    [InlineData("en", "aac", 2)]
    [InlineData("nl", "ac3", 2)]
    [InlineData("nl", "aac", 6)]
    public void NativeRejectsContradictoryLanguageCodecOrChannels(string language, string codec, long channels) =>
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(0,
            [Catalog("nl")], [Mounted(null, language, codec, channels)], false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UndecodableTrackStillOccupiesItsPositionAndCannotBeSelected(bool ffmpeg)
    {
        AudioTrack[] catalog = [Catalog("en"), Catalog("nl")];
        MountedPlaybackAudioTrack[] mounted = [Mounted(1, decodable: false), Mounted(4, "nl")];
        var failure = Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(0, catalog, mounted, ffmpeg));
        Assert.True(failure.DecoderUnsupported);
        Assert.True(failure.Failure.AudioDecoderUnsupported);
        Assert.Equal(1, PlaybackOriginalAudioSelection.ResolveIndex(1, catalog, mounted, ffmpeg));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VideoOnlyDoesNotAttemptToSelectAudio(bool ffmpeg)
    {
        Assert.Equal(-1, PlaybackOriginalAudioSelection.Select(null, [], [], ffmpeg,
            _ => Assert.Fail("No audio to select"), () => throw new InvalidOperationException()));
        Assert.Equal(-1, PlaybackOriginalAudioSelection.ResolveIndex(null, null, [], ffmpeg));
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(0, [], [], ffmpeg));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectionMustBeConfirmedAndNativeErrorsBecomeSafeTypedFailures(bool throws)
    {
        var read = false;
        var failure = Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.Select(1,
            [Catalog("en"), Catalog("nl")], [Mounted(1), Mounted(4, "nl")], true,
            _ => { if (throws) throw new InvalidOperationException("private native diagnostic"); },
            () => { read = true; return 0; }));
        Assert.Equal(!throws, read);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("private", failure.Message);
    }

    [Fact]
    public void AnOpenOriginalCanSelectAnotherPlanIndexWithTheSameVerifiedSelector()
    {
        AudioTrack[] catalog = [Catalog("en"), Catalog("nl")];
        MountedPlaybackAudioTrack[] mounted = [Mounted(4, "nl"), Mounted(1)];
        var selected = -1;
        Assert.Equal(1, PlaybackOriginalAudioSelection.Select(0, catalog, mounted, true, i => selected = i, () => selected));
        Assert.Equal(0, PlaybackOriginalAudioSelection.Select(1, catalog, mounted, true, i => selected = i, () => selected));
    }
}
