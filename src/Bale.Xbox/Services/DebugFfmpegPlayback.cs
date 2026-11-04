#if DEBUG_SERVER
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>Memory-only diagnostic override, captured once per attempt. Production policy is the default.</summary>
internal static class DebugFfmpegPlayback
{
    private static string _mode = "policy";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("policy" or "off")) return false;
        Interlocked.Exchange(ref _mode, mode);
        return true;
    }

    internal static DeviceCapabilities Capabilities(string mode) => mode == "policy"
        ? ConsoleEvidence.Default : SoftwareDecodePolicy.WithoutFallbacks(ConsoleEvidence.Default);
}
#endif
