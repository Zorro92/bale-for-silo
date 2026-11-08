namespace Bale.Core.Playback;

/// <summary>Software-video defaults; wrapper-fork options are shared with debug probes.</summary>
public sealed record SoftwareDecodeTuning(int? DecodeAheadFrames = null, int? SwsThreads = null, int? Dav1dMaxFrameDelay = null)
{
    public const int DefaultBufferMs = 1000;
    public const int DefaultD3d11Upload = 1;
    // Keep dav1d synthesis in production; Rust grain is opt-in until console integration is verified.
    public const int DefaultFilmGrainMode = 0;

    public static int? FilmGrainMode(PlaybackRouteDecision route) =>
        route.SoftwareVideo ? DefaultFilmGrainMode : null;

    public static int? D3d11Upload(PlaybackRouteDecision route) =>
        route.SoftwareVideo ? DefaultD3d11Upload : null;

    public static int? BufferTimeMs(bool softwareVideo, int? overrideMs = null) =>
        overrideMs ?? (softwareVideo ? DefaultBufferMs : null);

    public static SoftwareDecodeTuning ForVideo(bool softwareVideo, long? width = null, long? height = null) =>
        softwareVideo ? new(16, 4, width is > 1920 || height is > 1080 ? 8 : null) : new();
}
