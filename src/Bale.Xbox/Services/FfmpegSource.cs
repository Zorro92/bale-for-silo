using System.Globalization;
using Bale.Core.Playback;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

internal static class FfmpegSource
{
    internal static MediaSourceConfig Configuration(PlaybackRouteDecision route)
#if DEBUG_SERVER
        => Configuration(route, new DebugFfmpegInputTuning());

    internal static MediaSourceConfig Configuration(PlaybackRouteDecision route, DebugFfmpegInputTuning tuning)
#endif
    {
        // Debug captures only our safe Bale diagnostics; production keeps the native logger off.
        FfmpegLogging.Configure();
        if (!route.UseFfmpeg || (route.SoftwareVideo
            ? !SoftwareDecodePolicy.VideoCodecs.Contains(route.VideoCodec!)
            : route.VideoCodec is not null && !SoftwareDecodePolicy.SystemVideoCodecs.Contains(route.VideoCodec)))
            throw new InvalidDataException("The video codec cannot use the local fallback.");
        var config = new MediaSourceConfig();
        if (route.ConsoleToneMap && (route.VideoCodec is not ("hevc" or "vp9") || !ConsoleToneMapRuntime.IsAvailable))
            throw new InvalidDataException("Console tone mapping is unavailable for this source.");
        // Automatic selects d3d11va for the local tone-map stage. Every other hardware route
        // retains its system decoder; software AV1 remains dav1d without a tone-map stage.
        config.Video.VideoDecoderMode = route.ConsoleToneMap ? VideoDecoderMode.Automatic : route.SoftwareVideo
            ? VideoDecoderMode.ForceFFmpegSoftwareDecoder : VideoDecoderMode.ForceSystemDecoder;
        if (route.ConsoleToneMap) config.FFmpegOptions["bale_tonemap"] = "1"; // BT.2390, same defaults as the server.
        var softwareVideo = route.SoftwareVideo;
#if DEBUG_SERVER
        // Choose the production mode first; default leaves it intact on every route.
        config.Video.VideoDecoderMode = DecoderMode(config.Video.VideoDecoderMode, tuning);
        softwareVideo = tuning.SoftwareVideo(softwareVideo);
#endif
        // AAC/MP3 use the 2.1.0 winmd switches. Patch 0021 (2.1.0-bale.21) sends AC3/EAC3 to the
        // system Dolby decoder undecoded, keeping E-AC-3 JOC (Atmos). PCM and the rest use FFmpeg audio.
        config.Audio.SystemDecoderAAC = true;
        config.Audio.SystemDecoderMP3 = true;
        config.FFmpegOptions["bale_system_decoder_dolby"] = "1";
        config.Subtitles.AutoSelectForcedSubtitles = false;
        config.FFmpegOptions["rw_timeout"] = "10000000";
        ApplyDecodeTuning(config, SoftwareDecodeTuning.ForVideo(softwareVideo, route.VideoWidth, route.VideoHeight));
        // The zero-copy/GPU-grain integration is AV1-specific; an explicit HEVC software experiment
        // uses FFmpegInteropX's normal software provider instead.
        if (softwareVideo && SoftwareDecodePolicy.VideoCodecs.Contains(route.VideoCodec!)) ApplySoftwareVideo(config);
#if DEBUG_SERVER
        if (tuning.Decoder is DebugFfmpegDecoder.System or DebugFfmpegDecoder.Software)
            config.FFmpegOptions.Remove("bale_tonemap");
        DebugFfmpegToneMap.Apply(config, tuning, FfmpegWrapperVersion.Value);
        DebugFfmpegColorInfo.Apply(config, tuning, FfmpegWrapperVersion.Value);
        ApplyInputTuning(config, tuning, softwareVideo, route.VideoCodec);
#endif
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

    /// <summary>
    /// Wrapper patch 0022: decoded bitmaps of this one subtitle stream go to the overlay bound to the
    /// token (SubtitleOverlay.BindBitmaps) instead of becoming cues. Both options or neither.
    /// </summary>
    internal static void ApplySubtitleSink(MediaSourceConfig config, int? streamIndex, ulong token)
    {
        if (streamIndex is not { } index || token == 0) return;
        config.FFmpegOptions["bale_subtitle_stream"] = index.ToString(CultureInfo.InvariantCulture);
        config.FFmpegOptions["bale_subtitle_sink"] = token.ToString(CultureInfo.InvariantCulture);
    }

    internal static void ApplyDecodeTuning(MediaSourceConfig config, SoftwareDecodeTuning tuning)
    {
        if (tuning.DecodeAheadFrames is { } ahead) config.FFmpegOptions["bale_decode_ahead_frames"] = ahead.ToString(CultureInfo.InvariantCulture);
        if (tuning.Dav1dMaxFrameDelay is { } delay) config.FFmpegOptions["bale_dav1d_max_frame_delay"] = delay.ToString(CultureInfo.InvariantCulture);
    }

#if DEBUG_SERVER
    internal static VideoDecoderMode DecoderMode(VideoDecoderMode production, DebugFfmpegInputTuning tuning) => tuning.Decoder switch
    {
        DebugFfmpegDecoder.Auto => VideoDecoderMode.Automatic,
        DebugFfmpegDecoder.System => VideoDecoderMode.ForceSystemDecoder,
        DebugFfmpegDecoder.Software => VideoDecoderMode.ForceFFmpegSoftwareDecoder,
        _ => production,
    };

    private static void ApplyInputTuning(MediaSourceConfig config, DebugFfmpegInputTuning tuning, bool softwareVideo, string? codec)
    {
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
        if (tuning.HdrSupport is { } hdr) config.Video.HdrSupport = hdr ? HdrSupport.Enabled : HdrSupport.Disabled;
        if (softwareVideo)
        {
            ApplyDecodeTuning(config, new(tuning.DecodeAheadFrames, tuning.Dav1dMaxFrameDelay));
            if (tuning.DecodeThreadPriority is { } priority) config.FFmpegOptions["bale_decode_thread_priority"] = priority.ToString(CultureInfo.InvariantCulture);
            // Patch 0009: dav1d/gpu modes; off retains the independent export-only override.
            if (SoftwareDecodePolicy.VideoCodecs.Contains(codec!) && DebugFfmpegPlaytest.GrainOption(tuning.FilmGrain) is { } grain)
                config.FFmpegOptions[grain.Key] = grain.Value;
        }
        else if (config.Video.VideoDecoderMode == VideoDecoderMode.ForceSystemDecoder)
        {
            if (tuning.DecoderUseMaxResolution is { } maxres) config.FFmpegOptions["bale_decoder_use_max_resolution"] = maxres.ToString(CultureInfo.InvariantCulture);
            if (tuning.DecoderMaxDpbCount is { } dpb) config.FFmpegOptions["bale_decoder_max_dpb_count"] = dpb.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal static void ValidateVideoDecoders(FFmpegMediaSource source, PlaybackRouteDecision route, DebugFfmpegInputTuning tuning)
    {
        if (tuning.Decoder is null || tuning.RequiresToneMapGuard(route))
        {
            ValidateVideoDecoders(source, route);
            return;
        }
        // Automatic's engine is provisional here (upstream VideoDecoderMode IDL). Permit D3D11
        // and its software fallback only for this captured debug request; stats refresh after start.
        var streams = source.VideoStreams;
        if ((route.VideoCodec is not null && streams.Count == 0) || streams.Any(video => tuning.Decoder switch
        {
            DebugFfmpegDecoder.Auto => video.DecoderEngine is not (DecoderEngine.FFmpegD3D11HardwareDecoder or DecoderEngine.FFmpegSoftwareDecoder),
            DebugFfmpegDecoder.System => video.DecoderEngine != DecoderEngine.SystemDecoder,
            DebugFfmpegDecoder.Software => video.DecoderEngine != DecoderEngine.FFmpegSoftwareDecoder,
            _ => true,
        }))
            throw new InvalidDataException("The video decoder violates the debug decoder override.");
    }
#endif

    internal static void ValidateVideoDecoders(FFmpegMediaSource source, PlaybackRouteDecision route)
    {
        // ForceSystemDecoder falls back to FFmpeg for codecs with no compressed provider in 2.1.0.
        // Check every stream before mounting/reading frames, including unexpected extra video tracks.
        // A software route with no video stream also fails, so the open failure replans to a transcode.
        if (route.ConsoleToneMap)
        {
            if (source.VideoStreams.Count == 0 || source.VideoStreams.Any(video =>
                video.DecoderEngine != DecoderEngine.FFmpegD3D11HardwareDecoder))
                throw new InvalidDataException("Console tone mapping requires d3d11va decoding.");
            return;
        }
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
