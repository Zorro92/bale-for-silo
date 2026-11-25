using System.Globalization;
using Bale.Core.Playback;
using Bale.Core.Settings;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

internal static class FfmpegSource
{
#if DEBUG_SERVER
    internal static MediaSourceConfig Configuration(PlaybackRouteDecision route)
        => Configuration(route, new DebugFfmpegInputTuning());

    /// <param name="rav1g">The debug AV1 decoder switch's decision for this mount (Rav1gPolicy.Route); null is dav1d.</param>
    /// <param name="sdrWhiteNits">The attempt's captured SDR Brightness, the device setting; ffmpeg-tune white= wins.</param>
    internal static MediaSourceConfig Configuration(PlaybackRouteDecision route, DebugFfmpegInputTuning tuning,
        Rav1gRouteDecision? rav1g = null, string dav1dPath = "d3d11", bool dav1dGpuFilmGrain = false,
        int sdrWhiteNits = SdrBrightness.DefaultNits)
#else
    /// <param name="sdrWhiteNits">The attempt's captured SDR Brightness, the device setting.</param>
    internal static MediaSourceConfig Configuration(PlaybackRouteDecision route, bool dav1dGpuFilmGrain = false,
        int sdrWhiteNits = SdrBrightness.DefaultNits)
#endif
    {
        // Debug captures only our safe Bale diagnostics; production keeps the native logger off.
        FfmpegLogging.Configure();
#if DEBUG_SERVER
        // Debug software fallback past the release bounds (ffmpeg-tune swfallback=on, or decoder=software;
        // docs/software-decode.md): any H.264/HEVC/VP9 the console's decoder can't take decodes in FFmpeg software, for
        // measurements. Release builds take only the route's bounded fallback below and refuse the rest.
        if (!route.SoftwareFallback && tuning.SoftwareFallbackAdmits(route))
            Log.Info($"player: debug software fallback codec={route.VideoCodec} {route.VideoWidth}x{route.VideoHeight} (past the release bounds)");
        else
#endif
        // A hardware codec's software route plays only as the route's bounded fallback (phase A: a claimed software envelope
        // admits the file, no console stage); otherwise the open fails and the replan withdraws the codec (server transcode).
        if (!route.UseFfmpeg || (route.SoftwareVideo
            ? !SoftwareDecodePolicy.VideoCodecs.Contains(route.VideoCodec!) && !route.SoftwareFallback
            : route.VideoCodec is not null && !SoftwareDecodePolicy.SystemVideoCodecs.Contains(route.VideoCodec)))
            throw new InvalidDataException("The video codec cannot use the local fallback.");
        if (route.SoftwareFallback)
            Log.Info($"player: software fallback codec={route.VideoCodec} {route.VideoWidth}x{route.VideoHeight} (past the hardware decoder)");
        if (route.DolbyVision7BaseLayer && route.VideoCodec != "hevc")
            throw new InvalidDataException("DV7 base-layer playback requires HEVC.");
        var config = new MediaSourceConfig();
        // Exact seeks (docs/player-plan.md, 2026-11-16): upstream's fast seek jumps to the nearest
        // keyframe, 4.5-10 s off and often late, skipping content. The normal backward seek lands on
        // the target; Media Foundation and d3d11va start presenting there (console-verified 0.1.557).
        // Keep the existing fast-seek policy for software video: its earlier zero-copy route uploaded
        // every frame from the keyframe, and a 4K seek ran out of GPU memory (0x8007000E) on 0.1.558.
        config.General.FastSeek = route.SoftwareVideo;
        var rav1gStage = false;
#if DEBUG_SERVER
        // A probed URL can expose HDR AV1 that the Silo planner normally transcodes. rav1g already owns
        // its tone-map/downscale stage; keep that same configuration and its marked-output guard.
        rav1gStage = rav1g is { Rav1g: true } && route.SoftwareVideo && route.VideoCodec == "av1";
#endif
        // H.264 joined the SDR downscale route on 2026-11-24 (route.rs); the fork's stage is codec-neutral.
        if (route.ConsoleStage && (route.VideoCodec is not ("hevc" or "vp9" or "h264") && !rav1gStage || !ConsoleToneMapRuntime.IsAvailable))
            throw new InvalidDataException("Console tone mapping is unavailable for this source.");
        // FFmpeg's HEVC decoder handles only nuh_layer_id 0 and does not apply the RPU.
        // d3d11va gets that base picture, never an opaque DV7 system-decoder bitstream.
        config.Video.VideoDecoderMode = route.RequiresD3D11 ? VideoDecoderMode.Automatic : route.SoftwareVideo
            ? VideoDecoderMode.ForceFFmpegSoftwareDecoder : VideoDecoderMode.ForceSystemDecoder;
        if (route.DolbyVision7BaseLayer && !route.ConsoleToneMap) config.Video.HdrSupport = HdrSupport.Enabled;
        // BT.2390, same defaults as the server. The SDR downscale route takes the same stage: the fork
        // runs bale_gpu's scale-only pass for BT.709 SDR larger than the output size (bale.39+).
        if (route.ConsoleStage) config.FFmpegOptions["bale_tonemap"] = "1";
        // Stream-sized d3d11va decoder pools (fork bale.68+, in-band SPS since bale.79): the stream's DPB + 3 instead
        // of FFmpeg's 20 (4K HEVC 478 -> ~191-215 MiB). Release default since 2026-11-24 after the owner's long-play
        // check (30 min 4K HEVC HDR10, 0 missed, pool 8; a library MKV with in-band SPS: 9 surfaces). Unsupported codecs
        // and unreadable streams keep FFmpeg's pool inside the fork.
        if (!route.SoftwareVideo && route.VideoCodec is "hevc" or "h264" or "vp9")
            config.FFmpegOptions["bale_d3d11va_pool_from_stream"] = "1";
        var softwareVideo = route.SoftwareVideo;
#if DEBUG_SERVER
        if (rav1gStage) config.Video.VideoDecoderMode = VideoDecoderMode.ForceFFmpegSoftwareDecoder;
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
        // AV1 uses dav1d on D3D11, with CPU grain unless the attempt opted into GPU grain (owner, 2026-11-19).
        // Other software codecs use the ordinary provider without AV1-specific grain options.
#if DEBUG_SERVER
        // Debug only (owner, 2026-11-18): rav1g decodes software AV1 on the fork's GPU provider instead.
        if (rav1g is { Rav1g: true } && softwareVideo && route.VideoCodec == "av1")
            ApplyRav1g(config, rav1g, dav1dPath, dav1dGpuFilmGrain);
        else
#endif
        if (softwareVideo && route.VideoCodec == "av1")
        {
            ApplySoftwareVideo(config, dav1dGpuFilmGrain);
#if DEBUG_SERVER
            ApplyDav1dPath(config, dav1dPath);
#endif
        }
#if DEBUG_SERVER
        if (tuning.Decoder is DebugFfmpegDecoder.System or DebugFfmpegDecoder.Software)
            config.FFmpegOptions.Remove("bale_tonemap");
        DebugFfmpegToneMap.Apply(config, tuning, FfmpegWrapperVersion.Value);
        DebugToneMapSwitches.ApplyOutput(config, FfmpegWrapperVersion.Value);
        DebugToneMapSwitches.ApplyRav1gDisplay(config);
        DebugFfmpegColorInfo.Apply(config, tuning, FfmpegWrapperVersion.Value);
        ApplyInputTuning(config, tuning, softwareVideo, route.VideoCodec);
        // ffmpeg-tune white= wins over the device setting for this attempt; default leaves the setting.
        if (tuning.SdrWhiteNits is { } white) sdrWhiteNits = white;
#endif
        // After every override that adds or removes the stage (bale_tonemap), in release builds as in debug ones.
        ApplySdrWhite(config, sdrWhiteNits);
        if (softwareVideo && route.VideoCodec == "av1") LogFilmGrainRequest(config, dav1dGpuFilmGrain);
        return config;
    }

