#if DEBUG_SERVER
using System.Runtime.InteropServices;
using System.Text;

namespace Bale.Xbox.Services;

/// <summary>
/// bale_gpu's stage bench (debug builds, native feature stage-timing; 2026-11-26): the stage's GPU time per apply with
/// the Bayer and with rav1g's R2 NV12 dither, from GPU timestamps on a private hardware device, at the display routes
/// that write dithered NV12 (docs/gpu-tone-map.md "Bayer against R2 on the console"). Blocks for seconds: call it on
/// the thread pool, outside the player, with gpu-timing off.
/// </summary>
internal static unsafe class DebugToneMapBench
{
    private const int ReportCapacity = 16 * 1024;

    // Pointer-only signature: DisableRuntimeMarshalling=true, Native AOT and CA1420.
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern uint bale_tonemap_debug_bench(uint frames, byte* report, uint capacity);

    /// <summary>Timed applies per arm, bale_gpu's own bound (1-240).</summary>
    internal static bool ValidFrames(uint frames) => frames is >= 1 and <= 240;

    internal static string Run(uint frames)
    {
        try
        {
            var buffer = new byte[ReportCapacity];
            uint written;
            fixed (byte* report = buffer)
                written = bale_tonemap_debug_bench(frames, report, (uint)buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length));
        }
        catch (DllNotFoundException) { return "tonemap bench unavailable (native DLL missing)\n"; }
        catch (EntryPointNotFoundException) { return "tonemap bench unavailable (requires a debug native build with stage-timing)\n"; }
    }
}
#endif
