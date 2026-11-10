namespace Bale.Core.Playback;

/// <summary>Software-video defaults; wrapper-fork options are shared with debug probes.</summary>
public sealed record SoftwareDecodeTuning(int? DecodeAheadFrames = null, int? SwsThreads = null, int? Dav1dMaxFrameDelay = null)
{
    public const int DefaultBufferMs = 1000;
    // Above 1080p each decoded P010 frame is ~25 MB, so the 1 s MSS buffer plus 16 decode-ahead frames
    // held ~1.1 GB. The 4K console A/B (Brawl in Cell Block 99) measured 500 ms + 8 frames at ~460 MiB
    // less with zero late frames and the same post-seek refill speed; frame delay stays 8 because 4
    // refilled about three times slower.
    public const int Above1080pBufferMs = 500;
    public const int DefaultDecodeAheadFrames = 16;
    public const int Above1080pDecodeAheadFrames = 8;
    // Variant 6 (RT|SRV|UAV, shared NT handle) lets the zero-copy repack write the pooled texture
    // directly, grain fused (wrapper 0017/0018), skipping the landing copy and D3D11 grain pass.
    // 2026-11-09 interleaved 4K console A/B vs variant 1: ~0.15-0.2 cores less on Shrek and Brawl,
    // zero late frames both ways.
    public const int DefaultD3d11Upload = 6;
    // GPU grain is the production default since console step 5. The wrapper keeps dav1d when GPU
    // is unavailable at open; runtime GPU loss uses CPU grain through 1080p or no grain above 1080p.
    public const int DefaultFilmGrainMode = 2;

    public static int? FilmGrainMode(PlaybackRouteDecision route) =>
        route.SoftwareVideo ? DefaultFilmGrainMode : null;

    public static int? D3d11Upload(PlaybackRouteDecision route) =>
        route.SoftwareVideo ? DefaultD3d11Upload : null;

    // Zero-copy (wrapper patch 0015) is the production default since the 2026-11-09 console A/B:
    // dav1d decodes 10-bit AV1 into D3D12 buffers and the GPU repacks them, replacing swscale.
    // Needs upload variant 1 or 6; the wrapper still declines pictures it can't take (8-bit for now).
    public static bool ZeroCopy(PlaybackRouteDecision route) => route.SoftwareVideo;

    public static int? BufferTimeMs(bool softwareVideo, int? overrideMs = null, long? width = null, long? height = null) =>
        overrideMs ?? (softwareVideo ? (Above1080p(width, height) ? Above1080pBufferMs : DefaultBufferMs) : null);

    public static SoftwareDecodeTuning ForVideo(bool softwareVideo, long? width = null, long? height = null) =>
        !softwareVideo ? new()
        : Above1080p(width, height) ? new(Above1080pDecodeAheadFrames, 4, 8)
        : new(DefaultDecodeAheadFrames, 4, null);

    /// <summary>Unknown dimensions count as not above 1080p, keeping the measured 1080p defaults.</summary>
    public static bool Above1080p(long? width, long? height) => width is > 1920 || height is > 1080;
}
