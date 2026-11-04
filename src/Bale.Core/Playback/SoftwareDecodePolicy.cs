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

    public static DeviceCapabilities WithoutFallbacks(DeviceCapabilities device) => device with
    {
        VideoCodecs = device.VideoCodecs.Where(codec => !VideoCodecs.Contains(codec)).ToArray(),
        VideoDecode = device.VideoDecode.Where(decode => decode.Hardware).ToArray(),
        AudioDecodeCodecs = device.AudioDecodeCodecs.Where(codec => !AudioCodecs.Contains(codec)).ToArray(),
    };

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
