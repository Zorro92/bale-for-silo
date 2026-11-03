using System.Net;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Core.Connection;
using Bale.Core.Detail;

namespace Bale.Core.Tests;

/// <summary>TvPlaybackFormattingTest.kt / TvTrackSelectionPersistenceTest.kt / TvAnchoredSelectorMenuStateTest.kt.</summary>
public class PlaybackSelectorTests
{
    [Fact]
    public void SingleVersionDoesNotOpenButSingleAudioAndNoSubtitleTracksDo()
    {
        var version = Version();
        version.AudioTracks = [version.AudioTracks![0]];
        var detail = Detail("movie") with { Versions = [version] };
        Assert.False(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Version, detail, PlaybackSelection.Auto));
        Assert.True(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Audio, detail, PlaybackSelection.Auto));
        Assert.True(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Subtitles, detail, PlaybackSelection.Auto));
        var noTracks = detail with { Versions = [new FileVersion { FileId = "empty" }] };
        Assert.False(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Audio, noTracks, PlaybackSelection.Auto));
        Assert.True(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Subtitles, noTracks, PlaybackSelection.Auto));
        Assert.False(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Version, null, PlaybackSelection.Auto));
        Assert.False(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Subtitles, null, PlaybackSelection.Auto));
        Assert.True(PlaybackFormatting.SelectorIsInteractive(PlaybackSelectorKind.Version,
            detail with { Versions = [Version(), new FileVersion { FileId = "other" }] }, PlaybackSelection.Auto));
    }

    [Fact]
    public void AutoIsTheOnlyCheckedAudioOptionUntilAManualOrdinalIsPicked()
    {
        var detail = Detail("movie") with { Versions = [Version()] };
        var options = PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Audio, detail, PlaybackSelection.Auto);
        Assert.Equal("Auto", Assert.Single(options, option => option.IsSelected).Title);
        Assert.Equal("Use your Playback audio preference", options[0].Detail);
        Assert.Equal("English", options[1].Title);
        Assert.Equal("Main · AAC · Stereo · Default", options[1].Detail);
        Assert.Equal("Director Commentary · AC3 · 5.1 · Preferred", options[2].Detail);
        Assert.Equal(1, Assert.Single(PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Audio, detail,
            new PlaybackSelection(AudioIndex: 1)), option => option.IsSelected).TrackIndex);
    }

    [Fact]
    public void VersionMenuIncludesAutoThenCatalogOrderWithFullLabelsAndDecimalSizes()
    {
        var version = Version();
        version.Resolution = "2160p";
        version.CodecVideo = "hevc";
        version.Hdr = true;
        version.Container = "mkv";
        version.FileSize = 8_589_934_592;
        var options = PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Version,
            Detail("movie") with { Versions = [version] }, new PlaybackSelection("file"));
        Assert.Equal("Best match for this device", options[0].Detail);
        Assert.False(options[0].IsSelected);
        Assert.Equal("4K · HEVC · HDR · AC3", options[1].Title);
        Assert.Equal("HEVC · MKV · 8.59 GB", options[1].Detail);
        Assert.True(options[1].IsSelected);
    }

    [Fact]
    public void SortedSubtitleGroupsKeepCombinedIdentityNotCatalogOrStreamIndex()
    {
        var version = Version();
        version.SubtitleTracks =
        [
            new() { Index = 10, Language = "eng", Codec = "pgs", Title = "SDH" },
            new() { Index = 0, Language = "fre", Codec = "srt", External = true },
            new() { Index = 12, Language = "en", Codec = "srt", Title = "forced", Forced = true },
            new() { Index = 13, Language = "en", Codec = "srt" },
            new() { Index = 14, Codec = "srt" },
        ];
        var detail = Detail("episode") with { Versions = [version], EffectiveSubtitleLanguage = "fr" };
        var options = PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Subtitles, detail, new PlaybackSelection(SubtitleIndex: 0));
        Assert.Equal(new[] { "Auto", "Off", "French", "English", "English", "English", "Track 5" }, options.Select(option => option.Title));
        Assert.Equal(new int?[] { null, -1, 0, 3, 2, 1, 4 }, options.Select(option => option.TrackIndex));
        Assert.Equal("SRT · External", options[2].Detail);
        Assert.Equal("SRT · Forced", options[4].Detail);
        Assert.Equal("PGS · SDH", options[5].Detail);
        Assert.Same(options[2], Assert.Single(options, option => option.IsSelected));
    }

    [Fact]
    public void SubtitleSortingUsesCatalogForcedFlagButDisplayAlsoRecognizesForcedTitle()
    {
        var version = Version();
        version.SubtitleTracks =
        [
            new() { Language = "eng", Codec = "srt", Title = "forced" },
            new() { Language = "eng", Codec = "srt" },
            new() { Language = "eng", Codec = "srt", Forced = true },
        ];
        var options = PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Subtitles,
            Detail("movie") with { Versions = [version] }, PlaybackSelection.Auto);
        Assert.Equal(new int?[] { null, -1, 0, 1, 2 }, options.Select(option => option.TrackIndex));
        Assert.Equal("SRT · Forced", options[2].Detail);
    }

    [Fact]
    public void VersionChangeResetsBothTracksEvenWhenChoosingAuto()
    {
        var state = new PlaybackSelectionState();
        var detail = Detail("movie") with { Versions = [Version(), new FileVersion { FileId = "other" }] };
        state.Select(detail, PlaybackSelectorKind.Audio, new("Japanese", "", true, TrackIndex: 1));
        state.Select(detail, PlaybackSelectorKind.Subtitles, new("Off", "", true, TrackIndex: -1));
        Assert.Equal(new PlaybackSelection(AudioIndex: 1, SubtitleIndex: -1), state.For(detail));
        Assert.Equal(new PlaybackSelection("other"), state.Select(detail, PlaybackSelectorKind.Version, new("Other", "", true, FileId: "other")));
        Assert.Equal(PlaybackSelection.Auto, state.Select(detail, PlaybackSelectorKind.Version, new("Auto", "", true)));
    }

    [Fact]
    public void NextUpSelectionsAreKeyedToEpisodeAndDoNotLeakAcrossFocusedTargets()
    {
        var state = new PlaybackSelectionState();
        var first = Detail("episode-a") with { Versions = [Version()] };
        var second = first with { ContentId = "episode-b" };
        state.Select(first, PlaybackSelectorKind.Audio, new("Japanese", "", true, TrackIndex: 1));
        state.Select(first, PlaybackSelectorKind.Subtitles, new("Off", "", true, TrackIndex: -1));
        Assert.Equal(PlaybackSelection.Auto, state.For(second));
        Assert.Equal(new PlaybackSelection(AudioIndex: 1, SubtitleIndex: -1), state.For(first));
        Assert.Equal(PlaybackSelection.Auto, state.For(null));
    }

    [Fact]
    public void DeletedVersionOrChangedAutomaticFileCannotRetainFileSpecificIndexes()
    {
        var state = new PlaybackSelectionState();
        var detail = Detail("movie") with { Versions = [Version()] };
        state.Select(detail, PlaybackSelectorKind.Audio, new("Japanese", "", true, TrackIndex: 1));
        var replacement = new FileVersion { FileId = "replacement", AudioTracks = Version().AudioTracks };
        Assert.Equal(PlaybackSelection.Auto, state.For(detail with { Versions = [replacement] }));
        state.Select(detail, PlaybackSelectorKind.Version, new("File", "", true, FileId: "file"));
        Assert.Equal(PlaybackSelection.Auto, state.For(detail with { Versions = [replacement] }));
    }

    [Fact]
    public void ChoosingTracksFeedsTheExistingHeroReadout()
    {
        var state = new PlaybackSelectionState();
        var detail = Detail("movie") with { Versions = [Version()] };
        state.Select(detail, PlaybackSelectorKind.Audio, new("Japanese", "", true, TrackIndex: 1));
        var selected = state.Select(detail, PlaybackSelectorKind.Subtitles, new("Off", "", true, TrackIndex: -1));
        var summary = new PlaybackSummaryState().Update(detail, selectedFileId: selected.FileId,
            selectedAudioTrackIndex: selected.AudioIndex, selectedSubtitleTrackIndex: selected.SubtitleIndex);
        Assert.Equal("Japanese · AC3 · 5.1", summary.Audio);
        Assert.Equal("Off", summary.Subtitles);
    }

    [Fact]
    public void PageAutoResetOutlivesItsWriteUntilTheSameItemsEffectiveAudioChanges()
    {
        // TvTrackSelectionPersistenceTest.kt: DELETE can land before loaded detail fields are refreshed.
        var detail = Detail("e1") with { Type = "episode", SeriesId = "show", Versions = [Version()] };
        var state = new PlaybackSelectionState();
        state.Select(detail, PlaybackSelectorKind.Audio, new("Auto", "", true));
        var pending = state.PendingAudioChoiceFor(detail);
        Assert.NotNull(pending);
        Assert.Null(pending.Choice);
        Assert.Null(PlaybackAudioSelection.AutomaticAudioOrdinal(detail.Versions[0], "en", null, pending));
        Assert.Equal("Auto", Assert.Single(PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Audio, detail,
            state.For(detail)), option => option.IsSelected).Title);

        var refreshed = detail with { Versions = [Version()] };
        refreshed.Versions[0].EffectiveAudioTrackIndex = 0;
        Assert.Null(state.PendingAudioChoiceFor(refreshed));
        Assert.Equal(0, PlaybackAudioSelection.AutomaticAudioOrdinal(refreshed.Versions[0], "ja", null,
            state.PendingAudioChoiceFor(refreshed)));
    }

    [Fact]
    public void PendingSeriesPickFollowsSignaturesAcrossEpisodesAndVersionsWithoutChangingMenuChecks()
    {
        var state = new PlaybackSelectionState();
        var first = Detail("e1") with { Type = "episode", SeriesId = "show", Versions = [Version()] };
        var otherVersion = Version();
        otherVersion.FileId = "other";
        otherVersion.AudioTracks!.Reverse();
        otherVersion.EffectiveAudioTrackIndex = 0;
        var second = first with { ContentId = "e2", Versions = [otherVersion] };
        state.Select(first, PlaybackSelectorKind.Audio, new("Japanese", "", true, TrackIndex: 1));
        var pending = state.PendingAudioChoiceFor(second);
        Assert.NotNull(pending);
        Assert.Equal(0, PlaybackAudioSelection.AutomaticAudioOrdinal(otherVersion, "en", null, pending));
        Assert.Equal(PlaybackSelection.Auto, state.For(second));
        Assert.Equal("Auto", Assert.Single(PlaybackFormatting.SelectorOptions(PlaybackSelectorKind.Audio, second,
            state.For(second)), option => option.IsSelected).Title);
        Assert.Null(state.PendingAudioChoiceFor(second with { SeriesId = "another-show" }));
        Assert.Null(state.PendingAudioChoiceFor(null));
        Assert.NotNull(state.PendingAudioChoiceFor(second));
    }

    [Fact]
    public void VersionAndEditionChangesKeepTheSeriesOverlayButClearFileSpecificChoices()
    {
        var state = new PlaybackSelectionState();
        var first = Version();
        var other = Version();
        other.FileId = "other";
        other.AudioTracks!.Reverse();
        var detail = Detail("movie") with { Versions = [first, other] };
        state.Select(detail, PlaybackSelectorKind.Audio, new("Japanese", "", true, TrackIndex: 1));
        var pending = state.PendingAudioChoiceFor(detail);
        var selection = state.Select(detail, PlaybackSelectorKind.Edition, new("Other", "", true, FileId: "other"));
        Assert.Equal(new PlaybackSelection("other"), selection);
        Assert.Same(pending, state.PendingAudioChoiceFor(detail));
        Assert.Equal(0, PlaybackAudioSelection.AutomaticAudioOrdinal(other, "en", null, pending));
        state.Select(detail, PlaybackSelectorKind.Audio, new("Auto", "", true));
        Assert.Null(state.PendingAudioChoiceFor(detail)!.Choice);
        state.Select(detail, PlaybackSelectorKind.Version, new("Auto", "", true));
        Assert.Null(state.PendingAudioChoiceFor(detail)!.Choice);
    }

    [Fact]
    public void MenuFocusStartsOnCheckedRowStopsAtEdgesAndRevealsBelowFoldRows()
    {
        PlaybackSelectorOption[] options = [new("Auto", "", false), new("Off", "", true)];
        Assert.Equal(1, PlaybackSelectorNavigation.InitialIndex(options));
        Assert.Equal(-1, PlaybackSelectorNavigation.InitialIndex([]));
        Assert.Null(PlaybackSelectorNavigation.NextIndex(2, 0, false));
        Assert.Null(PlaybackSelectorNavigation.NextIndex(2, 1, true));
        Assert.Equal(1, PlaybackSelectorNavigation.NextIndex(2, 0, true));
        Assert.Equal(190, PlaybackSelectorNavigation.ScrollTarget(0, 400, 60, 270, 500));
        Assert.Equal(80, PlaybackSelectorNavigation.ScrollTarget(190, 80, 60, 270, 500));
        Assert.Equal(80, PlaybackSelectorNavigation.ScrollTarget(80, 100, 60, 270, 500));
    }

    [Fact]
    public async Task NextUpPicksWriteSeriesSignaturesAndAutoDeletesWithoutStartingPlayback()
    {
        var handler = new StubHandler().Enqueue("{}").Enqueue("{}").Enqueue(HttpStatusCode.NoContent, "").Enqueue(HttpStatusCode.NoContent, "");
        var address = new Uri("https://silo.example.com");
        var connection = new SiloConnection(address, new ClientIdentity("d", "x", "xbox", "Bale"), transport: handler);
        var feed = new DetailFeed(connection.Api, new BrowseFeed(connection.Api, address));
        var detail = Detail("episode-42") with { Type = "episode", SeriesId = "show" };
        var version = Version();
        version.SubtitleTracks = [new() { Index = 10, Language = "eng", Codec = "srt" },
            new() { Index = 0, Language = "fre", Codec = "srt", Title = "SDH", External = true }];
        await feed.SaveTrackChoiceAsync(detail, version, PlaybackSelectorKind.Audio, 1);
        await feed.SaveTrackChoiceAsync(detail, version, PlaybackSelectorKind.Subtitles, 0);
        await feed.SaveTrackChoiceAsync(detail, version, PlaybackSelectorKind.Audio, null);
        await feed.SaveTrackChoiceAsync(detail, version, PlaybackSelectorKind.Subtitles, null);
        Assert.Equal(new[] { "/api/v2/audio-prefs/show", "/api/v2/subtitle-prefs/show", "/api/v2/audio-prefs/show", "/api/v2/subtitle-prefs/show" },
            handler.Requests.Select(request => request.RequestUri!.AbsolutePath));
        Assert.Equal(new[] { HttpMethod.Put, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Delete }, handler.Requests.Select(request => request.Method));
        using var audio = JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.Equal(1, audio.RootElement.GetProperty("audio_track_index").GetInt32());
        Assert.Equal("jpn", audio.RootElement.GetProperty("track_signature").GetProperty("language").GetString());
        Assert.False(audio.RootElement.GetProperty("track_signature").TryGetProperty("default", out _));
        using var subtitle = JsonDocument.Parse(handler.RequestBodies[1]!);
        Assert.Equal(0, subtitle.RootElement.GetProperty("subtitle_track_index").GetInt32());
        Assert.Equal("external", subtitle.RootElement.GetProperty("track_signature").GetProperty("source").GetString());
        Assert.True(subtitle.RootElement.GetProperty("track_signature").GetProperty("hearing_impaired").GetBoolean());
        Assert.False(subtitle.RootElement.TryGetProperty("show_forced_subtitles", out _));
    }

    [Fact]
    public void SubtitleOffAndMovieOwnerMatchOfficialServerChoice()
    {
        var off = PlaybackTrackChoices.Subtitle(Version(), -1);
        Assert.Equal("", off.SubtitleLanguage);
        Assert.Equal(-1, off.SubtitleTrackIndex);
        Assert.Equal("off", off.SubtitleMode);
        Assert.Null(off.TrackSignature);
        Assert.Null(off.ShowForcedSubtitles);
        Assert.Equal("movie", PlaybackTrackChoices.OwnerId(Detail("movie")));
    }

    [Fact]
    public void AudioChoiceOmitsAnEmptySignatureAndRejectsUnknownServerLanguage()
    {
        var empty = PlaybackTrackChoices.Audio(new FileVersion { AudioTracks = [new() { Channels = 0, Default = true }] }, 0);
        Assert.Equal(0, empty.AudioTrackIndex);
        Assert.Null(empty.AudioLanguage);
        Assert.Null(empty.TrackSignature);
        var unknown = PlaybackTrackChoices.Audio(new FileVersion { AudioTracks = [new() { Language = "und", Title = "Commentary" }] }, 0);
        Assert.Null(unknown.AudioLanguage);
        Assert.Equal("Commentary", unknown.TrackSignature!.Title);
        var legacy = PlaybackTrackChoices.Audio(new FileVersion { AudioTracks = [new() { Language = "English" }] }, 0);
        Assert.Equal("en", legacy.AudioLanguage);
        Assert.Equal("English", legacy.TrackSignature!.Language);
    }

    [Fact]
    public async Task ContainerVersionAndEditionSelectionsNeverWriteServerPreferences()
    {
        var handler = new StubHandler();
        var address = new Uri("https://silo.example.com");
        var connection = new SiloConnection(address, new ClientIdentity("d", "x", "xbox", "Bale"), transport: handler);
        var feed = new DetailFeed(connection.Api, new BrowseFeed(connection.Api, address));
        await feed.SaveTrackChoiceAsync(Detail("show") with { Type = "series" }, Version(), PlaybackSelectorKind.Audio, 1);
        await feed.SaveTrackChoiceAsync(Detail("season") with { Type = "season", SeriesId = "show" }, Version(), PlaybackSelectorKind.Subtitles, -1);
        await feed.SaveTrackChoiceAsync(Detail("movie"), Version(), PlaybackSelectorKind.Version, null);
        await feed.SaveTrackChoiceAsync(Detail("movie"), Version(), PlaybackSelectorKind.Edition, null);
        Assert.Empty(handler.Requests);
    }

    private static FileVersion Version() => new()
    {
        FileId = "file", EffectiveAudioTrackIndex = 1,
        AudioTracks = [new() { Language = "eng", Codec = "aac", Channels = 2, Title = "Main", Default = true },
            new() { Language = "jpn", Codec = "ac3", Layout = "5.1", Title = "Director Commentary" }],
    };

    private static ItemDetail Detail(string id) => new(id, "movie", "Movie", null, null, null, null, null,
        [], null, [], [], null, [], null, id, null, null, new(false, false, false), []);
}
