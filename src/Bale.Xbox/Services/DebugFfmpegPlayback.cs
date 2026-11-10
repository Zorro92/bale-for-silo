#if DEBUG_SERVER
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only diagnostic override, captured once per attempt. Production policy is the default.
/// direct: claim the production capabilities, but demux every direct (non-HLS) mount with FFmpeg,
/// keeping system (hardware) video decode, to A/B against Media Foundation's own container sources.
/// </summary>
internal static class DebugFfmpegPlayback
{
    private static string _mode = "policy";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("policy" or "direct" or "off")) return false;
        Interlocked.Exchange(ref _mode, mode);
        return true;
    }

    internal static DeviceCapabilities Capabilities(string mode, DeviceCapabilities? capabilities = null)
    {
        capabilities ??= ConsoleEvidence.Default;
        return mode == "off" ? SoftwareDecodePolicy.WithoutFallbacks(capabilities) : capabilities;
    }
}
#endif
