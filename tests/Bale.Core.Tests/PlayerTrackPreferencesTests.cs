using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Core.Connection;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>
/// A track picked in the player saved as the series' or movie's track preference once a plan plays it
/// (TvPlayerViewModel.kt rememberSeriesAudioChoice / rememberSeriesSubtitleChoice / sendCommittedSeriesWrites; silo-roku
/// 3ae62aa trackPrefs.spec.bs): what each pick saves, for which item, and when.
/// </summary>
public class PlayerTrackPreferencesTests
{
    private const string FileId = "42";

    [Fact]
    public void AnAudioPickIsSavedForTheSeriesOnlyOnceAPlanPlaysIt()
    {
        var detail = Episode();
        var preferences = new PlayerTrackPreferences();
        preferences.Picked(Audio(1));
        // The plan still playing the old track (the replan in flight, or answered with another track): nothing yet.
        Assert.Empty(preferences.Settle(detail, Plan(audio: 0), FileId));
        Assert.True(preferences.Waiting);

        var played = Assert.Single(preferences.Settle(detail, Plan(audio: 1), FileId));
        Assert.Equal(PlayerTrackKind.Audio, played.Kind);
        var write = played.Write!;
        Assert.Equal("show", write.OwnerId);
        Assert.Equal(PlayerTrackKind.Audio, write.Kind);
        Assert.Null(write.Subtitle);
        // The body a Detail pick of the same track writes (PlaybackTrackChoices.Audio).
        Assert.Equal(1, write.Audio!.AudioTrackIndex);
        Assert.Equal("jpn", write.Audio.AudioLanguage);
        Assert.Equal("ac3", write.Audio.TrackSignature!.Codec);
        Assert.Equal("5.1", write.Audio.TrackSignature.Layout);
        Assert.Equal("Japanese", write.Audio.TrackSignature.Title);
        // Each pick is saved once.
        Assert.False(preferences.Waiting);
        Assert.Empty(preferences.Settle(detail, Plan(audio: 1), FileId));
    }

    [Fact]
    public void AMovieIsItsOwnKeyAndAnEpisodeWithoutItsSeriesRemembersNothing()
    {
        var movie = Episode() with { ContentId = "movie-7", Type = "movie", SeriesId = null };
        Assert.Equal("movie-7", PlayerTrackPreferences.OwnerId(movie));
        Assert.Equal("movie-7", PlayerTrackPreferences.WriteFor(movie, Plan(audio: 1), Audio(1), FileId)!.OwnerId);
        Assert.Equal("show", PlayerTrackPreferences.OwnerId(Episode()));

        // TvPlayerViewModel.kt trackChoiceKey: an episode keyed by itself would save a choice no other episode reads.
        var orphan = Episode() with { SeriesId = " " };
        Assert.Null(PlayerTrackPreferences.OwnerId(orphan));
        var preferences = new PlayerTrackPreferences();
        preferences.Picked(Audio(1));
        var played = Assert.Single(preferences.Settle(orphan, Plan(audio: 1), FileId));
        Assert.Null(played.Write);
        Assert.False(preferences.Waiting);
        // Detail saves movie and episode picks only (TvItemDetailViewModel.kt); so does the player.
        Assert.Null(PlayerTrackPreferences.OwnerId(Episode() with { Type = "video" }));
        Assert.Null(PlayerTrackPreferences.OwnerId(Episode() with { Type = "series" }));
    }

