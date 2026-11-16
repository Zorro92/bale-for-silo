#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only stream adapter switch, captured per playback attempt; rust after restart. "rust" (the
/// default, as in release builds) opens the Rust range or forward-only stream and hands the player its
/// Rust WinRT stream, so reads never run managed code (docs/rust-port.md, slice 7); "pooled" and
/// "builtin" keep the C# streams for comparison.
/// </summary>
internal static class DebugStreamAdapterSwitch
{
    private static string _mode = "rust";

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
