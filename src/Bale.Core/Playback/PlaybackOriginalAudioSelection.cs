using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>One mounted audio track, in the player's AudioTracks order, not catalog order.</summary>
public sealed record MountedPlaybackAudioTrack(int? StreamIndex, string? Language, string? Codec,
    long? Channels, string? EmbeddedTitle, bool Decodable);

/// <summary>A fixed, safe diagnostic; never carries a native exception's media URL or headers.</summary>
public sealed class PlaybackAudioSelectionException(bool decoderUnsupported = false)
    : Exception("The requested source audio track could not be mounted and selected.")
{
    // silo-android common/player/video/AudioReconcile.kt uses this typed failure for original files.
    public const string Classification = "audio_track_selection_failed";
    public bool DecoderUnsupported { get; } = decoderUnsupported;

    public PlaybackFailureV3 Failure => new(Classification, Message) { AudioDecoderUnsupported = DecoderUnsupported };
}

/// <summary>
/// AudioReconcile.kt's catalog-to-mounted reconciliation and confirmation for untouched originals.
/// The same selector can be used at opening or by a future in-player audio picker.
/// </summary>
public static class PlaybackOriginalAudioSelection
{
    public static int Select(long? planIndex, IReadOnlyList<AudioTrack>? catalog,
        IReadOnlyList<MountedPlaybackAudioTrack> mounted, bool ffmpeg, Action<int> select, Func<int> selectedIndex)
    {
        try
        {
            var index = ResolveIndex(planIndex, catalog, mounted, ffmpeg);
            if (index < 0) return index; // A proven video-only file.
            select(index);
            if (selectedIndex() != index) throw new PlaybackAudioSelectionException();
            return index;
        }
        catch (PlaybackAudioSelectionException) { throw; }
        catch (Exception) { throw new PlaybackAudioSelectionException(); }
    }

    public static int ResolveIndex(long? planIndex, IReadOnlyList<AudioTrack>? catalog,
        IReadOnlyList<MountedPlaybackAudioTrack> mounted, bool ffmpeg)
    {
        if (planIndex is null && mounted.Count == 0 && catalog is null or { Count: 0 }) return -1;
        if (catalog is null) throw new PlaybackAudioSelectionException();
        if (planIndex is not >= 0 || planIndex >= catalog.Count) throw new PlaybackAudioSelectionException();
        var ordinal = (int)planIndex.Value;
        int index;
        if (ffmpeg)
        {
            // FFmpeg may drop streams with no decoder. Never sort the surviving streams into
            // shifted catalog ordinals: this positional proof requires the complete inventory.
            if (catalog.Count != mounted.Count) throw new PlaybackAudioSelectionException();
            // FFmpegInteropX inserts av_find_best_stream's audio at position zero. Silo's scanner
            // appends only audio streams in demux order; its audio rows have no global stream id.
            // Reconstruct that order from AudioStreams[].StreamIndex, including non-audio gaps.
            var streams = mounted.Select(track => track.StreamIndex ?? -1).ToArray();
            try { index = PlaybackRoutePolicy.AudioTrackIndex(planIndex, streams, catalog.Count); }
            catch (InvalidDataException) { throw new PlaybackAudioSelectionException(); }
            var order = streams.Select((stream, i) => (stream, i)).OrderBy(pair => pair.stream).ToArray();
            for (var i = 0; i < catalog.Count; i++)
                if (!Compatible(catalog[i], mounted[order[i].i])) throw new PlaybackAudioSelectionException();
        }
        else
        {
            // Windows AudioTrack.Id is opaque; neither it nor list position is a source stream
            // index contract. Require a unique identity in BOTH inventories, rather than guessing
            // the language of indistinguishable main/commentary tracks from their positions.
            var candidates = Enumerable.Range(0, mounted.Count).Where(i => Compatible(catalog[ordinal], mounted[i])).ToArray();
            if (candidates.Length != 1) throw new PlaybackAudioSelectionException();
            index = candidates[0];
            if (catalog.Count(track => Compatible(track, mounted[index])) != 1) throw new PlaybackAudioSelectionException();
        }
        // Unsupported tracks still occupy their inventory positions; don't filter before mapping.
        if (!mounted[index].Decodable) throw new PlaybackAudioSelectionException(decoderUnsupported: true);
        return index;
    }

    private static bool Compatible(AudioTrack catalog, MountedPlaybackAudioTrack mounted) =>
        Agrees(Language(catalog.Language), Language(mounted.Language)) &&
        Agrees(Codec(catalog.Codec), Codec(mounted.Codec)) &&
        (catalog.Channels is not > 0 || mounted.Channels is not > 0 || catalog.Channels == mounted.Channels) &&
        Agrees(Clean(catalog.EmbeddedTitle), Clean(mounted.EmbeddedTitle));

    private static bool Agrees(string? a, string? b) => a is null || b is null || a == b;
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? Language(string? value)
    {
        var tag = Clean(value)?.Replace('_', '-');
        if (tag is null) return null;
        var parts = tag.Split('-', 2);
        // FFmpegInteropX's fallback may be a localized display name rather than a language tag.
        if (parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetter)) return null;
        var primary = PlaybackFormatting.SubtitleLanguageKey(parts[0]);
        return primary is null ? null : primary + (parts.Length == 2 ? "-" + parts[1] : "");
    }

    private static string? Codec(string? value) => Clean(value) switch
    {
        null or "unknown" => null,
        "mpeg4audio" or "mp4a" or "he-aac" or "aac_adts" => "aac",
        "ac-3" or "dolby_ac3" => "ac3",
        "dolby_ddplus" or "dd+" => "eac3",
        // FFmpeg's codec name is dts even when the catalog names the HD profile separately.
        "dts_hd" or "dts-hd" or "dts_hd_ma" or "dts_hd_hra" => "dts",
        "float" => "pcm",
        var codec => SoftwareDecodePolicy.NormalizeCodec(codec),
    };
}