    [Fact]
    public void AnEmbeddedSubtitleIsSavedByItsStreamIndexWithTheBodyADetailPickWrites()
    {
        var detail = Episode();
        var preferences = new PlayerTrackPreferences();
        preferences.Picked(Subtitle(2));
        var write = Assert.Single(preferences.Settle(detail, Plan(subtitle: 2), FileId)).Write!;
        Assert.Equal("show", write.OwnerId);
        Assert.Equal(PlayerTrackKind.Subtitle, write.Kind);
        Assert.Null(write.Audio);
        var subtitle = write.Subtitle!;
        Assert.Equal(2, subtitle.SubtitleTrackIndex);
        Assert.Equal("eng", subtitle.SubtitleLanguage);
        Assert.Equal("always", subtitle.SubtitleMode);
        Assert.Equal("embedded", subtitle.TrackSignature!.Source);
        Assert.Equal("subrip", subtitle.TrackSignature.Codec);
        Assert.Equal("English SDH", subtitle.TrackSignature.Label);
        Assert.True(subtitle.TrackSignature.HearingImpaired);
        Assert.False(subtitle.TrackSignature.Forced);
        // The profile's forced-subtitle toggle is never pinned (SeriesSubtitleChoice.kt).
        Assert.Null(subtitle.ShowForcedSubtitles);
        Assert.Equal(Json(PlaybackTrackChoices.Subtitle(detail.Versions[0], 2)), Json(subtitle));
    }

    [Fact]
    public void AnExternalSubtitleIsSavedByItsPlaceAmongTheExternals()
    {
        var detail = Episode();
        var preferences = new PlayerTrackPreferences();
        preferences.Picked(Subtitle(0));
        var subtitle = Assert.Single(preferences.Settle(detail, Plan(subtitle: 0), FileId)).Write!.Subtitle!;
        Assert.Equal(0, subtitle.SubtitleTrackIndex);
        Assert.Equal("fre", subtitle.SubtitleLanguage);
        Assert.Equal("external", subtitle.TrackSignature!.Source);
        Assert.Equal("subrip", subtitle.TrackSignature.Codec);

        // Its language (or codec) disagrees with the inventory row: the version no longer describes that track.
        var moved = Plan(subtitle: 0);
        moved.Subtitle!.Inventory![0].Language = "de";
        preferences.Picked(Subtitle(0));
        Assert.Null(Assert.Single(preferences.Settle(detail, moved, FileId)).Write);
        var recoded = Plan(subtitle: 0);
        recoded.Subtitle!.Inventory![0].Codec = "ass";
        Assert.Null(PlayerTrackPreferences.WriteFor(detail, recoded, Subtitle(0), FileId));
    }

    [Fact]
    public void TheSavedTrackIsTheOneThePlanPlaysNeverAListPosition()
    {
        // The version lists the SDH track before the PGS one, unlike the server's numbering (stream 3 is combined 1,
        // stream 4 combined 2), so the menu's index 2 asked for what the server numbers 2: the SDH track. That plays,
        // and that is what is saved, under its own place in the version's list (Roku 3ae62aa).
        var version = Version();
        version.SubtitleTracks = [version.SubtitleTracks![1], version.SubtitleTracks[0], version.SubtitleTracks[2]];
        var detail = Episode() with { Versions = [version] };
        var subtitle = PlayerTrackPreferences.WriteFor(detail, Plan(subtitle: 2), Subtitle(2), FileId)!.Subtitle!;
        Assert.Equal("English SDH", subtitle.TrackSignature!.Label);
        Assert.Equal("subrip", subtitle.TrackSignature.Codec);
        Assert.Equal(1, subtitle.SubtitleTrackIndex);

        // A version list that leaves the playing track out describes no track for it: nothing is saved.
        var partial = Version();
        partial.SubtitleTracks = [partial.SubtitleTracks![1], partial.SubtitleTracks[2]];
        Assert.Null(PlayerTrackPreferences.WriteFor(Episode() with { Versions = [partial] }, Plan(subtitle: 1), Subtitle(1), FileId));
        // Nor for a downloaded track, which no version lists.
        Assert.Null(PlayerTrackPreferences.WriteFor(Episode(), Plan(subtitle: 3), Subtitle(3), FileId));
        // Nor for an audio slot the version has no track for.
        Assert.Null(PlayerTrackPreferences.WriteFor(Episode(), Plan(audio: 5), Audio(5), FileId));
    }

