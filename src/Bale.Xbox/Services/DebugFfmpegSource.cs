#if DEBUG_SERVER
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
