using System.Globalization;
using Bale.Core.Playback;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

internal static class FfmpegSource
{
    internal static MediaSourceConfig Configuration(PlaybackRouteDecision route)
    {
        // Debug captures only our safe Bale diagnostics; production keeps the native logger off.
        FfmpegLogging.Configure();
        if (!route.UseFfmpeg || (route.SoftwareVideo
            ? !SoftwareDecodePolicy.VideoCodecs.Contains(route.VideoCodec!)
            : route.VideoCodec is not null && !ConsoleEvidence.Default.VideoDecode.Any(decode => decode.Hardware && decode.Codec == route.VideoCodec)))
            throw new InvalidDataException("The video codec cannot use the local fallback.");
        var config = new MediaSourceConfig();
        // Never Automatic: FFmpeg D3D11 and non-AV1 software video are forbidden by owner policy.
        config.Video.VideoDecoderMode = route.SoftwareVideo
            ? VideoDecoderMode.ForceFFmpegSoftwareDecoder : VideoDecoderMode.ForceSystemDecoder;
        // These are the only system audio switches in the pinned 2.1.0 winmd. AC3/EAC3/PCM
        // use FFmpeg audio on this path; keep their ordinary direct playback on Media Foundation.
        config.Audio.SystemDecoderAAC = true;
        config.Audio.SystemDecoderMP3 = true;
        config.Subtitles.AutoSelectForcedSubtitles = false;
        config.FFmpegOptions["rw_timeout"] = "10000000";
        ApplyDecodeTuning(config, SoftwareDecodeTuning.ForVideo(route.SoftwareVideo, route.VideoWidth, route.VideoHeight));
        if (route.SoftwareVideo) ApplySoftwareVideo(config);
        return config;
    }

    /// <summary>
    /// Software AV1's one path: zero-copy D3D12 decode, direct write into upload variant 6, GPU grain.
    /// PlayerPage claims no software AV1 when registration fails, so this throw is a backstop; an open
    /// failure on a software route replans to a server transcode.
    /// </summary>
    internal static void ApplySoftwareVideo(MediaSourceConfig config)
    {
        // Patch 0015: bale_gpu.dll needs d3d12.dll registered before the upload device arrives.
        if (!ZeroCopyRuntime.Register()) throw new InvalidDataException("Software AV1 needs zero-copy.");
        config.FFmpegOptions["bale_d3d11_upload"] = SoftwareDecodeTuning.DefaultD3d11Upload.ToString(CultureInfo.InvariantCulture);
        config.FFmpegOptions["bale_film_grain_mode"] = SoftwareDecodeTuning.FilmGrainMode.ToString(CultureInfo.InvariantCulture);
        config.FFmpegOptions["bale_zero_copy"] = "1";
    }

    internal static void ApplyDecodeTuning(MediaSourceConfig config, SoftwareDecodeTuning tuning)
    {
        if (tuning.DecodeAheadFrames is { } ahead) config.FFmpegOptions["bale_decode_ahead_frames"] = ahead.ToString(CultureInfo.InvariantCulture);
        if (tuning.Dav1dMaxFrameDelay is { } delay) config.FFmpegOptions["bale_dav1d_max_frame_delay"] = delay.ToString(CultureInfo.InvariantCulture);
    }

#if DEBUG_SERVER
    internal static MediaSourceConfig Configuration(PlaybackRouteDecision route, DebugFfmpegInputTuning tuning)
    {
        var config = Configuration(route);
        if (tuning.ReadAhead is { } enabled)
        {
            config.General.ReadAheadBufferEnabled = enabled;
            if (enabled)
            {
                config.General.ReadAheadBufferSize = DebugFfmpegInputTuning.ReadAheadCapBytes;
                config.General.ReadAheadBufferDuration = DebugFfmpegInputTuning.ReadAheadCapDuration;
            }
        }
        if (tuning.ReadSize is { } readSize) config.General.FileStreamReadSize = checked((uint)readSize);
        if (route.SoftwareVideo)
        {
            ApplyDecodeTuning(config, new(tuning.DecodeAheadFrames, tuning.Dav1dMaxFrameDelay));
            if (tuning.DecodeThreadPriority is { } priority) config.FFmpegOptions["bale_decode_thread_priority"] = priority.ToString(CultureInfo.InvariantCulture);
            // Patch 0009: dav1d/gpu modes; off retains the independent export-only override.
            if (DebugFfmpegPlaytest.GrainOption(tuning.FilmGrain) is { } grain) config.FFmpegOptions[grain.Key] = grain.Value;
        }
        else
        {
            if (tuning.DecoderUseMaxResolution is { } maxres) config.FFmpegOptions["bale_decoder_use_max_resolution"] = maxres.ToString(CultureInfo.InvariantCulture);
            if (tuning.DecoderMaxDpbCount is { } dpb) config.FFmpegOptions["bale_decoder_max_dpb_count"] = dpb.ToString(CultureInfo.InvariantCulture);
        }
        return config;
    }
#endif

    internal static void ValidateVideoDecoders(FFmpegMediaSource source, PlaybackRouteDecision route)
    {
        // ForceSystemDecoder falls back to FFmpeg for codecs with no compressed provider in 2.1.0.
        // Check every stream before mounting/reading frames, including unexpected extra video tracks.
        // A software route with no video stream also fails, so the open failure replans to a transcode.
        var streams = source.VideoStreams.Select(video => (video.CodecName,
            video.DecoderEngine == DecoderEngine.FFmpegSoftwareDecoder, video.DecoderEngine == DecoderEngine.SystemDecoder)).ToArray();
        if (!SoftwareDecodePolicy.VideoDecodersAllowed(route.SoftwareVideo, streams))
            throw new InvalidDataException("The video decoder violates the local fallback policy.");
    }

    internal static string DecoderName(DecoderEngine engine) => engine switch
    {
        DecoderEngine.SystemDecoder => "system",
        DecoderEngine.FFmpegSoftwareDecoder => "FFmpeg software",
        DecoderEngine.FFmpegD3D11HardwareDecoder => "FFmpeg D3D11",
        _ => "unknown",
    };
}
