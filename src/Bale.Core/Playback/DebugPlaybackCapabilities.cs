namespace Bale.Core.Playback;

/// <summary>An explicit experimental claim, never part of the measured console baseline.</summary>
public static class DebugPlaybackCapabilities
{
    // Shared by the opt-in CLI probe and DEBUG_SERVER player. This is not playback evidence.
    public static DeviceCapabilities Av1SoftwareClaim { get; } = ConsoleEvidence.Default with
    {
        VideoCodecs = [.. ConsoleEvidence.Default.VideoCodecs, "av1"],
        VideoDecode = [.. ConsoleEvidence.Default.VideoDecode,
            new("av1", "dav1d (debug claim)", [], [], [8, 10], 3840, 2160, null, null, false)],
    };
}
