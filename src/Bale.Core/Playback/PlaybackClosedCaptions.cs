using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>
/// In-band closed captions in the player: the CEA-608 that bale_gpu's caption decoder finds in the video's A/53 cc_data
/// (docs/closed-captions.md). Decides which mounts get a decoder, which caption channels the subtitle lists offer, what
/// picking one does and what a remount keeps. The player holds one per playback attempt, so a new attempt (Retry, the next
/// episode) starts with CC off, on CC1, from the Detail page's tracks.
/// <list type="bullet">
/// <item>Android TV lists in-band CEA-608 as a track the player found itself, never as a server row
/// (TvSubtitleIdentity.kt tvMountedSubtitleIdentity: <c>SubtitleIdentity.LocalMedia3</c>), after the server's rows
/// (TvSubtitleHudState.kt buildTvSubtitleHudOptions). Media3 exposes one CEA-608 track, which decodes CC1, unless the
/// container declares caption services, and AetherEngine surfaces its synthetic track with the first real caption data.
/// Deliberate deviation (owner, 2026-11-26): Bale offers every channel the stream carries, CC1 to CC4 (CC3 and CC4 are field
/// 2), each once a decoder has seen its caption commands, in channel order. A stream with CC1 alone keeps the one row "CC",
/// the name Android gives CEA-608 (TvSubtitleChoiceLabels.kt subtitleFormatShortName, the HUD's CC chip), so the common case
/// looks as on Android; with more, the rows are "CC1" to "CC4". Android's own row for an undeclared track has no language and
/// falls back to a numbered "Subtitle N".</item>
/// <item>Picking one is the player's own business, with no track_change replan (TvSubtitleTransactionAdapter.kt commits a
/// client-owned identity locally): the mount's decoder decodes that channel from now on. A server subtitle on screen goes
/// first, through the player's Off path, so the server stops its burn-in or sidecar; that Off is a step of the pick, not the
/// viewer's subtitle choice. Picking Off ends CC at once; picking a server subtitle ends it once a plan plays that subtitle,
/// so a refused change leaves CC on.</item>
/// <item>Nothing is remembered: a CC pick writes no subtitle preference, as for any track the playing version does not
/// describe (<see cref="PlayerTrackPreferences.WriteFor"/>). Android TV's HUD stages SeriesSubtitleChoice.of for such a
/// track, which for an undeclared CEA-608 track is only index -1, an empty language and its "Subtitle N" label; Bale
/// leaves the viewer's server subtitle preference as it was (docs/closed-captions.md). Auto never picks CC: the Detail
/// page's Auto ranks the catalog's tracks, which never list in-band captions.</item>
/// <item>A recovery replan, a seek reanchor, an HLS remount or a quality change keeps CC on, on the picked channel:
/// TvPlayerViewModel.kt selectedSubtitleTrackIndex keeps a player-discovered CEA-608 track as keep-current, so a recovery
/// keeps the viewer's subtitles. Every mount's new decoder decodes <see cref="Channel"/>, and every mount re-attaches CC
/// (<see cref="PlanPlays"/>) unless its plan plays a server subtitle.</item>
/// </list>
/// </summary>
public sealed class PlaybackClosedCaptions
{
    /// <summary>
    /// The CC1 row's <see cref="PlaybackSelectorOption.TrackIndex"/> in the player's subtitle lists; CC2 to CC4 take the next
    /// ones down, -3 to -5 (<see cref="TrackIndexOf"/>). No server track has them (-1 is Off, 0 and up are combined subtitle
    /// indexes), and nothing sends them to the server.
    /// </summary>
    public const int TrackIndex = -2;

    /// <summary>The CC1 row's title while it is the only CC row, and the Subtitles pill's value while it is on.</summary>
    public const string Title = "CC";

    /// <summary>CEA-608's caption channels, bale_cc_select's 1 to 4: CC1 and CC2 in field 1, CC3 and CC4 in field 2.</summary>
    public const uint Channels = 4;

    /// <summary>The bale_cc_seen bits of CC1 to CC4 (bit n-1: CCn carried caption commands, not just padding or Text).</summary>
    public const uint SeenChannels = 0xF;

    /// <summary>
    /// The channel the attempt's decoders decode: CC1 (Media3's one undeclared CEA-608 track) until the viewer picks another.
    /// Kept for the attempt, with CC off too, so a remount's new decoder decodes what the viewer picked last.
    /// </summary>
    public uint Channel { get; private set; } = 1;

    /// <summary>
    /// The channels (bale_cc_seen bits within <see cref="SeenChannels"/>) a mount of this attempt found. Kept for the attempt:
    /// a remount's new decoder starts unseen.
    /// </summary>
    public uint Found { get; private set; }

    /// <summary>A mount of this attempt found captions on some channel.</summary>
    public bool Discovered => Found != 0;

    /// <summary>Every channel was found: nothing is left to look for.</summary>
    public bool AllFound => Found == SeenChannels;

