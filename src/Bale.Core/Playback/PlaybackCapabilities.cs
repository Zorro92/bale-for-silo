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
    public IReadOnlyList<AudioPassthroughEntry> PassthroughEntries { get; init; } = [];
    public string DisplayHdrEvidence { get; init; } = "unknown";
    public bool OriginalHttpSupported { get; init; } = true;
    public bool ProgressiveSupported { get; init; }
    public bool HlsSupported { get; init; } = true;
}

/// <summary>The measured baseline in docs/player-plan.md; installed converters are not playback proof.</summary>
public static class ConsoleEvidence
{
    public static DeviceCapabilities Default { get; } = new(
        Containers: Array.AsReadOnly(new[] { "mp4", "mkv" }), // MKV still pending verification; see docs/player-plan.md.
        VideoCodecs: Array.AsReadOnly(new[] { "h264", "hevc" }), // No AV1 or VP9/WebM claim.
        // FLAC/Opus in MP4 failed; leave them out until verified in MKV (docs/player-plan.md).
        AudioDecodeCodecs: Array.AsReadOnly(new[] { "aac", "ac3", "eac3", "mp3", "pcm" }),
        // TrueHD/DTS converters exist, but no receiver has verified passthrough yet.
        AudioPassthroughCodecs: [],
        MaxChannels: 8,
        Hdr: new(), // The measured display reports SDR only, regardless of installed HEVC decoding.
        Subtitles: new()) // No subtitle rendering has been measured yet; permit server burn-in instead.
    {
        DisplayHdrEvidence = "exact",
    };
}

// Wire shapes from shared/src/commonMain/kotlin/org/siloserver/silo/model/playback/PlaybackModels.kt.
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
    public int ProtocolVersion { get; init; } = PlaybackProtocol.Version;
    public string? AppBuild { get; init; }
    public string? AppChannel { get; init; }
}
