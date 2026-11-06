using Bale.Core.Playback;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

internal static class FfmpegSource
{
    internal static MediaSourceConfig Configuration(PlaybackRouteDecision route)
    {
        // Never attach the native logger: it can disclose media URLs and credentials.
        FFmpegInteropLogging.SetLogProvider(null);
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
        return config;
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
        if (tuning.ReadSize is { } readSize) config.General.FileStreamReadSize = readSize;
        return config;
    }
#endif

    internal static void ValidateVideoDecoders(FFmpegMediaSource source, PlaybackRouteDecision route)
    {
        // ForceSystemDecoder falls back to FFmpeg for codecs with no compressed provider in 2.1.0.
        // Check every stream before mounting/reading frames, including unexpected extra video tracks.
        foreach (var video in source.VideoStreams)
        {
            var codec = SoftwareDecodePolicy.NormalizeCodec(video.CodecName);
            var valid = route.SoftwareVideo
                ? SoftwareDecodePolicy.VideoCodecs.Contains(codec!) && video.DecoderEngine == DecoderEngine.FFmpegSoftwareDecoder
                : video.DecoderEngine == DecoderEngine.SystemDecoder;
            if (!valid) throw new InvalidDataException("The video decoder violates the local fallback policy.");
        }
    }

    internal static string DecoderName(DecoderEngine engine) => engine switch
    {
        DecoderEngine.SystemDecoder => "system",
        DecoderEngine.FFmpegSoftwareDecoder => "FFmpeg software",
        DecoderEngine.FFmpegD3D11HardwareDecoder => "FFmpeg D3D11",
        _ => "unknown",
    };
}
