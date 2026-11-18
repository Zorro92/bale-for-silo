using System.Globalization;
using Bale.Core.Playback;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

internal static class FfmpegSource
{
    internal static MediaSourceConfig Configuration(PlaybackRouteDecision route)
#if DEBUG_SERVER
        => Configuration(route, new DebugFfmpegInputTuning());

    /// <param name="rav1g">The debug AV1 decoder switch's decision for this mount (Rav1gPolicy.Route); null is dav1d.</param>
    internal static MediaSourceConfig Configuration(PlaybackRouteDecision route, DebugFfmpegInputTuning tuning,
        Rav1gRouteDecision? rav1g = null)
#endif
    {
        // Debug captures only our safe Bale diagnostics; production keeps the native logger off.
        FfmpegLogging.Configure();
        if (!route.UseFfmpeg || (route.SoftwareVideo
            ? !SoftwareDecodePolicy.VideoCodecs.Contains(route.VideoCodec!)
            : route.VideoCodec is not null && !SoftwareDecodePolicy.SystemVideoCodecs.Contains(route.VideoCodec)))
            throw new InvalidDataException("The video codec cannot use the local fallback.");
        if (route.DolbyVision7BaseLayer && route.VideoCodec != "hevc")
            throw new InvalidDataException("DV7 base-layer playback requires HEVC.");
        var config = new MediaSourceConfig();
        // Exact seeks (docs/player-plan.md, 2026-11-16): upstream's fast seek jumps to the nearest
        // keyframe, 4.5-10 s off and often late, skipping content. The normal backward seek lands on
        // the target; Media Foundation and d3d11va start presenting there (console-verified 0.1.557).
        // Software video keeps fast seek: dav1d's zero-copy path uploads every frame from the keyframe
        // to the target, and a 4K AV1 seek ran out of GPU memory (0x8007000E) on 0.1.558.
        config.General.FastSeek = route.SoftwareVideo;
        if (route.ConsoleStage && (route.VideoCodec is not ("hevc" or "vp9") || !ConsoleToneMapRuntime.IsAvailable))
            throw new InvalidDataException("Console tone mapping is unavailable for this source.");
        // FFmpeg's HEVC decoder handles only nuh_layer_id 0 and does not apply the RPU.
        // d3d11va gets that base picture, never an opaque DV7 system-decoder bitstream.
        config.Video.VideoDecoderMode = route.RequiresD3D11 ? VideoDecoderMode.Automatic : route.SoftwareVideo
            ? VideoDecoderMode.ForceFFmpegSoftwareDecoder : VideoDecoderMode.ForceSystemDecoder;
        if (route.DolbyVision7BaseLayer && !route.ConsoleToneMap) config.Video.HdrSupport = HdrSupport.Enabled;
        // BT.2390, same defaults as the server. The SDR downscale route takes the same stage: the fork
        // runs bale_gpu's scale-only pass for BT.709 SDR larger than the output size (bale.39+).
        if (route.ConsoleStage) config.FFmpegOptions["bale_tonemap"] = "1";
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
        // Only AV1 uses zero-copy/GPU grain. MPEG-4 Part 2 uses FFmpegInteropX's ordinary
        // software provider; it has neither a D3D11VA decoder nor an AV1 zero-copy output path.
#if DEBUG_SERVER
        // Debug only (owner, 2026-11-18): rav1g decodes software AV1 on the fork's GPU provider instead.
        if (rav1g is { Rav1g: true } && softwareVideo && route.VideoCodec == "av1") ApplyRav1g(config, rav1g);
        else
#endif
        if (softwareVideo && route.VideoCodec == "av1") ApplySoftwareVideo(config);
#if DEBUG_SERVER
        if (tuning.Decoder is DebugFfmpegDecoder.System or DebugFfmpegDecoder.Software)
            config.FFmpegOptions.Remove("bale_tonemap");
        DebugFfmpegToneMap.Apply(config, tuning, FfmpegWrapperVersion.Value);
        DebugToneMapSwitches.ApplyOutput(config, FfmpegWrapperVersion.Value);
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
    /// <summary>
    /// Hand MF display-sized SDR on the tone-map route: its video processor leaks driver state per open when
    /// it scales 4K SDR down itself (dies at ~23 opens); bale_gpu's linear-light area downscale ran 40/40
    /// (2026-11-17). A debug size override set by Configuration is kept.
    /// </summary>
    internal static void ApplyDisplaySize(MediaSourceConfig config, PlaybackRouteDecision route, (int Width, int Height)? display)
    {
        if (!route.ConsoleStage || display is not { } screen || config.FFmpegOptions.ContainsKey("bale_tonemap_output_width")
            || !config.FFmpegOptions.ContainsKey("bale_tonemap")
            || !ConsoleToneMapPolicy.DownscaleToDisplay(route.VideoWidth, route.VideoHeight, screen.Width, screen.Height)
            || !ConsoleToneMapPolicy.DownscaleWrapperSupported(FfmpegWrapperVersion.Value)
            || !ConsoleToneMapRuntime.SupportsDownscale) return;
        config.FFmpegOptions["bale_tonemap_output_width"] = screen.Width.ToString(CultureInfo.InvariantCulture);
        config.FFmpegOptions["bale_tonemap_output_height"] = screen.Height.ToString(CultureInfo.InvariantCulture);
        Log.Info($"player: tone map output fits {screen.Width}x{screen.Height} (video {route.VideoWidth}x{route.VideoHeight})");
    }

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
    /// <summary>
    /// The debug rav1g route (wrapper 2.1.0-bale.44, the fork's bale/design-rav1g-provider.md): bale_av1_decoder=1 puts
    /// AV1 on GpuDecoderSampleProvider. rav1g needs no zero-copy registration, so its absence never fails the open.
    /// The fork ignores the zero-copy/grain keys for a rav1g stream but honours them when the open falls back to dav1d
    /// (rav1g.dll missing, ABI mismatch, unsupported stream), so they are set whenever zero-copy registers: that
    /// fallback is then today's dav1d path. The stage keys work as on the D3D11 path: SDR larger than the display is
    /// downscaled by bale_gpu (never Media Foundation's leaking scaler), PQ/HLG is tone-mapped on an SDR display;
    /// a dav1d fallback ignores them.
    /// </summary>
    internal static void ApplyRav1g(MediaSourceConfig config, Rav1gRouteDecision rav1g)
    {
        if (ZeroCopyRuntime.Register()) ApplySoftwareVideo(config);
        else Log.Info("player: rav1g route without zero-copy; a dav1d fallback would decode without it");
        config.FFmpegOptions["bale_av1_decoder"] = "1";
        if (rav1g.ToneMap) config.FFmpegOptions["bale_tonemap"] = "1";
        if (rav1g.OutputWidth is { } width && rav1g.OutputHeight is { } height)
        {
            config.FFmpegOptions["bale_tonemap_output_width"] = width.ToString(CultureInfo.InvariantCulture);
            config.FFmpegOptions["bale_tonemap_output_height"] = height.ToString(CultureInfo.InvariantCulture);
        }
    }

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
        // Seek-landing A/B: off is upstream's backward seek (no nearest-keyframe fast seek).
        if (tuning.FastSeek is { } fastSeek) config.General.FastSeek = fastSeek;
        if (softwareVideo)
        {
            ApplyDecodeTuning(config, new(tuning.DecodeAheadFrames, tuning.Dav1dMaxFrameDelay));
            if (tuning.DecodeThreadPriority is { } priority) config.FFmpegOptions["bale_decode_thread_priority"] = priority.ToString(CultureInfo.InvariantCulture);
            // Patch 0009: dav1d/gpu modes; off retains the independent export-only override.
            if (codec == "av1" && DebugFfmpegPlaytest.GrainOption(tuning.FilmGrain) is { } grain)
                config.FFmpegOptions[grain.Key] = grain.Value;
        }
        else if (config.Video.VideoDecoderMode == VideoDecoderMode.ForceSystemDecoder)
        {
            if (tuning.DecoderUseMaxResolution is { } maxres) config.FFmpegOptions["bale_decoder_use_max_resolution"] = maxres.ToString(CultureInfo.InvariantCulture);
            if (tuning.DecoderMaxDpbCount is { } dpb) config.FFmpegOptions["bale_decoder_max_dpb_count"] = dpb.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal static void ValidateVideoDecoders(FFmpegMediaSource source, PlaybackRouteDecision route, DebugFfmpegInputTuning tuning,
        bool rav1g = false)
    {
        if (rav1g)
        {
            // rav1g's provider reports the D3D11 engine (the wrapper's engine enum is frozen); its open-time fallback is
            // dav1d's software engine. Either is this route's one AV1 stream; anything else fails the open as before.
            var av1 = source.VideoStreams;
            if (av1.Count == 0 || av1.Any(video => SoftwareDecodePolicy.NormalizeCodec(video.CodecName) != "av1"
                    || video.DecoderEngine is not (DecoderEngine.FFmpegD3D11HardwareDecoder or DecoderEngine.FFmpegSoftwareDecoder)))
                throw new InvalidDataException("The rav1g route requires its AV1 stream on rav1g or dav1d.");
            return;
        }
        if (tuning.Decoder is null || tuning.RequiresToneMapGuard(route) || route.DolbyVision7BaseLayer)
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
        if (route.DolbyVision7BaseLayer)
        {
            var index = DolbyVision7Policy.BaseLayerTrackIndex(source.VideoStreams.Select(video => video.StreamIndex).ToArray(),
                source.VideoStreams.Count);
            var video = source.VideoStreams[index];
            // Fail before creating the item if FFmpeg preferred a separate EL or lost the BL.
            // Only this base stream is ever enabled; unused EL decoders may be unsupported.
            if (SoftwareDecodePolicy.NormalizeCodec(video.CodecName) != "hevc"
                || video.DecoderEngine != DecoderEngine.FFmpegD3D11HardwareDecoder
                || source.CurrentVideoStream?.StreamIndex != video.StreamIndex)
                throw new InvalidDataException("DV7 requires its HDR10 base layer through d3d11va.");
            return;
        }
        // ForceSystemDecoder falls back to FFmpeg for codecs with no compressed provider in 2.1.0.
        // Check every stream before mounting/reading frames, including unexpected extra video tracks.
        // A software route with no video stream also fails, so the open failure replans to a transcode.
        if (route.ConsoleStage)
        {
            if (source.VideoStreams.Count == 0 || source.VideoStreams.Any(video =>
                video.DecoderEngine != DecoderEngine.FFmpegD3D11HardwareDecoder))
                throw new InvalidDataException("The console stage requires d3d11va decoding.");
            return;
        }
        var streams = source.VideoStreams.Select(video => ((string?)video.CodecName,
            video.DecoderEngine == DecoderEngine.FFmpegSoftwareDecoder, video.DecoderEngine == DecoderEngine.SystemDecoder)).ToArray();
        if (!SoftwareDecodePolicy.VideoDecodersAllowed(route.SoftwareVideo, streams, route.VideoCodec))
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