    /// <summary>
    /// Settings › Playback › SDR Brightness (owner, 2026-11-24): the SDR white the console tone map maps HDR to on an SDR TV,
    /// the same for Bale's stage and rav1g's display pass (fork key <c>bale_tonemap_white_nits</c>, wrapper bale.77). Only on
    /// mounts that configure that tone map (<c>bale_tonemap</c>: the stage route, HDR or the SDR downscale, and the rav1g
    /// route when it tone-maps); the SDR downscale applies no curve, so there it changes nothing. Default, and any value
    /// outside the fork's 80-400 nits, leaves the key unset (the fork's 100 nits, as before), so it can never fail an open.
    /// Logs once per open that sends a white (the fork doesn't log it).
    /// </summary>
    internal static void ApplySdrWhite(MediaSourceConfig config, int nits)
    {
        if (nits == SdrBrightness.DefaultNits || nits is < SdrBrightness.MinimumNits or > SdrBrightness.MaximumNits
            || !config.FFmpegOptions.ContainsKey("bale_tonemap")) return;
        config.FFmpegOptions["bale_tonemap_white_nits"] = nits.ToString(CultureInfo.InvariantCulture);
        Log.Info($"player: tone map SDR white={nits} nits");
    }

    /// <summary>
    /// dav1d uses CPU grain by default. The opt-in uses the fork's private D3D11 staged upload and
    /// bale_gpu grain pass (bale.60), with CPU grain on probe failure. Neither choice needs D3D12
    /// or public upload/zero-copy keys. Its capability claim and rav1g fallback stay capped at 1080p.
    /// </summary>
    internal static void ApplySoftwareVideo(MediaSourceConfig config, bool dav1dGpuFilmGrain = false)
    {
        config.FFmpegOptions["bale_film_grain_mode"] = dav1dGpuFilmGrain ? "3"
            : SoftwareDecodeTuning.FilmGrainMode.ToString(CultureInfo.InvariantCulture);
    }

