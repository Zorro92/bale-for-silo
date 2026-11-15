using Bale.Core.Api.Models;
using Bale.Core.Native;

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

    /// <summary>
    /// The mounted index to select, or -1 for a proven video-only file. FFmpeg mounts map by stream
    /// order over the complete inventory; Windows mounts need one unique language/codec/channels/title
    /// identity in both inventories. Implemented in Rust (native/bale-core/src/audio_selection.rs).
    /// </summary>
    public static int ResolveIndex(long? planIndex, IReadOnlyList<AudioTrack>? catalog,
        IReadOnlyList<MountedPlaybackAudioTrack> mounted, bool ffmpeg)
    {
        var outcome = BaleCoreNative.Call("audio_selection.resolve_index", writer =>
        {
            writer.Number("plan_index", planIndex);
            writer.Server("catalog", catalog);
            writer.WriteStartArray("mounted");
            foreach (var track in mounted)
            {
                writer.WriteStartObject();
                writer.Number("stream_index", track.StreamIndex);
                writer.Text("language", track.Language);
                writer.Text("codec", track.Codec);
                writer.Number("channels", track.Channels);
                writer.Text("embedded_title", track.EmbeddedTitle);
                writer.WriteBoolean("decodable", track.Decodable);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteBoolean("ffmpeg", ffmpeg);
        }, BaleCoreJson.Default.AudioSelectionOutcome);
        return outcome.Failed is { } failed
            ? throw new PlaybackAudioSelectionException(failed.DecoderUnsupported)
            : outcome.Index!.Value;
    }
}
