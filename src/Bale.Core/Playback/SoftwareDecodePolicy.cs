using System.Text.Json;
using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// Bundled decoder policy, separate from the console's measured system decoders. Implemented in Rust
/// (native/bale-core/src/software_decode.rs), which holds the lists and the reasons behind them.
/// </summary>
public static class SoftwareDecodePolicy
{
    private static readonly SoftwareDecodeLists Lists =
        BaleCoreNative.Call("software_decode.lists", (string?)null, BaleCoreJson.Default.String, BaleCoreJson.Default.SoftwareDecodeLists);

    /// <summary>Software-only video codecs: those no console hardware decoder takes (AV1, MPEG-4 Part 2, VP8, WMV, …).</summary>
    public static IReadOnlyList<string> VideoCodecs { get; } = Array.AsReadOnly(Lists.VideoCodecs);

    /// <summary>
    /// Every video codec Bale declares from its FFmpeg build (owner, 2026-11-17): hardware codecs decode
    /// in FFmpeg when the console's decoder cannot take the file.
    /// </summary>
    public static IReadOnlyList<string> FfmpegVideoCodecs { get; } = Array.AsReadOnly(Lists.FfmpegVideoCodecs);

    /// <summary>What the server may package (progressive fMP4, HLS) for Bale: the codecs verified in packages.</summary>
    public static IReadOnlyList<string> PackagedVideoCodecs { get; } = Array.AsReadOnly(Lists.PackagedVideoCodecs);

    public static IReadOnlyList<string> FfmpegOnlyContainers { get; } = Array.AsReadOnly(Lists.FfmpegOnlyContainers);

    /// <summary>Codecs the system (hardware) decoders may take, given the baseline or the decoder probe.</summary>
    public static IReadOnlyList<string> SystemVideoCodecs { get; } = Array.AsReadOnly(Lists.SystemVideoCodecs);

    /// <summary>Hardware codecs that play only through FFmpeg's demuxer feeding the system decoder.</summary>
    public static IReadOnlyList<string> DemuxedVideoCodecs { get; } = Array.AsReadOnly(Lists.DemuxedVideoCodecs);

    /// <summary>Measured Media Foundation audio playback, not merely installed decoder registrations.</summary>
    public static IReadOnlyList<string> SystemAudioCodecs { get; } = Array.AsReadOnly(Lists.SystemAudioCodecs);

    /// <summary>FFmpeg audio decode (not passthrough), in the server's exact spellings including each pcm_*.</summary>
    public static IReadOnlyList<string> AudioCodecs { get; } = Array.AsReadOnly(Lists.AudioCodecs);

    /// <summary>
    /// Trimmed, lower-cased codec name with common aliases folded (h265/x265 → hevc, avc → h264,
    /// every pcm_* → pcm); null for a missing or blank name.
    /// </summary>
    public static string? NormalizeCodec(string? codec) => codec is null
        ? null
        : BaleCoreNative.Call("software_decode.normalize_codec", codec, BaleCoreJson.Default.String,
            BaleCoreJson.Default.String);

    /// <summary>
    /// Software video is measured only on Xbox Series (Zen 2 with AVX2): Xbox One consoles and any
    /// CPU without AVX2 claim none, and the server transcodes. An unknown device form keeps the claim.
    /// </summary>
    public static bool SoftwareVideoSupported(bool avx2, string? deviceForm) =>
        BaleCoreNative.Call("software_decode.video_supported", writer =>
        {
            writer.WriteBoolean("avx2", avx2);
            writer.Text("device_form", deviceForm);
        }, BaleCoreJson.Default.Boolean);

    /// <summary>Drops only the bundled software video claims; audio fallbacks stay.</summary>
    public static DeviceCapabilities WithoutSoftwareVideo(DeviceCapabilities device) =>
        FilterDevice(device, "without_software_video");

    /// <summary>Software AV1 uses D3D11 CPU buffers and grain; no D3D12 or zero-copy capability is needed.</summary>
    public static DeviceCapabilities ForSoftwareVideo(DeviceCapabilities device, bool supported) =>
        FilterDevice(device, "for_software_video", writer =>
        {
            writer.WriteBoolean("supported", supported);
        });

    /// <summary>
    /// Drops every claim that needs FFmpeg: software video, demuxer-only video and fallback audio,
    /// and the executors built on it (DV base layers, dynamic range mapping, progressive).
    /// </summary>
    public static DeviceCapabilities WithoutFallbacks(DeviceCapabilities device) => FilterDevice(device, "without_fallbacks");

