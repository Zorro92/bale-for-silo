#if DEBUG_SERVER
using System.Globalization;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

internal static class DebugFfmpegSource
{
    internal static MediaSourceConfig Configuration(string mode, DebugFfmpegTuning tuning)
    {
        // Share the strict Bale-prefix filter; never attach the native default logger.
        FfmpegLogging.Configure();
        var config = new MediaSourceConfig();
        config.Video.VideoDecoderMode = mode switch
        {
            "software" => VideoDecoderMode.ForceFFmpegSoftwareDecoder,
            "system" => VideoDecoderMode.ForceSystemDecoder,
            _ => VideoDecoderMode.Automatic,
        };
        if (tuning.Threads is { } threads) config.Video.MaxDecoderThreads = threads;
        if (tuning.Allow10bit is { } allow10bit) config.Video.VideoOutputAllow10bit = allow10bit;
        FfmpegSource.ApplyDecodeTuning(config, tuning.DecodeTuning(mode));
        if (tuning.DecodeThreadPriority is { } priority) config.FFmpegOptions["bale_decode_thread_priority"] = priority.ToString(CultureInfo.InvariantCulture);
        if (tuning.DecoderUseMaxResolution is { } maxres) config.FFmpegOptions["bale_decoder_use_max_resolution"] = maxres.ToString(CultureInfo.InvariantCulture);
        if (tuning.DecoderMaxDpbCount is { } dpb) config.FFmpegOptions["bale_decoder_max_dpb_count"] = dpb.ToString(CultureInfo.InvariantCulture);
        if (tuning.HdrSupport is { } hdr) config.Video.HdrSupport = hdr ? HdrSupport.Enabled : HdrSupport.Disabled;
        // Software mode probes the production path (zero-copy, upload 6, GPU grain); throws when
        // zero-copy can't register. A grain override is applied after it, as in the player.
        if (mode == "software") FfmpegSource.ApplySoftwareVideo(config);
        if (DebugFfmpegPlaytest.GrainOption(tuning.FilmGrain) is { } grain) config.FFmpegOptions[grain.Key] = grain.Value;
        config.FFmpegOptions["rw_timeout"] = "10000000";
        return config;
    }

    internal static string DecoderName(DecoderEngine engine) => engine switch
    {
        DecoderEngine.SystemDecoder => "system",
        DecoderEngine.FFmpegSoftwareDecoder => "FFmpeg software",
        DecoderEngine.FFmpegD3D11HardwareDecoder => "FFmpeg D3D11",
        _ => "unknown",
    };
}
#endif
