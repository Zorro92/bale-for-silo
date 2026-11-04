#if DEBUG_SERVER
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>In-memory development opt-in; captured once per playback attempt, never persisted.</summary>
internal static class DebugFfmpegPlayback
{
    private static string _mode = "off";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode != "off" && !IsEnabled(mode)) return false;
        Interlocked.Exchange(ref _mode, mode!);
        return true;
    }

    internal static bool IsEnabled(string? mode) => mode is "auto" or "software" or "system";

    internal static bool UseForDirectStream(string mode, bool isHls) => IsEnabled(mode) && !isHls;

    internal static DeviceCapabilities Capabilities(string mode) => IsEnabled(mode)
        ? DebugPlaybackCapabilities.Av1SoftwareClaim : ConsoleEvidence.Default;
}
#endif
