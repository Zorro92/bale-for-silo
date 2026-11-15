using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// Platform evidence, supplied by the player rather than inferred from a platform name.
/// android-shared/src/androidMain/kotlin/org/siloserver/silo/common/player/PlaybackCapabilityDetector.kt
/// keeps decode and current-sink passthrough lists separate.
/// </summary>
public sealed record DeviceCapabilities(
    IReadOnlyList<string> Containers,
    IReadOnlyList<string> VideoCodecs,
    IReadOnlyList<string> AudioDecodeCodecs,
    IReadOnlyList<string> AudioPassthroughCodecs,
    int MaxChannels,
    HdrCapabilities Hdr,
    DeliverySubtitleCapabilities Subtitles)
{
    // Codec names alone are not the exact profile/level tuples an "exact" claim requires.
    public string VideoEvidence { get; init; } = "platform_attested";
    public string AudioEvidence { get; init; } = "platform_attested";
    public IReadOnlyList<string> HardwareVideoCodecs { get; init; } = [];
    public IReadOnlyList<VideoDecodeCapability> VideoDecode { get; init; } = [];
    public IReadOnlyList<AudioPassthroughEntry> PassthroughEntries { get; init; } = [];
    public string DisplayHdrEvidence { get; init; } = "unknown";
    public bool OriginalHttpSupported { get; init; } = true;
    /// <summary>The original-file executor maps, selects and verifies catalog audio before Play.</summary>
    public bool OriginalAudioTrackSelectionSupported { get; init; }
    /// <summary>
    /// The original-file executor plays a Dolby Vision profile 8 file's base layer: the FFmpeg demuxer
    /// feeds the system HEVC decoder, which ignores the RPU NAL units (type 62, unspecified in HEVC).
    /// </summary>
    public bool OriginalDolbyVision8BaseLayerSupported { get; init; }
    /// <summary>FFmpeg HEVC/d3d11va presents only DV7's HDR10 base layer, ignoring RPU and EL pixels.</summary>
    public bool OriginalDolbyVision7BaseLayerSupported { get; init; }
    /// <summary>The pinned wrapper and bale_gpu tone-map ABI are available; eligibility is still per selected version.</summary>
    public bool OriginalDynamicRangeMappingSupported { get; init; }
    public bool ProgressiveSupported { get; init; }
    public bool HlsSupported { get; init; } = true;
}

/// <summary>
/// The measured baseline in docs/player-plan.md; installed converters are not playback proof.
/// Implemented in Rust (native/bale-core/src/evidence.rs), which keeps the measurements behind each claim.
/// </summary>
public static class ConsoleEvidence
{
    /// <summary>Image subtitle codecs (silo-server's names) the overlay draws from the container.</summary>
    public static IReadOnlyList<string> EmbeddedBitmapSubtitleCodecs { get; } = Array.AsReadOnly(BaleCoreNative.Call(
        "evidence.embedded_bitmap_subtitle_codecs", (string?)null, BaleCoreJson.Default.String, BaleCoreJson.Default.StringArray));

    /// <summary>
    /// The fixed baseline: MP4/MKV/AVI, H.264/HEVC at 1080p plus AV1 and MPEG-4 software, system and
    /// FFmpeg audio decode, no passthrough, HDR unknown, Bale's subtitle overlay and the original-file
    /// executors (audio selection, DV base layers, progressive).
    /// </summary>
    public static DeviceCapabilities Default { get; } =
        BaleCoreNative.Call("evidence.baseline", (string?)null, BaleCoreJson.Default.String, BaleCoreJson.Default.DeviceCapabilities);

    /// <summary>
    /// The production claims for what the console's hardware decoders report. Null or empty limits
    /// (the probe failed or found nothing) give <see cref="Default"/>, the fixed 1080p baseline.
    /// </summary>
    public static DeviceCapabilities ForHardware(HardwareDecodeLimits? hardware)
    {
        var claims = BaleCoreNative.Call("evidence.for_hardware", writer =>
            writer.Record("hardware", hardware, BaleCoreJson.Default.HardwareDecodeLimits), BaleCoreJson.Default.HardwareClaims);
        return claims is null ? Default : Default with { VideoCodecs = claims.VideoCodecs, VideoDecode = claims.VideoDecode };
    }

    /// <summary>The fixed baseline claims; optional AV1 bounds let debug callers test unmeasured sizes.</summary>
    public static IReadOnlyList<VideoDecodeCapability> BuildVideoDecode(int av1MaxWidth = 1920, int av1MaxHeight = 1080) =>
        BuildVideoDecode(null, av1MaxWidth, av1MaxHeight);

    /// <summary>
    /// Hardware claims from the decoder probe (each codec at its reported size, 60 fps, by bit depth)
    /// plus the measured AV1 (dav1d Main, 1080p60) and MPEG-4 Part 2 (1080p30) software claims. Null
    /// or empty limits keep the fixed H.264/HEVC 1080p baseline with no VP9.
    /// </summary>
    public static IReadOnlyList<VideoDecodeCapability> BuildVideoDecode(HardwareDecodeLimits? hardware,
        int av1MaxWidth = 1920, int av1MaxHeight = 1080)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(av1MaxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(av1MaxHeight);
        return Array.AsReadOnly(BaleCoreNative.Call("evidence.video_decode", writer =>
        {
            writer.Record("hardware", hardware, BaleCoreJson.Default.HardwareDecodeLimits);
            writer.WriteNumber("av1_max_width", av1MaxWidth);
            writer.WriteNumber("av1_max_height", av1MaxHeight);
        }, BaleCoreJson.Default.VideoDecodeCapabilityArray));
    }
}

