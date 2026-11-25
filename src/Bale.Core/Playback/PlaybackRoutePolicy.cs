using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <param name="SoftwareFallback">
/// A hardware codec (H.264/HEVC/VP9) the console's decoder can't take, inside a software envelope the attempt claims: FFmpeg
/// decodes it, through the console stage where one applies (docs/software-decode.md phases A and B; then
/// <see cref="ConsoleStage"/> with <see cref="SoftwareVideo"/>). Any other software route of a hardware codec is refused
/// (FfmpegSource), and the failure replan withdraws the codec.
/// </param>
public sealed record PlaybackRouteDecision(bool UseFfmpeg, bool SoftwareVideo, string? VideoCodec, string? AudioCodec, long? AudioOrdinal,
    long? VideoWidth, long? VideoHeight, bool DolbyVisionBaseLayer = false, bool ConsoleToneMap = false,
    bool DolbyVision7BaseLayer = false, bool ConsoleDownscale = false, bool SoftwareFallback = false)
{
    public bool SoftwareAudio => UseFfmpeg && SoftwareDecodePolicy.AudioCodecs.Contains(AudioCodec!);
    public bool RequiresD3D11 => ConsoleToneMap || DolbyVision7BaseLayer || ConsoleDownscale;
    /// <summary>The console's bale_gpu stage runs: the HDR tone map, or the SDR scale-only downscale.</summary>
    public bool ConsoleStage => ConsoleToneMap || ConsoleDownscale;
}

/// <summary>
/// Pure per-mount policy. HLS stays on AdaptiveMediaSource unless it plays through the Rust HLS reader
/// and FFmpeg. Implemented in Rust (native/bale-core/src/route.rs).
/// </summary>
public static class PlaybackRoutePolicy
{
    /// <param name="forceFfmpeg">Debug A/B only: demux every direct mount with FFmpeg, keeping system video decode.</param>
    /// <param name="hlsThroughFfmpeg">HLS plays through the Rust reader's forward-only stream and FFmpeg.</param>
    /// <param name="softwareDecode">
    /// The decode entries the attempt claims now (its start request, as any replan left them); their software entries decide
    /// a hardware codec's software fallback against the delivered file in <paramref name="versions"/>.
    /// </param>
    /// <param name="softwareStage">
    /// The wrapper (bale.80+) and bale_gpu run the console stage on FFmpeg software frames too: a software fallback may take
    /// the tone map, and with <paramref name="sdrDownscale"/> the downscale (docs/software-decode.md phase B).
    /// </param>
    public static PlaybackRouteDecision Decide(PlaybackPlan plan, bool isHls, bool ffmpegEnabled = true, bool forceFfmpeg = false,
        bool sdrOutput = false, IReadOnlyList<FileVersion>? versions = null, bool hlsThroughFfmpeg = false,
        IReadOnlyList<VideoDecodeCapability>? hardwareDecode = null, (int Width, int Height)? display = null,
        bool sdrDownscale = false, IReadOnlyList<VideoDecodeCapability>? softwareDecode = null, bool softwareStage = false) =>
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
            // SDR larger than the display takes bale_gpu's scale-only stage (wrapper bale.39+).
            writer.WriteNumber("display_width", display?.Width ?? 0);
            writer.WriteNumber("display_height", display?.Height ?? 0);
            writer.WriteBoolean("sdr_downscale", sdrDownscale);
            // Only software entries: the hardware ones above already decided whether the route is software.
            writer.Records("software_decode", (softwareDecode ?? []).Where(decode => !decode.Hardware).ToArray(),
                BaleCoreJson.Default.VideoDecodeCapability);
            writer.WriteBoolean("software_stage", softwareStage);
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