    /// <summary>The viewer picked a CC row and has not picked another subtitle since.</summary>
    public bool Selected { get; private set; }

    /// <summary>The subtitle lists show CC rows: once captions were found, and while CC is on.</summary>
    public bool Offered => Discovered || Selected;

    /// <summary>The channels with a row: the ones found, and the picked one while it is on.</summary>
    private uint Listed => Found | (Selected ? SeenBit(Channel) : 0);

    /// <summary>Whether <paramref name="channel"/> is one of CC1 to CC4.</summary>
    public static bool IsChannel(uint channel) => channel is >= 1 and <= Channels;

    /// <summary>The row of CC<paramref name="channel"/>: <see cref="TrackIndex"/> for CC1, then one down per channel.</summary>
    public static int TrackIndexOf(uint channel) => IsChannel(channel)
        ? TrackIndex - (int)(channel - 1)
        : throw new ArgumentOutOfRangeException(nameof(channel), channel, "CEA-608 has the channels CC1 to CC4.");

    /// <summary>The channel of a CC row's track index; null for Off, a server track or anything else.</summary>
    public static uint? ChannelOf(int trackIndex) =>
        trackIndex is <= TrackIndex and > TrackIndex - (int)Channels ? (uint)(TrackIndex - trackIndex) + 1 : null;

    /// <summary>The channels of bale_cc_seen bits in words, "cc1,cc3", for logs and the debug readout; empty for none.</summary>
    public static string Names(uint seen) => string.Join(',',
        Enumerable.Range(1, (int)Channels).Where(channel => (seen & SeenBit((uint)channel)) != 0).Select(channel => $"cc{channel}"));

    private static uint SeenBit(uint channel) => 1u << (int)(channel - 1);

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

    /// <summary>Whether the lists offer CC<paramref name="channel"/>: a mount of this attempt found it.</summary>
    public bool Offers(uint channel) => IsChannel(channel) && (Found & SeenBit(channel)) != 0;

    /// <summary>
    /// The title of CC<paramref name="channel"/>'s row: "CC" while CC1 is the only row, so a stream with CC1 alone looks as
    /// Android TV's one track does, else "CC1" to "CC4".
    /// </summary>
    public string TitleOf(uint channel) => channel == 1 && Listed == SeenBit(1) ? Title : $"CC{channel}";

    /// <summary>
    /// A mount's decoder reports its bale_cc_seen bits. Returns the channels found for the first time (0 when none): each has a
    /// row from now on. Padding, Text and CEA-708 data alone offer nothing.
    /// </summary>
    public uint Observe(uint seen)
    {
        var found = seen & SeenChannels & ~Found;
        Found |= found;
        return found;
    }

    /// <summary>
    /// <paramref name="options"/> (the player's subtitle list: Off and the catalog's tracks) with a CC row per channel found
    /// last, in channel order, once <see cref="Offered"/>. While CC is on, the picked channel's row is the checked one and no
    /// other is.
    /// </summary>
    public IReadOnlyList<PlaybackSelectorOption> WithEntries(IReadOnlyList<PlaybackSelectorOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Offered) return options;
        var rows = Selected ? options.Select(option => option.IsSelected ? option with { IsSelected = false } : option) : options;
        var entries = Enumerable.Range(1, (int)Channels).Select(channel => (uint)channel)
            .Where(channel => (Listed & SeenBit(channel)) != 0)
            .Select(channel => new PlaybackSelectorOption(TitleOf(channel), string.Empty, Selected && channel == Channel,
                TrackIndex: TrackIndexOf(channel)));
        return [.. rows, .. entries];
    }

    /// <summary>
    /// The viewer picks CC<paramref name="channel"/>, which is on from now on: the mount's decoder and every later mount's
    /// decode that channel (<see cref="Channel"/>). True when <paramref name="plan"/> plays a server subtitle that must go Off
    /// first (the player's Off path, which remembers nothing); the plan that Off brings then attaches CC, and
    /// <see cref="PlanPlays"/> afterwards says whether it took.
    /// </summary>
    public bool Pick(uint channel, PlaybackPlan? plan)
    {
        if (!IsChannel(channel)) throw new ArgumentOutOfRangeException(nameof(channel), channel, "CEA-608 has the channels CC1 to CC4.");
        Channel = channel;
        Selected = true;
        return ServerSubtitleShows(plan);
    }

    /// <summary>
    /// The viewer picks Off (-1) or a server subtitle (its combined index). Off ends CC at once: true, detach it now. A server
    /// subtitle ends CC only once a plan plays it (<see cref="PlanPlays"/>), so a change the server refuses leaves CC on, as
    /// Android rolls a failed pick back to the committed track. Neither changes <see cref="Channel"/>.
    /// </summary>
    public bool PickTrack(int index)
    {
        if (ChannelOf(index) is not null) throw new ArgumentOutOfRangeException(nameof(index), "A CC row is picked with Pick.");
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
