namespace Bale.Core.Playback;

/// <summary>Software-video defaults; wrapper-fork options are shared with debug probes.</summary>
public sealed record SoftwareDecodeTuning(int? DecodeAheadFrames = null, int? Dav1dMaxFrameDelay = null)
{
    public const int DefaultBufferMs = 1000;
    // Above 1080p each decoded P010 frame is ~25 MB, so the 1 s MSS buffer plus 16 decode-ahead frames
    // held ~1.1 GB. The 4K console A/B (Brawl in Cell Block 99) measured 500 ms + 8 frames at ~460 MiB
    // less with zero late frames and the same post-seek refill speed.
    public const int Above1080pBufferMs = 500;
    public const int DefaultDecodeAheadFrames = 16;
    public const int Above1080pDecodeAheadFrames = 8;
    // Zero-copy retune (input-idle console A/B, Brawl and Shrek 4K): dav1d frame delay 6 holds ~190-200
    // MiB less than 8 with the same CPU and stalls; 5 and 4 save more but stall noticeably more in heavy
    // scenes. Trimming decode-ahead or BufferTime further also added stalls.
    public const int Above1080pDav1dMaxFrameDelay = 6;
    // Owner, 2026-11-19: dav1d's own CPU grain on D3D11, with CPU NV12/P010 output.
    // Fork modes: 0 = dav1d applies grain, 2 = GPU grain (zero-copy streams only).
    // Export-only bale_dav1d_export_film_grain=1 overrides either mode for grain=off.
    public const int FilmGrainMode = 0;

    public static int? BufferTimeMs(bool softwareVideo, int? overrideMs = null, long? width = null, long? height = null) =>
        overrideMs ?? (softwareVideo ? (Above1080p(width, height) ? Above1080pBufferMs : DefaultBufferMs) : null);

    public static SoftwareDecodeTuning ForVideo(bool softwareVideo, long? width = null, long? height = null) =>
        !softwareVideo ? new()
        : Above1080p(width, height) ? new(Above1080pDecodeAheadFrames, Above1080pDav1dMaxFrameDelay)
        : new(DefaultDecodeAheadFrames, null);

    /// <summary>Unknown dimensions count as not above 1080p, keeping the measured 1080p defaults.</summary>
    public static bool Above1080p(long? width, long? height) => width is > 1920 || height is > 1080;
}
