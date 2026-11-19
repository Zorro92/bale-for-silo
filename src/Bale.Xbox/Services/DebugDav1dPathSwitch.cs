#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only dav1d output/grain A/B, captured per playback attempt (owner, 2026-11-19).
/// d3d11 uses CPU NV12/P010 and dav1d CPU grain without D3D12; zerocopy restores the old
/// D3D12 buffers, upload variant 6 and GPU grain when registration succeeds.
/// </summary>
internal static class DebugDav1dPathSwitch
{
    private static string _mode = "d3d11";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("d3d11" or "zerocopy")) return false;
        Volatile.Write(ref _mode, mode);
        return true;
    }
}
#endif
