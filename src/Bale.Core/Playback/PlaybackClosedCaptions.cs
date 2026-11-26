using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>
/// In-band closed captions in the player: the CEA-608 that bale_gpu's caption decoder finds in the video's A/53 cc_data
/// (docs/closed-captions.md). Decides which mounts get a decoder, when the subtitle lists offer CC, what picking it does and
/// what a remount keeps. The player holds one per playback attempt, so a new attempt (Retry, the next episode) starts with
/// CC off, from the Detail page's tracks.
/// <list type="bullet">
/// <item>Android TV lists in-band CEA-608 as a track the player found itself, never as a server row
/// (TvSubtitleIdentity.kt tvMountedSubtitleIdentity: <c>SubtitleIdentity.LocalMedia3</c>), after the server's rows
/// (TvSubtitleHudState.kt buildTvSubtitleHudOptions). Media3 exposes one CEA-608 track, which decodes CC1, unless the
/// container declares caption services; the fork's packet tap carries no such declaration, so Bale offers one entry, on
/// CC1, once the decoder has seen CC1 captions (AetherEngine also surfaces its synthetic track with the first real caption
/// data). It is called "CC": the name Android gives CEA-608 (TvSubtitleChoiceLabels.kt subtitleFormatShortName, the
/// HUD's CC chip); Android's own row for an undeclared track has no language and falls back to a numbered "Subtitle N".</item>
/// <item>Picking it is the player's own business, with no track_change replan (TvSubtitleTransactionAdapter.kt commits a
/// client-owned identity locally). A server subtitle on screen goes first, through the player's Off path, so the server
/// stops its burn-in or sidecar; that Off is a step of the pick, not the viewer's subtitle choice. Picking Off ends CC at
/// once; picking a server subtitle ends it once a plan plays that subtitle, so a refused change leaves CC on.</item>
/// <item>Nothing is remembered: a CC pick writes no subtitle preference, as for any track the playing version does not
/// describe (<see cref="PlayerTrackPreferences.WriteFor"/>). Android TV's HUD stages SeriesSubtitleChoice.of for such a
/// track, which for an undeclared CEA-608 track is only index -1, an empty language and its "Subtitle N" label; Bale
/// leaves the viewer's server subtitle preference as it was (docs/closed-captions.md). Auto never picks CC: the Detail
/// page's Auto ranks the catalog's tracks, which never list in-band captions.</item>
/// <item>A recovery replan, a seek reanchor, an HLS remount or a quality change keeps CC on: TvPlayerViewModel.kt
/// selectedSubtitleTrackIndex keeps a player-discovered CEA-608 track as keep-current, so a recovery keeps the viewer's
/// subtitles. Every mount re-attaches CC (<see cref="PlanPlays"/>) unless its plan plays a server subtitle.</item>
/// </list>
/// </summary>
public sealed class PlaybackClosedCaptions
{
    /// <summary>
    /// The CC row's <see cref="PlaybackSelectorOption.TrackIndex"/> in the player's subtitle lists. No server track has it
    /// (-1 is Off, 0 and up are combined subtitle indexes), and nothing sends it to the server.
    /// </summary>
    public const int TrackIndex = -2;

    /// <summary>The row's title, and the Subtitles pill's value while CC is on.</summary>
    public const string Title = "CC";

    /// <summary>The channel the decoder draws: CC1, as Media3's one undeclared CEA-608 track.</summary>
    public const uint Channel = 1;

    /// <summary>The bale_cc_seen bit of <see cref="Channel"/> (CC1 carried caption commands, not just padding).</summary>
    public const uint SeenChannel = 1 << 0;

    /// <summary>A mount of this attempt found CC1 captions. Kept for the attempt: a remount's new decoder starts unseen.</summary>
    public bool Discovered { get; private set; }

    /// <summary>The viewer picked CC and has not picked another subtitle since.</summary>
    public bool Selected { get; private set; }

    /// <summary>The subtitle lists show the CC row: once captions were found, and while CC is on.</summary>
    public bool Offered => Discovered || Selected;

    /// <summary>
    /// Whether a mount gets a caption decoder and token (FFmpeg option bale_cc_sink): only an FFmpeg source can tap the
    /// video's packets, and the fork's tap reads H.264, HEVC and MPEG-2 (the route's normalized codec names).
    /// </summary>
    public static bool Taps(PlaybackRouteDecision route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.UseFfmpeg && route.VideoCodec is "h264" or "hevc" or "mpeg2video";
    }

    /// <summary>Whether <paramref name="plan"/> plays a server subtitle: its selected_tracks name one (sidecar, burn-in or native).</summary>
    public static bool ServerSubtitleShows(PlaybackPlan? plan) =>
        plan?.SelectedTracks?.Subtitle is { } subtitle && (!string.IsNullOrEmpty(subtitle.Id) || subtitle.Index is >= 0);

    /// <summary>
    /// A mount's decoder reports its bale_cc_seen bits. True the first time CC1 carried captions: the lists offer CC from now on.
    /// Padding, other channels and CEA-708 data alone offer nothing.
    /// </summary>
    public bool Observe(uint seen)
    {
        if (Discovered || (seen & SeenChannel) == 0) return false;
        Discovered = true;
        return true;
    }

    /// <summary>
    /// <paramref name="options"/> (the player's subtitle list: Off and the catalog's tracks) with the CC row last, once
    /// <see cref="Offered"/>. While CC is on, its row is the checked one and no other is.
    /// </summary>
    public IReadOnlyList<PlaybackSelectorOption> WithEntry(IReadOnlyList<PlaybackSelectorOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Offered) return options;
        var rows = Selected ? options.Select(option => option.IsSelected ? option with { IsSelected = false } : option) : options;
        return [.. rows, new PlaybackSelectorOption(Title, string.Empty, Selected, TrackIndex: TrackIndex)];
    }

    /// <summary>
    /// The viewer picks CC, which is on from now on. True when <paramref name="plan"/> plays a server subtitle that must go
    /// Off first (the player's Off path, which remembers nothing); the plan that Off brings then attaches CC, and
    /// <see cref="PlanPlays"/> afterwards says whether it took.
    /// </summary>
    public bool Pick(PlaybackPlan? plan)
    {
        Selected = true;
        return ServerSubtitleShows(plan);
    }

    /// <summary>
    /// The viewer picks Off (-1) or a server subtitle (its combined index). Off ends CC at once: true, detach it now. A server
    /// subtitle ends CC only once a plan plays it (<see cref="PlanPlays"/>), so a change the server refuses leaves CC on, as
    /// Android rolls a failed pick back to the committed track.
    /// </summary>
    public bool PickTrack(int index)
    {
        if (index == TrackIndex) throw new ArgumentOutOfRangeException(nameof(index), "The CC row is picked with Pick.");
        if (index >= 0 || !Selected) return false;
        Selected = false;
        return true;
    }

    /// <summary>
    /// <paramref name="plan"/> is mounted or adopted in place (a start, a recovery replan, a seek reanchor or HLS remount, a
    /// quality or track change), or the Off path a pick needed has returned. True when CC stays on and that mount draws it; a
    /// plan that plays a server subtitle ends CC, as picking that subtitle does.
    /// </summary>
    public bool PlanPlays(PlaybackPlan? plan)
    {
        if (Selected && ServerSubtitleShows(plan)) Selected = false;
        return Selected;
    }
}
