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
    public bool ProgressiveSupported { get; init; }
    public bool HlsSupported { get; init; } = true;
}

/// <summary>The measured baseline in docs/player-plan.md; installed converters are not playback proof.</summary>
public static class ConsoleEvidence
{
    /// <summary>Image subtitle codecs (silo-server's names) the overlay draws from the container.</summary>
    public static IReadOnlyList<string> EmbeddedBitmapSubtitleCodecs { get; } =
        Array.AsReadOnly(new[] { "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle" });

    public static DeviceCapabilities Default { get; } = new(
        Containers: Array.AsReadOnly(new[] { "mp4", "mkv" }), // Both verified; see docs/player-plan.md.
        VideoCodecs: Array.AsReadOnly(new[] { "h264", "hevc", "av1" }), // No VP9/WebM claim.
        // The system baseline plus bundled FFmpeg audio decode; no encoded output/passthrough promise.
        AudioDecodeCodecs: Array.AsReadOnly(SoftwareDecodePolicy.SystemAudioCodecs.Concat(SoftwareDecodePolicy.AudioCodecs).ToArray()),
        // TrueHD/DTS converters exist, but no receiver has verified passthrough yet.
        AudioPassthroughCodecs: [],
        MaxChannels: 8,
        Hdr: new(), // Unknown until the player reads the connected display; installed HEVC is not HDR evidence.
        // Bale's own overlay (bale_gpu.dll, libass) draws fetched text sidecars: ASS/SSA with their
        // styling and the server's font bundle, SubRip and WebVTT. It also draws image tracks the
        // FFmpeg demuxer decodes from a direct MKV (wrapper patch 0022), claimed per stream index.
        // PlaybackStartBuilder keeps that entry on original_http only. No .sup sidecar claim
        // (EmbeddedBitmap/SidecarBitmap), and text tracks keep their sidecar route.
        Subtitles: new()
        {
            SidecarText = true, AssStyling = true, FontAttachments = true,
            NativeEmbedded = Array.AsReadOnly(new[] { new NativeEmbeddedSubtitleCapability("mkv", EmbeddedBitmapSubtitleCodecs, "ffmpeg_stream_index") }),
        })
    {
        VideoDecode = BuildVideoDecode(),
        DisplayHdrEvidence = "unknown",
    };

    /// <summary>Measured decode claims; optional AV1 bounds let debug callers test unmeasured sizes.</summary>
    public static IReadOnlyList<VideoDecodeCapability> BuildVideoDecode(int av1MaxWidth = 1920, int av1MaxHeight = 1080, int av1MaxFps = 30)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(av1MaxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(av1MaxHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(av1MaxFps);
        // Mirror silo-apple's iosApp/iosApp/Shared/AppleDecodeCapabilities.swift bounded attestation.
        // The measured display tops out at 1080p (docs/player-plan.md), so keep system claims there.
        return Array.AsReadOnly(new[]
        {
            // H.264/AAC MP4 playback is verified on the console (docs/player-plan.md).
            new VideoDecodeCapability("h264", "Media Foundation", [], [], [8], 1920, 1080, 60, 25000, true),
            // HEVC MP4 playback, including Main10, is verified on the console (docs/player-plan.md).
            new VideoDecodeCapability("hevc", "Media Foundation", [], [], [8, 10], 1920, 1080, 60, 25000, true),
            // 0.1.230 Fight Club test: dav1d, AV1 Main 10-bit 1080p23.976, real time (~1.3 cores).
            // 8-bit clips also verified. Bound the claim to 1080p30; 4K software AV1 is untested.
            // Zero-copy takes only Main (4:2:0, 8/10-bit); High (4:4:4) and Professional (4:2:2, 12-bit)
            // transcode. The server lowercases ffprobe's "Main" and fails an empty or unknown profile,
            // so such streams transcode too.
            new VideoDecodeCapability("av1", "dav1d", ["main"], [], [8, 10], av1MaxWidth, av1MaxHeight, av1MaxFps, null, false),
        });
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
    public int ProtocolVersion { get; init; } = PlaybackProtocol.Version;
    public string? AppBuild { get; init; }
    public string? AppChannel { get; init; }
}
