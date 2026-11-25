using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>The two series/movie track preferences a pick in the player can change.</summary>
public enum PlayerTrackKind
{
    Audio,
    Subtitle,
}

/// <summary>
/// A track the viewer picked in the player (the control bar's Audio or Subtitles menu, or the Options panel): an ordinal
/// into the file's audio_tracks, or a combined external-first subtitle index, -1 for Off, as
/// <see cref="PlaybackReplan.ForTrackChange"/> asks for them. <paramref name="FileId"/> is the file it was picked on.
/// </summary>
public sealed record PlayerTrackPick(PlayerTrackKind Kind, int Index, string FileId)
{
    /// <summary>The track id its replan names (silo-server tracks_v3.go TrackIDV3).</summary>
    public string TrackId => $"file:{FileId}:{(Kind == PlayerTrackKind.Audio ? "audio" : "subtitle")}:{Index}";

    public bool IsOff => Kind == PlayerTrackKind.Subtitle && Index < 0;
}

/// <summary>PUT /api/v2/audio-prefs/{OwnerId} when <paramref name="Audio"/> is set, else /api/v2/subtitle-prefs/{OwnerId}.</summary>
public sealed record PlayerTrackPreferenceWrite(string OwnerId, AudioPreferenceUpdate? Audio = null, SubtitlePreferenceUpdate? Subtitle = null)
{
    public PlayerTrackKind Kind => Audio is null ? PlayerTrackKind.Subtitle : PlayerTrackKind.Audio;
}

/// <summary>A pick a plan now plays and the write that remembers it; no write when nothing describes it.</summary>
public sealed record PlayerTrackPlayed(PlayerTrackKind Kind, PlayerTrackPreferenceWrite? Write);

/// <summary>
/// A track the viewer picks in the player becomes the series' (for an episode) or the movie's track preference once a plan
/// plays it, the same write a Detail pick makes (<see cref="DetailFeed.SaveTrackChoiceAsync"/>), so the next start keeps
/// it. TvPlayerViewModel.kt rememberSeriesAudioChoice / rememberSeriesSubtitleChoice stage the pick and
/// sendCommittedSeriesWrites / confirmDesiredAudio save it once the change commits; tvOS's player and silo-roku 3ae62aa
/// (trackPrefs.bs) do the same. So a change the server refuses, answers with another track, or that fails is never the
/// viewer's choice, and nothing the player or the server chose by itself (the start's tracks, an Auto burn-in, a
/// fallback) comes through here. A pick of what already plays is saved at once. Off is saved as Off (a PUT, never the
/// Auto delete: the player has no Auto row), and a subtitle the server answers by burning it in is saved as that track
/// (Android c89bdf34b: the burn-in stands for the row the viewer picked). A newer pick of a kind replaces the one still
/// waiting; a pick of the other kind waits on. The body comes from the track the plan plays, matched through its
/// inventory entry (an embedded track by stream index, an external one by its place among the externals when language
/// and codec agree; Roku 3ae62aa), never from a list position.
/// </summary>
public sealed class PlayerTrackPreferences
{
    private PlayerTrackPick? _audio;
    private PlayerTrackPick? _subtitle;

    /// <summary>Whether a pick still waits for a plan that plays it.</summary>
    public bool Waiting => _audio is not null || _subtitle is not null;

    /// <summary>The viewer picked a track: it waits for a plan that plays it, replacing a waiting pick of its kind.</summary>
    public void Picked(PlayerTrackPick pick)
    {
        ArgumentNullException.ThrowIfNull(pick);
        if (pick.Kind == PlayerTrackKind.Audio) _audio = pick;
        else _subtitle = pick;
    }

    /// <summary>
    /// The change that asked for <paramref name="pick"/> was refused or failed (TvPlayerViewModel.kt: a pick whose request
    /// fails or rolls back never becomes a preference). A newer pick of its kind and a pick of the other kind wait on.
    /// </summary>
    public void Refused(PlayerTrackPick pick)
    {
        if (ReferenceEquals(_audio, pick)) _audio = null;
        if (ReferenceEquals(_subtitle, pick)) _subtitle = null;
    }

    /// <summary>A new attempt starts from the Detail page's tracks again (clearStagedSeriesWrites): no pick waits.</summary>
    public void Clear()
    {
        _audio = null;
        _subtitle = null;
    }

