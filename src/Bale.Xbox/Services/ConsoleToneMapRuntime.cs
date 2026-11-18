using System.Runtime.InteropServices;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

internal static class ConsoleToneMapRuntime
{
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_tonemap_abi_version();

    private static readonly Lazy<bool> Available = new(CheckAvailable);
    internal static bool IsAvailable => Available.Value;

    // bale_tonemap_output_formats bit 4 (BALE_TONEMAP_OUTPUT_DOWNSCALE): area downscale to a smaller output.
    private const uint DownscaleBit = 4;
    private static readonly Lazy<bool> Downscale = new(CheckDownscale);
    internal static bool SupportsDownscale => IsAvailable && Downscale.Value;

    private static unsafe bool CheckDownscale()
    {
        nint module = 0;
        try
        {
            if (!NativeLibrary.TryLoad("bale_gpu.dll", out module)
                || !NativeLibrary.TryGetExport(module, "bale_tonemap_output_formats", out var export)) return false;
            return (((delegate* unmanaged<uint>)export)() & DownscaleBit) != 0;
        }
        catch (Exception) { return false; }
        finally { if (module != 0) NativeLibrary.Free(module); }
    }

    private static bool CheckAvailable()
    {
        if (!ConsoleToneMapPolicy.WrapperSupported(FfmpegWrapperVersion.Value)) return false;
        nint module = 0;
        try
        {
            if (!NativeLibrary.TryLoad("bale_gpu.dll", out module)) return false;
            return new[] { "bale_tonemap_abi_version", "bale_tonemap_create", "bale_tonemap_apply", "bale_tonemap_destroy" }
                .All(name => NativeLibrary.TryGetExport(module, name, out _)) && bale_tonemap_abi_version() == 1;
        }
        catch (Exception ex)
        {
            Log.Info($"Tone map: unavailable ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}); server tone mapping required");
            return false;
        }
        finally { if (module != 0) NativeLibrary.Free(module); }
    }
}