    [Fact]
    public void OffIsSavedAsOffOnceThePlanSelectsNoSubtitle()
    {
        var detail = Episode();
        var preferences = new PlayerTrackPreferences();
        preferences.Picked(Subtitle(-1));
        Assert.Empty(preferences.Settle(detail, Plan(subtitle: 2), FileId));
        var off = Assert.Single(preferences.Settle(detail, Plan(subtitle: null), FileId)).Write!.Subtitle!;
        // SeriesSubtitleChoice.Off: a PUT, not the delete Detail's Auto sends (the player has no Auto row).
        Assert.Equal(string.Empty, off.SubtitleLanguage);
        Assert.Equal(-1, off.SubtitleTrackIndex);
        Assert.Equal("off", off.SubtitleMode);
        Assert.Null(off.TrackSignature);
        Assert.Equal(Json(PlaybackTrackChoices.Subtitle(detail.Versions[0], -1)), Json(off));
        // Off names no track of a file: it is Off whichever file the plan plays.
        Assert.True(PlayerTrackPreferences.Plays(Subtitle(-1), Plan(subtitle: null, file: "43"), FileId));
    }

    [Fact]
    public void ASubtitleTheServerBurnsInIsSavedAsThePickedTrack()
    {
        // Android c89bdf34b: the committed burn-in stands for the row the viewer picked.
        var preferences = new PlayerTrackPreferences();
        preferences.Picked(Subtitle(1));
        var subtitle = Assert.Single(preferences.Settle(Episode(), Plan(subtitle: 1, mode: "burn_in"), FileId)).Write!.Subtitle!;
        Assert.Equal(1, subtitle.SubtitleTrackIndex);
        Assert.Equal("embedded", subtitle.TrackSignature!.Source);
        Assert.Equal("hdmv_pgs_subtitle", subtitle.TrackSignature.Codec);
        Assert.Equal("always", subtitle.SubtitleMode);
    }

    [Fact]
    public void NothingThePlayerOrTheServerChoseByItselfIsSaved()
    {
        // The start's tracks, an Auto burn-in, a fallback: no pick waits, so no plan saves anything.
        var preferences = new PlayerTrackPreferences();
        Assert.False(preferences.Waiting);
        Assert.Empty(preferences.Settle(Episode(), Plan(audio: 1, subtitle: 1, mode: "burn_in"), FileId));
        Assert.Empty(preferences.Settle(Episode(), Plan(audio: 0, subtitle: null), FileId));

        // An audio pick waits; the subtitle the plan carries alongside it was not picked and is not saved.
        preferences.Picked(Audio(1));
        var played = Assert.Single(preferences.Settle(Episode(), Plan(audio: 1, subtitle: 2), FileId));
        Assert.Equal(PlayerTrackKind.Audio, played.Kind);
    }

    [Fact]
    public void ARefusedChangeDropsOnlyItsOwnPick()
    {
        var detail = Episode();
        var preferences = new PlayerTrackPreferences();
        var audio = Audio(1);
        var subtitle = Subtitle(2);
        preferences.Picked(audio);
        preferences.Picked(subtitle);
        preferences.Refused(subtitle);
        // Even a later plan that happens to carry that subtitle does not make it the viewer's choice.
        var played = Assert.Single(preferences.Settle(detail, Plan(audio: 1, subtitle: 2), FileId));
        Assert.Equal(PlayerTrackKind.Audio, played.Kind);
        Assert.False(preferences.Waiting);

        // A refusal of a pick a newer one already replaced leaves the newer one waiting.
        var older = Subtitle(0);
        var newer = Subtitle(2);
        preferences.Picked(older);
        preferences.Picked(newer);
        preferences.Refused(older);
        Assert.True(preferences.Waiting);
        // An equal pick is still another pick: only the very pick a change asked for is refused.
        preferences.Refused(Subtitle(2));
        Assert.True(preferences.Waiting);
        Assert.Equal(2, Assert.Single(preferences.Settle(detail, Plan(subtitle: 2), FileId)).Write!.Subtitle!.SubtitleTrackIndex);
    }

