#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only stream adapter switch, captured per playback attempt; pooled after restart. "rust" opens
/// the Rust range stream and hands the player its Rust IRandomAccessStream, so reads never run managed
/// code (docs/rust-port.md, slice 7).
/// </summary>
internal static class DebugStreamAdapterSwitch
{
    private static string _mode = "pooled";

    internal static string Mode => Volatile.Read(ref _mode);
    internal static bool Pooled => Mode == "pooled";

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("pooled" or "builtin" or "rust")) return false;
        Volatile.Write(ref _mode, mode);
        return true;
    }
}
#endif
