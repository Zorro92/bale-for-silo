#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only engine swap-chain format, captured per playback attempt; bgra after restart, as in release builds
/// (which never call bale_gpu_engine_session_set_target). rgb10 asks IMFMediaEngine for R10G10B10A2 output so 10-bit
/// SDR can reach the screen at 10 bits (owner's bit-depth rule, AGENTS.md); fp16 is scRGB. For comparing banding
/// on the console: playtest-present target=rgb10 played a 10-bit SDR clip with 0 dropped frames (2026-11-17).
/// </summary>
internal static class DebugEngineTargetSwitch
{
    private static string _mode = "bgra";

    internal static string Mode => Volatile.Read(ref _mode);

    /// <summary>The bale_gpu_engine_session_set_target value: 0 bgra, 1 fp16, 2 rgb10.</summary>
    internal static uint Code => Mode switch { "fp16" => 1u, "rgb10" => 2u, _ => 0u };

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("bgra" or "rgb10" or "fp16")) return false;
        Volatile.Write(ref _mode, mode);
        return true;
    }
}
#endif
