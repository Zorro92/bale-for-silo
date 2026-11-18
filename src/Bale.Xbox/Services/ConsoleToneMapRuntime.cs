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
    // INPUT_NV12 (8) | SDR709 (16): the SDR scale-only stage, including 8-bit sources.
    private const uint SdrDownscaleBits = DownscaleBit | 8 | 16;
    private static readonly Lazy<uint> Formats = new(ReadFormats);
    internal static bool SupportsDownscale => IsAvailable && (Formats.Value & DownscaleBit) != 0;
    internal static bool SupportsSdrDownscale => IsAvailable && (Formats.Value & SdrDownscaleBits) == SdrDownscaleBits;

    private static unsafe uint ReadFormats()
    {
        nint module = 0;
        try
        {
            if (!NativeLibrary.TryLoad("bale_gpu.dll", out module)
                || !NativeLibrary.TryGetExport(module, "bale_tonemap_output_formats", out var export)) return 0;
            return ((delegate* unmanaged<uint>)export)();
        }
        catch (Exception) { return 0; }
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
