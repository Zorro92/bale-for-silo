#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only override of the engine swap-chain format, captured per playback attempt; auto after restart. auto
/// follows EngineOutputPolicy (owner's bit-depth rule in AGENTS.md), including the output-depth switch. bgra,
/// rgb10 and fp16 (scRGB) force that format on every engine open, overriding every output-depth rule.
/// </summary>
internal static class DebugEngineTargetSwitch
{
    private static string _mode = "auto";

    internal static string Mode => Volatile.Read(ref _mode);

    /// <summary>The forced bale_gpu engine target code (0 bgra, 1 fp16, 2 rgb10), or null for auto.</summary>
    internal static uint? Code => Mode switch { "bgra" => 0u, "fp16" => 1u, "rgb10" => 2u, _ => null };

    internal static bool TrySet(string? mode)
    {
        if (mode is "default") mode = "auto";
        if (mode is not ("auto" or "bgra" or "rgb10" or "fp16")) return false;
        Volatile.Write(ref _mode, mode);
        return true;
    }
}
#endif
