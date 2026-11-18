using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

/// <summary>
/// DetailVersionSelectionTests.swift (Apple 417508d9) for Bale's combined indexes: every track here is embedded, so a
/// track's combined index is its position (Apple's tests use stream numbers).
/// </summary>
public class SubtitleVersionCarryTests
{
    private static VersionSubtitleTrack Track(string? language, string? title = null, bool forced = false, string codec = "subrip",
        bool external = false) =>
        new() { Language = language, Title = title, Forced = forced ? true : null, Codec = codec, External = external ? true : null };

    private static FileVersion File(string id, params VersionSubtitleTrack[] tracks) => new() { FileId = id, SubtitleTracks = [.. tracks] };

    private static FileVersion Fourteen => File("14",
        Track("en", "SUBRIP"), Track("en", "SDH"), Track("fr", "SUBRIP"), Track("es", "Latin American"),
        Track("de", forced: true), Track("es", "Castilian"));

    private static FileVersion ThirtySix => File("36",
        Track("en", "English", codec: "hdmv_pgs_subtitle"), Track("en", "English (SDH)", codec: "hdmv_pgs_subtitle"),
        Track("zh", codec: "hdmv_pgs_subtitle"), Track("de", codec: "hdmv_pgs_subtitle"),
        Track("fr", "French (Canadian)", codec: "hdmv_pgs_subtitle"), Track("es", "Spanish (Castilian)", codec: "hdmv_pgs_subtitle"),
        Track("es", "Spanish (Latin American)", codec: "hdmv_pgs_subtitle"));

    private static int? Moved(int? index, FileVersion from, FileVersion to) =>
        PlaybackFormatting.SubtitleIndexAfterVersionPick(index, from, to);

    [Fact]
    public void ASubtitlePickFollowsItsTrackToAnotherVersion()
    {
        Assert.Equal(1, Moved(1, ThirtySix, Fourteen)); // English SDH stays English SDH, not French
        Assert.Equal(4, Moved(2, Fourteen, ThirtySix)); // French, not English SDH
        Assert.Equal(6, Moved(3, Fourteen, ThirtySix)); // the Spanish track with the same title
        Assert.Equal(3, Moved(6, ThirtySix, Fourteen)); // and back, not the first Spanish track
        Assert.Equal(5, Moved(5, ThirtySix, Fourteen));
        Assert.Equal(0, Moved(0, ThirtySix, Fourteen)); // plain English, not the SDH track
        Assert.Null(Moved(4, Fourteen, ThirtySix)); // no forced German there: Auto
        Assert.Null(Moved(2, ThirtySix, Fourteen)); // no Chinese there: Auto
        Assert.Equal(-1, Moved(-1, Fourteen, ThirtySix)); // Off
        Assert.Null(Moved(null, Fourteen, ThirtySix)); // Auto
        Assert.Equal(4, Moved(4, Fourteen, Fourteen)); // same file
    }

    [Fact]
    public void UndMatchesNothingAndTheSameRegionalTagWins()
    {
        var first = File("1", Track("en"), Track("und", "Commentary"), Track("zh-Hant"));
        var second = File("2", Track("en", forced: true), Track("en"), Track("und", "Spanish"), Track("zh-Hans"), Track("zh-Hant"));
        Assert.Equal(1, Moved(0, first, second)); // a full English pick stays full, not forced
        Assert.Null(Moved(1, first, second));
        Assert.Equal(4, Moved(2, first, second)); // Traditional Chinese, not the first Chinese track
    }

    [Fact]
    public void AnUnplayableTrackIsNeverTheMatchAndExternalTracksKeepTheirCombinedIndex()
    {
        var first = File("1", Track("en"), Track("fr", external: true));
        // An embedded track with no codec cannot play (Android 461240c6d), so the external English one is chosen.
        var second = File("2", Track("en", codec: ""), Track("en", external: true));
        Assert.Equal(0, Moved(1, first, second));
        // External tracks come first in combined indexes: French external is 0 on the first file.
        var third = File("3", Track("de"), Track("fr", external: true));
        Assert.Equal(0, Moved(0, first, third));
    }

    [Fact]
    public void AVersionPickMovesTheSubtitleAndKeepsTheAudioOrdinal()
    {
        var fourteen = Fourteen;
        var thirtySix = ThirtySix;
        fourteen.AudioTracks = [new() { Language = "en" }, new() { Language = "fr" }];
        thirtySix.AudioTracks = [new() { Language = "en" }, new() { Language = "fr" }];
        var detail = new ItemDetail("movie", "movie", "Movie", null, null, null, null, null, [], null, [], [], null, [], null, "movie",
            null, null, new(false, false, false), []) { Versions = [fourteen, thirtySix], LastFileId = "14" };
        var state = new PlaybackSelectionState();
        state.Select(detail, PlaybackSelectorKind.Audio, new("French", "", true, TrackIndex: 1));
        state.Select(detail, PlaybackSelectorKind.Subtitles, new("French", "", true, TrackIndex: 2));
        Assert.Equal("14", PlaybackFormatting.SelectDisplayVersion(detail, state.For(detail))!.FileId);
        var moved = state.Select(detail, PlaybackSelectorKind.Version, new("Other", "", true, FileId: "36"));
        Assert.Equal(new PlaybackSelection("36", AudioIndex: 1, SubtitleIndex: 4), moved);
        Assert.Equal(moved, state.For(detail));
    }
}
