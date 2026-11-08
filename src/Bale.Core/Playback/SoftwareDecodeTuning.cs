namespace Bale.Core.Playback;

/// <summary>Software-video defaults; wrapper-fork options are shared with debug probes.</summary>
public sealed record SoftwareDecodeTuning(int? DecodeAheadFrames = null, int? SwsThreads = null, int? Dav1dMaxFrameDelay = null)
{
    public const int DefaultBufferMs = 2000;

    public static int? BufferTimeMs(bool softwareVideo, int? overrideMs = null) =>
        overrideMs ?? (softwareVideo ? DefaultBufferMs : null);

    public static SoftwareDecodeTuning ForVideo(bool softwareVideo, long? width = null, long? height = null) =>
        softwareVideo ? new(16, 4, width is > 1920 || height is > 1080 ? 8 : null) : new();
}