// Wire shapes from shared/src/commonMain/kotlin/org/siloserver/silo/model/playback/PlaybackModels.kt.
public sealed record VideoDecodeCapability(
    string Codec,
    string? DecoderName,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<int> Levels,
    IReadOnlyList<int> BitDepths,
    int? MaxWidth,
    int? MaxHeight,
    double? MaxFrameRate,
    int? MaxBitrateKbps,
    bool Hardware);

public sealed record HdrCapabilities
{
    public bool Hdr10 { get; init; }
    public bool Hdr10Plus { get; init; }
    public bool Hlg { get; init; }
    public IReadOnlyList<int> DolbyVisionProfiles { get; init; } = [];
}

public sealed record AudioPassthroughEntry(string Codec, IReadOnlyList<int> ChannelCounts, IReadOnlyList<string> Layouts);

public sealed record AudioPassthroughCapabilities(
    IReadOnlyList<string> PassthroughCodecs,
    int MaxChannels,
    IReadOnlyList<AudioPassthroughEntry> Entries)
{
    public bool SpatializerEnabled { get; init; }
}

public sealed record NativeEmbeddedSubtitleCapability(string Container, IReadOnlyList<string> Codecs, string TrackIdentity)
{
    public bool AssStyling { get; init; }
    public bool FontAttachments { get; init; }
}

public sealed record DeliverySubtitleCapabilities
{
    public IReadOnlyList<NativeEmbeddedSubtitleCapability> NativeEmbedded { get; init; } = [];
    public bool EmbeddedText { get; init; }
    public bool SidecarText { get; init; }
    public bool AssStyling { get; init; }
    public bool EmbeddedBitmap { get; init; }
    public bool SidecarBitmap { get; init; }
    public bool FontAttachments { get; init; }
}

public sealed record ClientCodecCapabilities(
    string VideoEvidence,
    string AudioEvidence,
    IReadOnlyList<string> CodecsVideo,
    IReadOnlyList<string> CodecsVideoHardware,
    IReadOnlyList<VideoDecodeCapability> VideoDecode,
    IReadOnlyList<string> CodecsAudio,
    IReadOnlyList<string> Containers,
    bool Hdr,
    HdrCapabilities HdrDetails,
    AudioPassthroughCapabilities AudioPassthrough);

public sealed record PlaybackDeviceContext(string Platform)
{
    public string? OsVersion { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public IReadOnlyDictionary<string, string> PlatformDetails { get; init; } = new Dictionary<string, string>();
}

public sealed record PlaybackOutputDisplay(string HdrEvidence, HdrCapabilities HdrTypes);

public sealed record PlaybackOutputContext(HdrCapabilities HdrDetails, AudioPassthroughCapabilities AudioPassthrough)
{
    public string CurrentSink { get; init; } = "local_output";
    public string? SinkType { get; init; }
    public string? OutputContextId { get; init; }
    public PlaybackOutputDisplay? Display { get; init; }
}

public sealed record DeliveryCapability
{
    public bool Enabled { get; init; } = true;
    public bool SupportedOnDevice { get; init; } = true;
    public string? FailureReason { get; init; }
    public IReadOnlyList<string> Containers { get; init; } = [];
    public IReadOnlyList<string> VideoCodecs { get; init; } = [];
    public IReadOnlyList<string> AudioDecodeCodecs { get; init; } = [];
    public IReadOnlyList<string> AudioPassthroughCodecs { get; init; } = [];
    public int MaxChannels { get; init; }
    public HdrCapabilities HdrDetails { get; init; } = new();
    public DeliverySubtitleCapabilities Subtitles { get; init; } = new();
    public IReadOnlyList<string> Features { get; init; } = [];
    public IReadOnlyList<PlaybackTransformation> Transformations { get; init; } = [];
    public bool AuthHeaderRefresh { get; init; }
    public IReadOnlyList<string> ValidatedClaims { get; init; } = [];
}

public sealed record PlaybackTransformation(string Name, string Executor = "server", string RecipeVersion = "1")
{
    public IReadOnlyList<string> ValidatedClaims { get; init; } = [];
}

public sealed record ClientPlaybackContext(
    string FormFactor,
    string AppVersion,
    PlaybackDeviceContext Device,
    PlaybackOutputContext Output,
    IReadOnlyDictionary<string, DeliveryCapability> Deliveries)
{
    /// <summary>
    /// silo-server rejects the whole request (422) when a client-executed transformation sits on a
    /// delivery that is not enabled and supported (protocol_v3.go "client transformation capability is
    /// not enabled"). Replans and debug forces disable deliveries; drop their client recipes on the way out.
    /// </summary>
    public ClientPlaybackContext WithoutInactiveClientTransformations() =>
        Deliveries.Any(pair => !(pair.Value.Enabled && pair.Value.SupportedOnDevice) && pair.Value.Transformations.Any(IsClient))
            ? this with
            {
                Deliveries = Deliveries.ToDictionary(pair => pair.Key, pair => pair.Value.Enabled && pair.Value.SupportedOnDevice
                    ? pair.Value
                    : pair.Value with { Transformations = pair.Value.Transformations.Where(t => !IsClient(t)).ToArray() }),
            }
            : this;

    private static bool IsClient(PlaybackTransformation transformation) =>
        string.Equals(transformation.Executor?.Trim(), "client", StringComparison.OrdinalIgnoreCase);

    public int ProtocolVersion { get; init; } = PlaybackProtocol.Version;
    public string? AppBuild { get; init; }
    public string? AppChannel { get; init; }
}
