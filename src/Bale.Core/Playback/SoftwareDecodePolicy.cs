namespace Bale.Core.Playback;

/// <summary>Bundled decoder policy, separate from the console's measured system decoders.</summary>
public static class SoftwareDecodePolicy
{
    // The project owner maintains this allowlist. Add no other software video codec without them.
    public static IReadOnlyList<string> VideoCodecs { get; } = Array.AsReadOnly(new[] { "av1" });

    // Codecs the system (hardware) decoders may take. A claim for one still needs the fixed
    // baseline or the console's decoder probe (ConsoleEvidence.ForHardware).
    public static IReadOnlyList<string> SystemVideoCodecs { get; } = Array.AsReadOnly(new[] { "h264", "hevc", "vp9", "vc1", "mpeg2video" });

    // Hardware codecs that play only through FFmpeg's demuxer feeding the system decoder.
    // Windows' own WebM source opened VP9 but never left Paused at 0 s, and its MP4 source and
    // AdaptiveMediaSource are untested for VP9 (docs/player-plan.md). So every direct VP9 file
    // takes the FFmpeg demuxer, HLS never carries VP9, and "ffmpeg off" withdraws the claim.
    // VC-1/MPEG-2 use the same original-file boundary: only their compressed FFmpeg providers
    // feeding system decoders are verified; no Windows container-source or HLS claim follows.
    public static IReadOnlyList<string> DemuxedVideoCodecs { get; } = Array.AsReadOnly(new[] { "vp9", "vc1", "mpeg2video" });

    // Measured Media Foundation playback, not merely installed decoder registrations.
    public static IReadOnlyList<string> SystemAudioCodecs { get; } = Array.AsReadOnly(new[] { "aac", "ac3", "eac3", "mp3" });

    // silo-server capabilities_v3.go normalizeCodecV3 / protocol_v3.go spellings. Decode, not passthrough.
    // The server compares audio claims to the stored ffprobe codec name exactly (after lowercasing),
    // so LPCM needs each pcm_* spelling. PCM and MPEG-1 Layer II decode in FFmpeg: Windows' own MP4
    // source was never verified with LPCM or MP2, so those files take the FFmpeg route like DTS.
    // "pcm" is the route's normalized name for every pcm_* (NormalizeCodec).
    public static IReadOnlyList<string> AudioCodecs { get; } = Array.AsReadOnly(new[]
    {
        "dts", "dts_hd", "truehd", "flac", "opus", "vorbis", "alac", "mp2",
        "pcm", "pcm_s16le", "pcm_s16be", "pcm_s24le", "pcm_s24be", "pcm_s32le", "pcm_s32be",
        "pcm_f32le", "pcm_f32be", "pcm_f64le", "pcm_f64be", "pcm_u8", "pcm_bluray", "pcm_dvd",
    });

    public static string? NormalizeCodec(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "h265" or "h.265" or "x265" => "hevc",
        "h.264" or "avc" or "x264" => "h264",
        "vp09" => "vp9",
        "e-ac-3" or "ec-3" => "eac3",
        "mlp fba" => "truehd",
        { } value when value.StartsWith("pcm_", StringComparison.Ordinal) => "pcm",
        var value => value,
    };

    /// <summary>
    /// Software video (dav1d AV1) is measured only on Xbox Series (Zen 2 with AVX2). Xbox One, One S and
    /// One X have 1.75–2.3 GHz Jaguar cores without AVX2, where dav1d drops to its SSSE3 paths, so they
    /// claim no software video and the server transcodes AV1. A missing AVX2 alone also excludes it;
    /// an unknown device form keeps the claim.
    /// </summary>
    public static bool SoftwareVideoSupported(bool avx2, string? deviceForm) =>
        avx2 && deviceForm?.TrimStart().StartsWith("Xbox One", StringComparison.OrdinalIgnoreCase) != true;

    /// <summary>Drops only the bundled software video claims; audio fallbacks stay.</summary>
    public static DeviceCapabilities WithoutSoftwareVideo(DeviceCapabilities device) => device with
    {
        VideoCodecs = device.VideoCodecs.Where(codec => !VideoCodecs.Contains(codec)).ToArray(),
        VideoDecode = device.VideoDecode.Where(decode => decode.Hardware).ToArray(),
    };