    [Fact]
    public void ANewerPickOfAKindReplacesTheOneWaitingAndTheOtherKindWaitsOn()
    {
        var detail = Episode();
        var preferences = new PlayerTrackPreferences();
        preferences.Picked(Subtitle(0));
        preferences.Picked(Audio(1));
        preferences.Picked(Subtitle(2));
        Assert.Empty(preferences.Settle(detail, Plan(audio: 0, subtitle: 0), FileId));
        var played = preferences.Settle(detail, Plan(audio: 0, subtitle: 2), FileId);
        Assert.Equal(PlayerTrackKind.Subtitle, Assert.Single(played).Kind);
        Assert.True(preferences.Waiting);
        Assert.Equal(PlayerTrackKind.Audio, Assert.Single(preferences.Settle(detail, Plan(audio: 1, subtitle: 2), FileId)).Kind);
        Assert.False(preferences.Waiting);

        preferences.Picked(Audio(0));
        preferences.Clear();
        Assert.False(preferences.Waiting);
        Assert.Empty(preferences.Settle(detail, Plan(audio: 0), FileId));
    }

    [Fact]
    public void APlanPlaysAPickByIdOrIndexOnTheFileItWasPickedOn()
    {
        Assert.True(PlayerTrackPreferences.Plays(Audio(1), Plan(audio: 1), FileId));
        Assert.False(PlayerTrackPreferences.Plays(Audio(1), Plan(audio: 0), FileId));
        // The same ordinal of another file is another track.
        Assert.False(PlayerTrackPreferences.Plays(Audio(1), Plan(audio: 1, file: "43"), FileId));
        // A plan that names no id is compared by index; one that names no file plays the file the start asked for.
        var bare = Plan();
        bare.Source = null;
        bare.SelectedTracks = new SelectedTracksV3 { Audio = new TrackIdentityV3 { Index = 1 } };
        Assert.True(PlayerTrackPreferences.Plays(Audio(1), bare, FileId));
        Assert.False(PlayerTrackPreferences.Plays(Audio(1), bare, "43"));
        // The id wins over a disagreeing index.
        var named = Plan(audio: 1);
        named.SelectedTracks!.Audio!.Index = 0;
        Assert.True(PlayerTrackPreferences.Plays(Audio(1), named, FileId));
        // A subtitle the plan does not select is not played, and Off is not played while one is.
        Assert.False(PlayerTrackPreferences.Plays(Subtitle(2), Plan(subtitle: null), FileId));
        Assert.False(PlayerTrackPreferences.Plays(Subtitle(-1), Plan(subtitle: 1), FileId));
        Assert.Equal("file:42:subtitle:2", Subtitle(2).TrackId);
        Assert.Equal("file:42:audio:1", Audio(1).TrackId);
    }

    [Fact]
    public void ALocalAudioSwitchIsAPlanThatPlaysThePick()
    {
        // TvPlayerViewModel.kt confirmDesiredAudio: the original switched on the player, recorded as a committed replan.
        var plan = Plan(audio: 0);
        var start = PlaybackStartBuilder.Build(FileId, "profile", "attempt-id", 0, PlaybackSelection.Auto,
            ConsoleEvidence.Default, PlaybackTestSupport.Identity);
        PlaybackReplan.WithLocalAudio(start, plan, 1);
        var preferences = new PlayerTrackPreferences();
        preferences.Picked(Audio(1));
        Assert.Equal(1, Assert.Single(preferences.Settle(Episode(), plan, FileId)).Write!.Audio!.AudioTrackIndex);
    }

