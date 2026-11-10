namespace Bale.Core.Playback;

/// <summary>Bundled decoder policy, separate from the console's measured system decoders.</summary>
public static class SoftwareDecodePolicy
{
    // The project owner maintains this allowlist. Add no other software video codec without them.
    public static IReadOnlyList<string> VideoCodecs { get; } = Array.AsReadOnly(new[] { "av1" });

    // Measured Media Foundation playback, not merely installed decoder registrations.
    public static IReadOnlyList<string> SystemAudioCodecs { get; } = Array.AsReadOnly(new[] { "aac", "ac3", "eac3", "mp3", "pcm" });

    // silo-server capabilities_v3.go normalizeCodecV3 / protocol_v3.go spellings. Decode, not passthrough.
    public static IReadOnlyList<string> AudioCodecs { get; } = Array.AsReadOnly(new[] { "dts", "dts_hd", "truehd", "flac", "opus", "vorbis", "alac" });

    public static string? NormalizeCodec(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "h265" or "h.265" or "x265" => "hevc",
        "h.264" or "avc" or "x264" => "h264",
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

    public static DeviceCapabilities WithoutFallbacks(DeviceCapabilities device) => device with
    {
        VideoCodecs = device.VideoCodecs.Where(codec => !VideoCodecs.Contains(codec)).ToArray(),
        VideoDecode = device.VideoDecode.Where(decode => decode.Hardware).ToArray(),
        AudioDecodeCodecs = device.AudioDecodeCodecs.Where(codec => !AudioCodecs.Contains(codec)).ToArray(),
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
        bool KeepVideo(string codec) => !route.SoftwareVideo || !VideoCodecs.Contains(codec);
        bool KeepAudio(string codec) => !route.SoftwareAudio || codec != route.AudioCodec;
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
