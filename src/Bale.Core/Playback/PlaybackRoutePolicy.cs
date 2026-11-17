using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Playback;

public sealed record PlaybackRouteDecision(bool UseFfmpeg, bool SoftwareVideo, string? VideoCodec, string? AudioCodec, long? AudioOrdinal,
    long? VideoWidth, long? VideoHeight, bool DolbyVisionBaseLayer = false, bool ConsoleToneMap = false,
    bool DolbyVision7BaseLayer = false)
{
    public bool SoftwareAudio => UseFfmpeg && SoftwareDecodePolicy.AudioCodecs.Contains(AudioCodec!);
    public bool RequiresD3D11 => ConsoleToneMap || DolbyVision7BaseLayer;
}

/// <summary>
/// Pure per-mount policy. HLS stays on AdaptiveMediaSource unless it plays through the Rust HLS reader
/// and FFmpeg. Implemented in Rust (native/bale-core/src/route.rs).
/// </summary>
public static class PlaybackRoutePolicy
{
    /// <param name="forceFfmpeg">Debug A/B only: demux every direct mount with FFmpeg, keeping system video decode.</param>
    /// <param name="hlsThroughFfmpeg">HLS plays through the Rust reader's forward-only stream and FFmpeg.</param>
    public static PlaybackRouteDecision Decide(PlaybackPlan plan, bool isHls, bool ffmpegEnabled = true, bool forceFfmpeg = false,
        bool sdrOutput = false, IReadOnlyList<FileVersion>? versions = null, bool hlsThroughFfmpeg = false,
        IReadOnlyList<VideoDecodeCapability>? hardwareDecode = null) =>
        BaleCoreNative.Call("route.decide", writer =>
        {
            writer.Server("plan", plan);
            writer.WriteBoolean("is_hls", isHls);
            writer.WriteBoolean("ffmpeg_enabled", ffmpegEnabled);
            writer.WriteBoolean("force_ffmpeg", forceFfmpeg);
            writer.WriteBoolean("sdr_output", sdrOutput);
            writer.Server("versions", versions);
            writer.WriteBoolean("hls_through_ffmpeg", hlsThroughFfmpeg);
            // The console's hardware entries: a hardware codec they cannot take decodes in FFmpeg.
            writer.Records("hardware_decode", (hardwareDecode ?? []).Where(decode => decode.Hardware).ToArray(),
                BaleCoreJson.Default.VideoDecodeCapability);
        }, BaleCoreJson.Default.PlaybackRouteDecision);

    /// <summary>MKV and WebM (Matroska-based); MP4 stays on Windows' own source.</summary>
    public static bool IsMatroska(string? container) =>
        BaleCoreNative.Call("route.is_matroska", container, BaleCoreJson.Default.String, BaleCoreJson.Default.Boolean);

    /// <summary>FFmpegInteropX moves its best stream first; Silo indexes audio in demux order.</summary>
    public static int AudioTrackIndex(long? selectedOrdinal, IReadOnlyList<int> streamIndices, int? expectedAudioCount = null) =>
        BaleCoreNative.Checked(BaleCoreNative.Call("route.audio_track_index", writer =>
        {
            writer.Number("selected_ordinal", selectedOrdinal);
            writer.Numbers("stream_indices", streamIndices);
            writer.Number("expected_audio_count", expectedAudioCount);
        }, BaleCoreJson.Default.BaleCoreCheckedInt32));
}
