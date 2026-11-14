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
        // VP9 joins only when the console's decoder probe reports it (ForHardware). A .webm file
        // needs no container claim: silo-server's detectContainer (internal/scanner/probe.go)
        // names Matroska and WebM both "mkv".
        VideoCodecs: Array.AsReadOnly(new[] { "h264", "hevc", "av1" }),
        // The system baseline plus bundled FFmpeg audio decode; no encoded output/passthrough promise.
        AudioDecodeCodecs: Array.AsReadOnly(SoftwareDecodePolicy.SystemAudioCodecs.Concat(SoftwareDecodePolicy.AudioCodecs).ToArray()),
        // TrueHD/DTS converters exist, but no receiver has verified passthrough yet.
        AudioPassthroughCodecs: [],
        MaxChannels: 8,
        Hdr: new(), // Unknown until the player reads the connected display; installed HEVC is not HDR evidence.
        // Bale's own overlay (bale_gpu.dll, libass) draws fetched text sidecars: ASS/SSA with their
        // styling and the server's font bundle, SubRip and WebVTT. It also draws image tracks the
        // FFmpeg demuxer decodes from a direct MKV (wrapper patch 0022), claimed per stream index.
        // PlaybackStartBuilder keeps native_embedded on original_http and opts packaged deliveries
        // into PGS .sup windows. The server gates embedded_bitmap to embedded PGS alone.
        Subtitles: new()
        {
            SidecarText = true, AssStyling = true, FontAttachments = true, EmbeddedBitmap = true,
            NativeEmbedded = Array.AsReadOnly(new[] { new NativeEmbeddedSubtitleCapability("mkv", EmbeddedBitmapSubtitleCodecs, "ffmpeg_stream_index") }),
        })
    {
        VideoDecode = BuildVideoDecode(),
        DisplayHdrEvidence = "unknown",
        OriginalAudioTrackSelectionSupported = true,
        OriginalDolbyVision8BaseLayerSupported = true,
    };

    /// <summary>
    /// The production claims for what the console's hardware decoders report. Null or empty limits
    /// (the probe failed or found nothing) give <see cref="Default"/>, the fixed 1080p baseline.
    /// </summary>
    public static DeviceCapabilities ForHardware(HardwareDecodeLimits? hardware)
    {
        if (hardware is null || hardware.IsEmpty) return Default;
        var decode = BuildVideoDecode(hardware);
        return Default with
        {
            // A codec is named only while it has a decode entry, so VP9 appears only when probed.
            VideoCodecs = decode.Select(claim => claim.Codec).Distinct().ToArray(),
            VideoDecode = decode,
        };
    }

    /// <summary>The fixed baseline claims; optional AV1 bounds let debug callers test unmeasured sizes.</summary>
    public static IReadOnlyList<VideoDecodeCapability> BuildVideoDecode(int av1MaxWidth = 1920, int av1MaxHeight = 1080) =>
        BuildVideoDecode(null, av1MaxWidth, av1MaxHeight);

    /// <summary>
    /// Hardware claims from the decoder probe plus the measured software AV1 claim. Null or empty
    /// limits keep the fixed H.264/HEVC 1080p baseline with no VP9.
    /// </summary>
    public static IReadOnlyList<VideoDecodeCapability> BuildVideoDecode(HardwareDecodeLimits? hardware,
        int av1MaxWidth = 1920, int av1MaxHeight = 1080)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(av1MaxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(av1MaxHeight);
        // Mirror silo-apple's iosApp/iosApp/Shared/AppleDecodeCapabilities.swift bounded attestation.
        var claims = new List<VideoDecodeCapability>();
        if (hardware is null || hardware.IsEmpty)
        {
            // No decoder report to go on: the sizes MP4 playback was verified at on the console
            // (docs/player-plan.md), H.264 8-bit and HEVC 8/10-bit.
            claims.Add(new("h264", "Media Foundation", [], [], [8], 1920, 1080, 60, 25000, true));
            claims.Add(new("hevc", "Media Foundation", [], [], [8, 10], 1920, 1080, 60, 25000, true));
        }
        else
        {
            // The hardware decoder's own report is the limit (docs/player-plan.md). Display fit is
            // Auto's version choice (DisplayVersionMatch), never a smaller claim.
            AddHardware("h264", hardware.H264, null);
            AddHardware("hevc", hardware.HevcMain, hardware.HevcMain10);
            AddHardware("vp9", hardware.Vp9Profile0, hardware.Vp9Profile2);
            // FFmpegInteropX's compressed providers feed WVC1/MPEG2 to Media Foundation, like
            // VP9. Only the console's matching NV12 VLD profile admits these original-file paths.
            AddHardware("vc1", hardware.Vc1, null);
            AddHardware("mpeg2video", hardware.Mpeg2, null);
        }
        // dav1d zero-copy, measured in real time: 0.1.230 Fight Club 1080p23.976 10-bit (~1.3 cores)
        // and 0.1.439 SmackDown 1080p59.94 10-bit (0 late frames, ~3 cores); 8-bit clips too.
        // Zero-copy takes only Main (4:2:0, 8/10-bit); High (4:4:4) and Professional (4:2:2, 12-bit)
        // transcode. The server lowercases ffprobe's "Main" and fails an empty or unknown profile,
        // so such streams transcode too.
        claims.Add(Av1(Math.Min(av1MaxWidth, 1920), Math.Min(av1MaxHeight, 1080), 60));
        // A larger debug bound stays at 30 fps. Platform-attested rates are absolute (the server
        // trades size for rate only on exact evidence), and it takes a source any entry admits.
        if (av1MaxWidth > 1920 || av1MaxHeight > 1080) claims.Add(Av1(av1MaxWidth, av1MaxHeight, 30));
        return claims.AsReadOnly();

        // One entry per codec at the probed size. If the 8-bit and 10-bit profiles report different
        // sizes, each depth gets its own entry so neither is overclaimed; the server takes a source
        // any entry admits.
        void AddHardware(string codec, DecodeSize? eightBit, DecodeSize? tenBit)
        {
            if (eightBit is { } both && eightBit == tenBit) claims.Add(Hardware(codec, [8, 10], both));
            else
            {
                if (eightBit is { } low) claims.Add(Hardware(codec, [8], low));
                if (tenBit is { } high) claims.Add(Hardware(codec, [10], high));
            }
        }

        // 60 fps: Microsoft's Xbox guidance has 60 Hz media modes and no 120 Hz ones
        // (learn.microsoft.com/windows/uwp/audio-video-camera/hevc-xbox). No bitrate bound: the
        // decoder reports none, and silo-server's videoDecodeTakesSourceV3 (capabilities_v3.go)
        // reads an absent max_bitrate_kbps as unconstrained.
        // No profile list. Under Bale's platform_attested evidence silo-server's videoEligibleV3
        // skips profile and level lists on hardware entries, so "main"/"main 10" or
        // "profile 0"/"profile 2" would not keep HEVC RExt (Main 4:2:2 10) or VP9 profiles 1 and 3
        // out. Only the exact tier reads them, by plain lowercase equality with ffprobe's name, and
        // there a file with no recorded profile would be refused. Bale's own version matching
        // (PlaybackFormatting.Decodes) does read lists, so adding one would also make it disagree
        // with the server. Bit depth is the filter the server applies.
        static VideoDecodeCapability Hardware(string codec, int[] bitDepths, DecodeSize size) =>
            new(codec, "Media Foundation", [], [], bitDepths, size.Width, size.Height, 60, null, true);

        static VideoDecodeCapability Av1(int width, int height, int fps) =>
            new("av1", "dav1d", ["main"], [], [8, 10], width, height, fps, null, false);
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
