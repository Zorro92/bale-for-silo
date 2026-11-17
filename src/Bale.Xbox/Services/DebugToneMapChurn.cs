#if DEBUG_SERVER
using System.Globalization;
using System.Runtime.InteropServices;

namespace Bale.Xbox.Services;

/// <summary>bale_gpu's tone-map churn and device-release counters (debug builds only).</summary>
internal static class DebugToneMapChurn
{
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe int bale_tonemap_debug_churn(uint iterations, uint flags, uint* completed);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe void bale_gpu_device_release_counts(ulong* watched, ulong* released);

    /// <summary>Watched player/churn D3D11 devices and how many were really destroyed.</summary>
    public static unsafe (ulong Watched, ulong Released) DeviceReleases()
    {
        ulong watched = 0, released = 0;
        try { bale_gpu_device_release_counts(&watched, &released); }
        catch (Exception) { return (0, 0); }
        return (watched, released);
    }

    /// <summary>
    /// bale_gpu-only churn: per iteration a fresh hardware device, a 4K P010 decoder-style array,
    /// five NV12 outputs, tone map create, five applies, destroy. control skips the tone map.
    /// </summary>
    public static unsafe string Churn(uint iterations, bool control)
    {
        var before = DeviceReleases();
        var started = System.Diagnostics.Stopwatch.StartNew();
        uint completed = 0;
        var hr = bale_tonemap_debug_churn(iterations, control ? 1u : 0u, &completed);
        var after = DeviceReleases();
        return string.Create(CultureInfo.InvariantCulture,
            $"churn {(control ? "control (no tone map)" : "tone map")}: completed={completed}/{iterations} hr=0x{unchecked((uint)hr):X8} " +
            $"devices watched=+{after.Watched - before.Watched} destroyed=+{after.Released - before.Released} in {started.ElapsedMilliseconds} ms\n");
    }
}
#endif
