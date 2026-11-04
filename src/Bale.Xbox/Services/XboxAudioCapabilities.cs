using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

internal static class XboxAudioCapabilities
{
    // PlaybackCapabilityDetector.kt feeds TrackSelectionPresets.kt the same decode snapshot used
    // for planning. Bundled FFmpeg audio is now a direct path too, not encoded passthrough evidence.
    // Let the server adapt the selected track when the debug override disables that fallback.
    public static ClientAudioCapabilities Current { get; } = new(ConsoleEvidence.Default.AudioDecodeCodecs);
}