    private static DeviceCapabilities FilterDevice(DeviceCapabilities device, string filter, Action<Utf8JsonWriter>? options = null)
    {
        var result = BaleCoreNative.Call("software_decode.filter_device", writer =>
        {
            writer.WriteStartObject("device");
            writer.Strings("video_codecs", device.VideoCodecs);
            writer.VideoDecode("video_decode", device.VideoDecode);
            writer.Strings("audio_decode_codecs", device.AudioDecodeCodecs);
            writer.Strings("containers", device.Containers);
            writer.WriteEndObject();
            writer.WriteString("filter", filter);
            options?.Invoke(writer);
        }, BaleCoreJson.Default.FilteredDevice);
        var filtered = device with
        {
            VideoCodecs = result.VideoCodecs,
            VideoDecode = result.VideoDecode.Select(index => device.VideoDecode[index]).ToArray(),
            AudioDecodeCodecs = result.AudioDecodeCodecs,
            Containers = result.Containers,
        };
        return result.DropFfmpegExecutors
            ? filtered with
            {
                OriginalDolbyVision8BaseLayerSupported = false,
                OriginalDolbyVision7BaseLayerSupported = false,
                OriginalDynamicRangeMappingSupported = false,
                ProgressiveSupported = false,
                HlsFfmpegSupported = false,
            }
            : filtered;
    }

    /// <summary>
    /// Checks the opened source's video streams against the route. Every stream must use the route's
    /// decoder (system decoder, or FFmpeg software for an allowlisted codec), and a software-video route
    /// must have at least one video stream: the server planned a software decode, so a source without
    /// video is a failed open that must replan to a server transcode, never silent audio-only playback.
    /// When supplied, softwareCodec also keeps AV1 and ordinary MPEG-4 output separate.
    /// </summary>
    public static bool VideoDecodersAllowed(bool softwareVideo,
        IReadOnlyCollection<(string? Codec, bool SoftwareDecoder, bool SystemDecoder)> streams, string? softwareCodec = null) =>
        BaleCoreNative.Call("software_decode.video_decoders_allowed", writer =>
        {
            writer.WriteBoolean("software_video", softwareVideo);
            writer.WriteStartArray("streams");
            foreach (var stream in streams)
            {
                writer.WriteStartObject();
                writer.Text("codec", stream.Codec);
                writer.WriteBoolean("software_decoder", stream.SoftwareDecoder);
                writer.WriteBoolean("system_decoder", stream.SystemDecoder);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.Text("software_codec", softwareCodec);
        }, BaleCoreJson.Default.Boolean);

    /// <summary>
    /// The replan after a failed FFmpeg route: withdraws the claims that route depended on (software
    /// video, its demuxer-only codec, its FFmpeg audio, FFmpeg-only containers, a DV8 base layer).
    /// </summary>
    internal static PlaybackStartRequestV3 WithoutFailedFallbacks(PlaybackStartRequestV3 start, PlaybackRouteDecision route)
    {
        var capabilities = start.ClientCapabilities;
        var deliveries = start.ClientPlaybackContext.Deliveries.ToArray();
        var result = BaleCoreNative.Call("software_decode.without_failed_fallbacks", writer =>
        {
            writer.WriteStartObject("start");
            writer.Strings("codecs_video", capabilities.CodecsVideo);
            writer.Strings("codecs_video_hardware", capabilities.CodecsVideoHardware);
            writer.VideoDecode("video_decode", capabilities.VideoDecode);
            writer.Strings("codecs_audio", capabilities.CodecsAudio);
            writer.Strings("containers", capabilities.Containers);
            writer.WriteStartArray("deliveries");
            foreach (var (key, delivery) in deliveries)
            {
                writer.WriteStartObject();
                writer.WriteString("key", key);
                writer.Strings("video_codecs", delivery.VideoCodecs);
                writer.Strings("audio_decode_codecs", delivery.AudioDecodeCodecs);
                writer.Strings("containers", delivery.Containers);
                writer.Strings("validated_claims", delivery.ValidatedClaims);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.Strings("client_features", start.ClientFeatures);
            writer.WriteEndObject();
            writer.WriteStartObject("route");
            writer.WriteBoolean("software_video", route.SoftwareVideo);
            writer.WriteBoolean("software_audio", route.SoftwareAudio);
            writer.Text("video_codec", route.VideoCodec);
            writer.Text("audio_codec", route.AudioCodec);
            writer.WriteBoolean("dolby_vision_base_layer", route.DolbyVisionBaseLayer);
            writer.WriteEndObject();
        }, BaleCoreJson.Default.FilteredStart);
        return start with
        {
            ClientCapabilities = capabilities with
            {
                CodecsVideo = result.CodecsVideo,
                CodecsVideoHardware = result.CodecsVideoHardware,
                VideoDecode = result.VideoDecode.Select(index => capabilities.VideoDecode[index]).ToArray(),
                CodecsAudio = result.CodecsAudio,
                Containers = result.Containers,
            },
            ClientPlaybackContext = start.ClientPlaybackContext with
            {
                Deliveries = deliveries.Zip(result.Deliveries).ToDictionary(pair => pair.First.Key, pair => pair.First.Value with
                {
                    VideoCodecs = pair.Second.VideoCodecs,
                    AudioDecodeCodecs = pair.Second.AudioDecodeCodecs,
                    Containers = pair.Second.Containers,
                    ValidatedClaims = pair.Second.ValidatedClaims,
                }),
            },
            ClientFeatures = result.ClientFeatures,
        };
    }
}
