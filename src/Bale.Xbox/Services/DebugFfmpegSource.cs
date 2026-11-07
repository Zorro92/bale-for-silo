#if DEBUG_SERVER
using System.Globalization;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

internal static class DebugFfmpegSource
{
    internal static MediaSourceConfig Configuration(string mode, DebugFfmpegTuning tuning)
    {
        // The native default logger can expose media URLs. Never install it for either debug path.
        FFmpegInteropLogging.SetLogProvider(null);
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