    /// <summary>Drops every claim that needs FFmpeg: software video, demuxer-only video and fallback audio.</summary>
    public static DeviceCapabilities WithoutFallbacks(DeviceCapabilities device) => device with
    {
        VideoCodecs = device.VideoCodecs.Where(codec => !VideoCodecs.Contains(codec) && !DemuxedVideoCodecs.Contains(codec)).ToArray(),
        VideoDecode = device.VideoDecode.Where(decode => decode.Hardware && !DemuxedVideoCodecs.Contains(decode.Codec)).ToArray(),
        AudioDecodeCodecs = device.AudioDecodeCodecs.Where(codec => !AudioCodecs.Contains(codec)).ToArray(),
        // The base-layer claim relies on the FFmpeg demuxer for DV-tagged MP4.
        OriginalDolbyVision8BaseLayerSupported = false,
    };

    /// <summary>
    /// Checks the opened source's video streams against the route. Every stream must use the route's
    /// decoder (system decoder, or FFmpeg software for an allowlisted codec), and a software-video route
    /// must have at least one video stream: the server planned a software decode, so a source without
    /// video is a failed open that must replan to a server transcode, never silent audio-only playback.
    /// </summary>
    public static bool VideoDecodersAllowed(bool softwareVideo,
        IReadOnlyCollection<(string? Codec, bool SoftwareDecoder, bool SystemDecoder)> streams)
    {
        if (softwareVideo && streams.Count == 0) return false;
        return streams.All(stream => softwareVideo
            ? NormalizeCodec(stream.Codec) is { } codec && VideoCodecs.Contains(codec) && stream.SoftwareDecoder
            : stream.SystemDecoder);
    }

    internal static PlaybackStartRequestV3 WithoutFailedFallbacks(PlaybackStartRequestV3 start, PlaybackRouteDecision route)
    {
        // A software route withdraws the software video codecs. A system-decoder route withdraws its
        // own codec only when that codec exists here solely through this FFmpeg route (VP9).
        bool KeepVideo(string codec) => route.SoftwareVideo ? !VideoCodecs.Contains(codec)
            : codec != route.VideoCodec || !DemuxedVideoCodecs.Contains(codec);
        // Claims carry exact spellings (pcm_s24le); the route carries the normalized one (pcm).
        bool KeepAudio(string codec) => !route.SoftwareAudio || NormalizeCodec(codec) != route.AudioCodec;
        var capabilities = start.ClientCapabilities with
        {
            CodecsVideo = start.ClientCapabilities.CodecsVideo.Where(KeepVideo).ToArray(),
            CodecsVideoHardware = start.ClientCapabilities.CodecsVideoHardware.Where(KeepVideo).ToArray(),
            VideoDecode = start.ClientCapabilities.VideoDecode.Where(decode => KeepVideo(decode.Codec)).ToArray(),
            CodecsAudio = start.ClientCapabilities.CodecsAudio.Where(KeepAudio).ToArray(),
        };
        var context = start.ClientPlaybackContext with
        {
            Deliveries = start.ClientPlaybackContext.Deliveries.ToDictionary(pair => pair.Key, pair => pair.Value with
            {
                VideoCodecs = pair.Value.VideoCodecs.Where(KeepVideo).ToArray(),
                AudioDecodeCodecs = pair.Value.AudioDecodeCodecs.Where(KeepAudio).ToArray(),
                // A failed base-layer mount must not be offered again: the server then remuxes or transcodes.
                ValidatedClaims = route.DolbyVisionBaseLayer
                    ? pair.Value.ValidatedClaims.Where(claim => claim != PlaybackProtocol.ClientDolbyVision8BaseLayerClaim).ToArray()
                    : pair.Value.ValidatedClaims,
            }),
        };
        return start with
        {
            ClientCapabilities = capabilities,
            ClientPlaybackContext = context,
            ClientFeatures = start.ClientFeatures.Where(feature => feature != PlaybackProtocol.SoftwareVideoDecodeFeature
                || capabilities.VideoDecode.Any(decode => !decode.Hardware)).ToArray(),
        };
    }
}