    [Fact]
    public void DetailPutsItsPicksOfTheSavedKindsBackOnAuto()
    {
        var other = Version();
        other.FileId = "43";
        var detail = Episode() with { ContentId = "movie-7", Type = "movie", SeriesId = null, Versions = [Version(), other] };
        var state = new PlaybackSelectionState();
        state.Select(detail, PlaybackSelectorKind.Version, new("Other", "", false, FileId: "43"));
        state.Select(detail, PlaybackSelectorKind.Audio, new("Japanese", "", false, TrackIndex: 1));
        state.Select(detail, PlaybackSelectorKind.Subtitles, new("French", "", false, TrackIndex: 0));
        Assert.Equal(new PlaybackSelection("43", 1, 0), state.For(detail));
        Assert.NotNull(state.PendingAudioChoiceFor(detail));

        // Roku 3ae62aa reseedTracks: the player saved a subtitle; the page's Subtitles pick goes back to Auto.
        state.ForgetTrackPicks(new PlayerTrackChoiceReturn("movie-7", Audio: false, Subtitle: true));
        Assert.Equal(new PlaybackSelection("43", 1, null), state.For(detail));
        Assert.NotNull(state.PendingAudioChoiceFor(detail));
        // ...and audio: the page's pick and its overlay over stale details go, so Auto follows the server again.
        state.ForgetTrackPicks(new PlayerTrackChoiceReturn("movie-7", Audio: true, Subtitle: false));
        Assert.Equal(new PlaybackSelection("43"), state.For(detail));
        Assert.Null(state.PendingAudioChoiceFor(detail));
    }

    [Fact]
    public void TheReturnToDetailGathersOneItemsSavedKinds()
    {
        var audio = new PlayerTrackPreferenceWrite("show", Audio: PlaybackTrackChoices.Audio(Version(), 1));
        var subtitle = new PlayerTrackPreferenceWrite("show", Subtitle: PlaybackTrackChoices.SubtitleOff());
        var first = PlayerTrackChoiceReturn.With(null, audio);
        Assert.Equal(new PlayerTrackChoiceReturn("show", Audio: true, Subtitle: false), first);
        // Up Next keeps the series: the kinds add up.
        Assert.Equal(new PlayerTrackChoiceReturn("show", true, true), PlayerTrackChoiceReturn.With(first, subtitle));
        // Another item starts over.
        Assert.Equal(new PlayerTrackChoiceReturn("movie", false, true),
            PlayerTrackChoiceReturn.With(first, subtitle with { OwnerId = "movie" }));
    }

