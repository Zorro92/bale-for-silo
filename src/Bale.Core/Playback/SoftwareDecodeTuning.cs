namespace Bale.Core.Playback;

/// <summary>Wrapper-fork defaults shared by production software video and debug probes.</summary>
public sealed record SoftwareDecodeTuning(int? DecodeAheadFrames = null, int? SwsThreads = null, int? Dav1dMaxFrameDelay = null)
{
    public static SoftwareDecodeTuning ForVideo(bool softwareVideo, long? width = null, long? height = null) =>
        softwareVideo ? new(16, 4, width is > 1920 || height is > 1080 ? 8 : null) : new();
}