    private static void LogFilmGrainRequest(MediaSourceConfig config, bool dav1dGpuFilmGrain)
    {
        // Log the final request after debug overrides; the fork reports the probe's actual outcome.
        var mode = config.FFmpegOptions.ContainsKey("bale_dav1d_export_film_grain") ? "off"
            : config.FFmpegOptions["bale_film_grain_mode"] switch
            {
                "3" => "gpu-d3d11",
                "2" => "gpu-zerocopy",
                _ => "cpu",
            };
        Log.Info($"player: dav1d grain request={mode} (setting={(dav1dGpuFilmGrain ? "on" : "off")})");
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

    /// <summary>
    /// Phase 2 of the bit-depth rule (AGENTS.md): on an 8-bit SDR display ask the fork's stage for dithered 8-bit NV12
    /// (<c>bale_tonemap_output_depth=8</c>, next to the output size keys) when the mount's EngineOutputPolicy decision
    /// says so; unset keeps today's P010. Older wrappers ignore the key and keep P010, which the guard still accepts.
    /// Only when the stage is still configured (a debug decoder override may have removed it). Logs once per open.
    /// </summary>
    /// <returns>True when 8-bit stage output was requested for this mount.</returns>
    internal static bool ApplyStageOutputDepth(MediaSourceConfig config, PlaybackRouteDecision route, EngineOutputDecision output)
    {
        if (!route.ConsoleStage) return false;
        var requested = output.StageOutputDepth == 8 && config.FFmpegOptions.ContainsKey("bale_tonemap");
        if (requested) config.FFmpegOptions["bale_tonemap_output_depth"] = "8";
        else config.FFmpegOptions.Remove("bale_tonemap_output_depth");
        var reason = output.StageOutputDepth == 8 && !requested ? "stage not configured" : output.StageReason;
        Log.Info($"player: stage 8-bit output {(requested ? "requested" : "not requested")} ({reason})");
        return requested;
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
    /// Debug-only restoration of the old dav1d zero-copy path for a grain A/B. The caller passes the
    /// attempt's captured switch; failure to register keeps D3D11 with the captured grain setting.
    /// </summary>
    private static void ApplyDav1dPath(MediaSourceConfig config, string path)
    {
        if (path != "zerocopy") return;
        if (!ZeroCopyRuntime.Register())
        {
            Log.Info("player: dav1d zero-copy unavailable; using d3d11");
            return;
        }
        config.FFmpegOptions["bale_d3d11_upload"] = "6";
        config.FFmpegOptions["bale_zero_copy"] = "1";
        config.FFmpegOptions["bale_film_grain_mode"] = "2";
    }

    /// <summary>
    /// The debug rav1g route (the fork's bale/design-rav1g-provider.md). Always configure dav1d's
    /// D3D11 output with the captured grain setting for an in-open fallback, capped at 1080 lines by wrapper bale.59.
    /// A taller stream whose GPU open declines fails to the normal server replan. The stage keys
    /// downscale SDR or tone-map PQ/HLG on an SDR display; a dav1d fallback ignores those keys.
    /// Only the captured debug dav1d-path zerocopy switch restores the old fallback path.
    /// </summary>
    internal static void ApplyRav1g(MediaSourceConfig config, Rav1gRouteDecision rav1g, string dav1dPath = "d3d11",
        bool dav1dGpuFilmGrain = false)
    {
        ApplySoftwareVideo(config, dav1dGpuFilmGrain);
        ApplyDav1dPath(config, dav1dPath);
        config.FFmpegOptions["bale_av1_fallback_max_height"] = "1080";
        config.FFmpegOptions["bale_av1_decoder"] = "1";
        // Decode-ahead from Rav1gPolicy.Route (8, as dav1d above 1080p: since fork bale.46 the buffer no longer competes
        // with rav1g's pool). ApplyInputTuning runs after this, so ffmpeg-tune ahead= still wins.
        if (rav1g.DecodeAheadFrames is { } ahead)
            config.FFmpegOptions["bale_decode_ahead_frames"] = ahead.ToString(CultureInfo.InvariantCulture);
        // Player-side texture cap (fork bale.56), from the frame rate (Rav1gPolicy.Route); rav1g-tune outtex= overrides it below.
        if (rav1g.OutputTextures is { } textures)
            config.FFmpegOptions["bale_gpu_output_textures"] = textures.ToString(CultureInfo.InvariantCulture);
        if (rav1g.ToneMap) config.FFmpegOptions["bale_tonemap"] = "1";
        if (rav1g.OutputWidth is { } width && rav1g.OutputHeight is { } height)
        {
            config.FFmpegOptions["bale_tonemap_output_width"] = width.ToString(CultureInfo.InvariantCulture);
            config.FFmpegOptions["bale_tonemap_output_height"] = height.ToString(CultureInfo.InvariantCulture);
        }
        // rav1g's display output (fork bale.74, rav1g interface 2): rav1g writes the display-size, tone-mapped image where
        // the stage would run; the fork's default (unset) is the stage, so the rav1g route asks for it (owner, 2026-11-24:
        // the default, after 0% missed at 4K60 60M SDR and HDR10 vs the stage's 3-5% / 12-13%). rav1g-tune
        // display=off restores the stage for A/B (the loop below overrides this).
        config.FFmpegOptions["bale_rav1g_display"] = "1";
        // Console-test knobs for the provider's feeder thread, rav1g's perf timings and parse depth (bale.52+); this
        // whole method is debug-only (DEBUG_SERVER region).
        foreach (var (key, value) in DebugRav1gTuning.Options()) config.FFmpegOptions[key] = value;
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
        // ffmpeg-tune pool= overrides the release default (set in Configuration) for A/B; default = the same codecs:
        // HEVC (4K 20 -> 8 surfaces, -287 MiB), VP9 (4K 12 -> 11) and H.264 (1080p 20 -> 8), each with 0 missed frames.
        var pool = tuning.DecoderPoolFromStream ?? codec is "hevc" or "h264" or "vp9";
        config.FFmpegOptions["bale_d3d11va_pool_from_stream"] = pool ? "1" : "0";
        if (softwareVideo)
        {
            ApplyDecodeTuning(config, new(tuning.DecodeAheadFrames, tuning.Dav1dMaxFrameDelay));
            if (tuning.DecodeThreadPriority is { } priority) config.FFmpegOptions["bale_decode_thread_priority"] = priority.ToString(CultureInfo.InvariantCulture);
            // FFmpeg's software decoder threads (frame + slice for H.264/HEVC/VP9, dav1d's pool for AV1); unset keeps the
            // wrapper's logical CPU count. A/B for the 4-core console (docs/software-decode.md).
            if (tuning.DecoderThreads is { } threads) config.Video.MaxDecoderThreads = (uint)threads;
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