    [Fact]
    public async Task APlayerWriteIsTheDetailPutAndReadsCanWaitForIt()
    {
        var handler = new GateHandler();
        var address = new Uri("https://silo.example.com");
        var connection = new SiloConnection(address, new ClientIdentity("d", "x", "xbox", "Bale"), transport: handler);
        var feed = new DetailFeed(connection.Api, new BrowseFeed(connection.Api, address));
        // Nothing on its way: no wait.
        await feed.WhenTrackChoicesSavedAsync(TimeSpan.FromSeconds(30)).WaitAsync(TimeSpan.FromSeconds(5));

        var audio = PlayerTrackPreferences.WriteFor(Episode(), Plan(audio: 1), Audio(1), FileId)!;
        var write = feed.SaveTrackPreferenceAsync(audio);
        await handler.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A read after the pick waits for the write in flight (tvOS settlePendingWrites), bounded by its timeout.
        var settled = feed.WhenTrackChoicesSavedAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.False(settled.IsCompleted);
        await feed.WhenTrackChoicesSavedAsync(TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(5));
        handler.Release.SetResult();
        await write.WaitAsync(TimeSpan.FromSeconds(5));
        await settled.WaitAsync(TimeSpan.FromSeconds(5));

        await feed.SaveTrackPreferenceAsync(PlayerTrackPreferences.WriteFor(Episode(), Plan(subtitle: null), Subtitle(-1), FileId)!);
        Assert.Equal(new[] { "/api/v2/audio-prefs/show", "/api/v2/subtitle-prefs/show" }, handler.Paths);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Put, method));
        using var audioBody = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(1, audioBody.RootElement.GetProperty("audio_track_index").GetInt32());
        Assert.Equal("jpn", audioBody.RootElement.GetProperty("audio_language").GetString());
        using var offBody = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("off", offBody.RootElement.GetProperty("subtitle_mode").GetString());
        Assert.Equal(-1, offBody.RootElement.GetProperty("subtitle_track_index").GetInt32());
        Assert.False(offBody.RootElement.TryGetProperty("show_forced_subtitles", out _));
        // A write with no body is a caller bug, never a request.
        await Assert.ThrowsAsync<ArgumentException>(() => feed.SaveTrackPreferenceAsync(new PlayerTrackPreferenceWrite("show")));
        Assert.Equal(2, handler.Paths.Count);
    }

    private static PlayerTrackPick Audio(int index) => new(PlayerTrackKind.Audio, index, FileId);

    private static PlayerTrackPick Subtitle(int index) => new(PlayerTrackKind.Subtitle, index, FileId);

    /// <summary>
    /// The server lists embedded subtitle tracks first, then externals (catalog/detail.go buildVersionSubtitleTracks),
    /// while combined indexes number the externals first: French 0, English PGS 1, English SDH 2.
    /// </summary>
    private static FileVersion Version() => new()
    {
        FileId = FileId,
        AudioTracks =
        [
            new() { Language = "eng", Codec = "aac", Channels = 2, Title = "Main", Default = true },
            new() { Language = "jpn", Codec = "ac3", Layout = "5.1", Title = "Japanese" },
        ],
        SubtitleTracks =
        [
            new() { Index = 3, Language = "eng", Codec = "hdmv_pgs_subtitle", Title = "English" },
            new() { Index = 4, Language = "eng", Codec = "subrip", Title = "English SDH", HearingImpaired = true },
            new() { Index = 5, Language = "fre", Codec = "subrip", External = true, FileName = "fr.srt" },
        ],
    };

    private static ItemDetail Episode() => new("episode-3", "episode", "Episode", null, null, null, null, null,
        [], null, [], [], null, [], "show", "episode-3", null, null, new(false, false, false), [])
    {
        Versions = [Version()],
    };

    /// <summary>A plan for <paramref name="file"/> with the server's inventory for <see cref="Version"/> (plus a downloaded track).</summary>
    private static PlaybackPlan Plan(int? audio = 0, int? subtitle = null, string? mode = null, string file = FileId) => new()
    {
        Source = new PlaybackSource { MediaFileId = file },
        SelectedTracks = new SelectedTracksV3
        {
            Audio = audio is { } a ? new TrackIdentityV3 { Id = $"file:{file}:audio:{a}", Index = a } : null,
            Subtitle = subtitle is { } s ? new TrackIdentityV3 { Id = $"file:{file}:subtitle:{s}", Index = s } : null,
        },
        Subtitle = new SubtitleDecisionV3
        {
            Mode = mode ?? (subtitle is null ? "off" : "render"),
            TrackId = subtitle is { } t ? $"file:{file}:subtitle:{t}" : null,
            Inventory =
            [
                new() { TrackId = $"file:{file}:subtitle:0", CombinedIndex = 0, Source = "external", Language = "fr", Codec = "subrip" },
                new() { TrackId = $"file:{file}:subtitle:1", CombinedIndex = 1, Source = "embedded", StreamIndex = 3, Language = "en", Codec = "hdmv_pgs_subtitle" },
                new() { TrackId = $"file:{file}:subtitle:2", CombinedIndex = 2, Source = "embedded", StreamIndex = 4, Language = "en", Codec = "subrip", HearingImpaired = true },
                new() { TrackId = $"file:{file}:subtitle:3", CombinedIndex = 3, Source = "downloaded", Language = "de", Codec = "subrip", StoredSubtitleId = 9 },
            ],
        },
    };

    private static string Json(Microsoft.Kiota.Abstractions.Serialization.IParsable body)
    {
        using var writer = new Microsoft.Kiota.Serialization.Json.JsonSerializationWriter();
        writer.WriteObjectValue(null, body);
        using var reader = new StreamReader(writer.GetSerializedContent());
        return reader.ReadToEnd();
    }

    /// <summary>Answers 204 to each request; the first waits for <see cref="Release"/>.</summary>
    private sealed class GateHandler : HttpMessageHandler
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Paths { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Methods.Add(request.Method);
            Bodies.Add(request.Content is { } content ? await content.ReadAsStringAsync(cancellationToken) : string.Empty);
            if (Arrived.TrySetResult()) await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent(string.Empty, Encoding.UTF8), RequestMessage = request };
        }
    }
}
