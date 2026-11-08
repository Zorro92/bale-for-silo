namespace Bale.Core.Playback;

/// <summary>Software-video defaults; wrapper-fork options are shared with debug probes.</summary>
public sealed record SoftwareDecodeTuning(int? DecodeAheadFrames = null, int? SwsThreads = null, int? Dav1dMaxFrameDelay = null)
{
    public const int DefaultBufferMs = 1000;
    public const int DefaultD3d11Upload = 1;
    // GPU grain is the production default since console step 5. The wrapper keeps dav1d when GPU
    // is unavailable at open; runtime GPU loss uses CPU grain through 1080p or no grain above 1080p.
    public const int DefaultFilmGrainMode = 2;

    public static int? FilmGrainMode(PlaybackRouteDecision route) =>
        route.SoftwareVideo ? DefaultFilmGrainMode : null;

    public static int? D3d11Upload(PlaybackRouteDecision route) =>
        route.SoftwareVideo ? DefaultD3d11Upload : null;

    public static int? BufferTimeMs(bool softwareVideo, int? overrideMs = null) =>
        overrideMs ?? (softwareVideo ? DefaultBufferMs : null);

    public static SoftwareDecodeTuning ForVideo(bool softwareVideo, long? width = null, long? height = null) =>
        softwareVideo ? new(16, 4, width is > 1920 || height is > 1080 ? 8 : null) : new();
}