    /// <summary>
    /// <paramref name="plan"/> now plays (its mount opened, a subtitle answer was adopted in place, or the mounted original
    /// switched audio): the waiting picks it plays, each once, with the write that remembers them for
    /// <paramref name="detail"/>'s series or movie. A pick it does not play waits for a later plan: the replans after it
    /// keep asking for the picked track. <paramref name="requestedFileId"/> is the file the start asked for, should the
    /// plan name none.
    /// </summary>
    public IReadOnlyList<PlayerTrackPlayed> Settle(ItemDetail detail, PlaybackPlan plan, string? requestedFileId)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(plan);
        var played = new List<PlayerTrackPlayed>(2);
        foreach (var pick in new[] { _audio, _subtitle })
        {
            if (pick is null || !Plays(pick, plan, requestedFileId)) continue;
            if (pick.Kind == PlayerTrackKind.Audio) _audio = null;
            else _subtitle = null;
            played.Add(new(pick.Kind, WriteFor(detail, plan, pick, requestedFileId)));
        }
        return played;
    }

    /// <summary>
    /// Whether <paramref name="plan"/>'s selected_tracks play <paramref name="pick"/> (trackPrefs.bs plays): that track of
    /// the file it was picked on, by id when the plan names one, else by index; for Off, no subtitle at all.
    /// </summary>
    public static bool Plays(PlayerTrackPick pick, PlaybackPlan plan, string? requestedFileId)
    {
        ArgumentNullException.ThrowIfNull(pick);
        ArgumentNullException.ThrowIfNull(plan);
        if (pick.IsOff) return !Names(plan.SelectedTracks?.Subtitle);
        // The same ordinal of another file is another track.
        if (PlayingFileId(plan, requestedFileId) != pick.FileId) return false;
        var selected = pick.Kind == PlayerTrackKind.Audio ? plan.SelectedTracks?.Audio : plan.SelectedTracks?.Subtitle;
        if (selected is null || !Names(selected)) return false;
        return string.IsNullOrEmpty(selected.Id) ? selected.Index == pick.Index : selected.Id == pick.TrackId;
    }

    /// <summary>
    /// The write that remembers <paramref name="pick"/>, which <paramref name="plan"/> plays, for
    /// <paramref name="detail"/>'s series or movie: Detail's body for the playing version's track
    /// (<see cref="PlaybackTrackChoices"/>). Null when no series or movie owns the pick (<see cref="OwnerId"/>) or the
    /// version does not describe the track, as for a subtitle downloaded during playback (trackPrefs.bs write).
    /// </summary>
    public static PlayerTrackPreferenceWrite? WriteFor(ItemDetail detail, PlaybackPlan plan, PlayerTrackPick pick, string? requestedFileId)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(pick);
        if (OwnerId(detail) is not { } owner) return null;
        if (pick.IsOff) return new(owner, Subtitle: PlaybackTrackChoices.SubtitleOff());
        var fileId = PlayingFileId(plan, requestedFileId);
        if (detail.Versions.FirstOrDefault(version => version.FileId == fileId) is not { } version) return null;
        if (pick.Kind == PlayerTrackKind.Audio)
        {
            // trackPrefs.bs audioAt: a slot without a track saves nothing.
            return pick.Index >= 0 && pick.Index < (version.AudioTracks?.Count ?? 0)
                ? new(owner, Audio: PlaybackTrackChoices.Audio(version, pick.Index)) : null;
        }

        var tracks = version.SubtitleTracks ?? [];
        var ordinal = PlayingSubtitleOrdinal(plan, tracks);
        return ordinal < 0 ? null : new(owner,
            Subtitle: PlaybackTrackChoices.Subtitle(version, PlaybackFormatting.CombinedSubtitleSelectionIndexes(tracks)[ordinal]));
    }

    /// <summary>
    /// The item a pick is remembered under (TvPlayerViewModel.kt trackChoiceKey): an episode's series, a movie itself, as
    /// a Detail pick is (TvItemDetailViewModel.kt: movies and episodes only). Null for an episode whose series is unknown,
    /// since a key no other episode reads would remember nothing, and for anything else.
    /// </summary>
    public static string? OwnerId(ItemDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (detail.Type == "episode") return string.IsNullOrWhiteSpace(detail.SeriesId) ? null : detail.SeriesId;
        if (detail.Type != "movie") return null;
        var owner = PlaybackTrackChoices.OwnerId(detail);
        return string.IsNullOrWhiteSpace(owner) ? null : owner;
    }

    /// <summary>
    /// The playing version's subtitle track (its place in subtitle_tracks) that the plan's selected subtitle is, matched
    /// by identity through the plan's inventory entry, never by place in the list (trackPrefs.bs watchSubtitle): an
    /// embedded track by its container stream index, an external one by its place among the version's external tracks,
    /// which the server numbers first, when its language and codec agree. -1 for anything else, such as a downloaded
    /// track, which the version does not list.
    /// </summary>
    private static int PlayingSubtitleOrdinal(PlaybackPlan plan, IReadOnlyList<VersionSubtitleTrack> tracks)
    {
        if (InventoryEntry(plan) is not { } entry) return -1;
        var source = entry.Source?.Trim().ToLowerInvariant();
        if (source == "embedded")
        {
            if (entry.StreamIndex is not { } stream) return -1;
            for (var i = 0; i < tracks.Count; i++)
            {
                // catalog/detail.go buildVersionSubtitleTracks omits index 0 (omitempty).
                if (tracks[i].External != true && (tracks[i].Index ?? 0) == stream) return i;
            }

            return -1;
        }

        if (source != "external" || entry.CombinedIndex is not { } position || position < 0) return -1;
        var externals = Enumerable.Range(0, tracks.Count).Where(i => tracks[i].External == true).ToList();
        if (position >= externals.Count) return -1;
        var ordinal = externals[(int)position];
        var track = tracks[ordinal];
        return PlaybackFormatting.SubtitleLanguageKey(track.Language) == PlaybackFormatting.SubtitleLanguageKey(entry.Language)
            && string.Equals(track.Codec?.Trim(), entry.Codec?.Trim(), StringComparison.OrdinalIgnoreCase) ? ordinal : -1;
    }

    /// <summary>The plan's inventory row for its selected subtitle: by track id when both name one, else by combined index.</summary>
    private static SubtitleInventoryItemV3? InventoryEntry(PlaybackPlan plan)
    {
        if (plan.SelectedTracks?.Subtitle is not { } selected || plan.Subtitle?.Inventory is not { } inventory) return null;
        foreach (var entry in inventory)
        {
            if (entry is null) continue;
            if (!string.IsNullOrEmpty(selected.Id) && !string.IsNullOrEmpty(entry.TrackId))
            {
                if (selected.Id == entry.TrackId) return entry;
            }
            else if (selected.Index is { } index && entry.CombinedIndex == index) return entry;
        }

        return null;
    }

    /// <summary>Whether a selected_tracks member names a track (the server leaves the member out for none).</summary>
    private static bool Names(TrackIdentityV3? track) => track is not null && (!string.IsNullOrEmpty(track.Id) || track.Index is >= 0);

    /// <summary>The file the plan plays: its source's, else its effective file, else the one the start asked for.</summary>
    private static string? PlayingFileId(PlaybackPlan plan, string? requestedFileId) =>
        NonBlank(plan.Source?.MediaFileId) ?? NonBlank(plan.EffectiveMediaFileId) ?? NonBlank(requestedFileId);

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// The track preferences players saved for one series or movie, handed to its Detail page underneath (silo-roku 3ae62aa
/// nav.notePlayed tracksSaved), which puts its own picks of those kinds back on Auto and reads its choices again.
/// </summary>
public sealed record PlayerTrackChoiceReturn(string OwnerId, bool Audio, bool Subtitle)
{
    /// <summary><paramref name="pending"/> with <paramref name="write"/> added; a write for another item starts over.</summary>
    public static PlayerTrackChoiceReturn With(PlayerTrackChoiceReturn? pending, PlayerTrackPreferenceWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var audio = write.Kind == PlayerTrackKind.Audio;
        return pending is not null && pending.OwnerId == write.OwnerId
            ? pending with { Audio = pending.Audio || audio, Subtitle = pending.Subtitle || !audio }
            : new(write.OwnerId, audio, !audio);
    }
}
