#if DEBUG_SERVER
using System.Runtime.InteropServices;
using System.Text;

namespace Bale.Xbox.Services;

/// <summary>
/// Loader check for native/bale-gpu (Rust, C ABI): the DLL loads in the app container, Rust std threads work,
/// and the windows crate reaches a hardware D3D11 device. Every call is guarded so a missing export or DLL
/// reports instead of ending the app.
/// </summary>
internal static unsafe class DebugRustProbe
{
    private const uint ExpectedAbi = 6;
    private const uint ExpectedGrainAbi = 2;
    private const int EngineReportCapacity = 16 * 1024;
    private const int GrainReportCapacity = 64 * 1024;

    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_gpu_abi_version();
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern ulong bale_gpu_std_probe();
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_d3d11_probe(uint* featureLevel);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_grain_abi_version();
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_grain_gpu_probe(byte* report, uint capacity);
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern uint bale_gpu_engine_probe(char* url, uint urlLength, uint seconds, char* target, uint targetLength,
        byte* report, uint reportCapacity);
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern uint bale_gpu_engine_probe_source(nint source, uint seconds, byte* report, uint reportCapacity);
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern uint bale_ass_probe(byte* font, uint fontLength, byte* report, uint reportCapacity);
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern uint bale_ass_probe_png(byte* font, uint fontLength, uint timeMs, byte* png, uint pngCapacity);

    private const int AssReportCapacity = 64 * 1024;
    /// <summary>1920x1080 RGB in stored deflate blocks is about 6.3 MB.</summary>
    private const int AssPngCapacity = 8 * 1024 * 1024;

    /// <summary>The packaged Inter Tight Black, passed to libass as an embedded font (like an MKV attachment).</summary>
    private static byte[] EmbeddedTestFont()
    {
        try
        {
            var path = Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "Assets", "Fonts", "InterTight-Black.ttf");
            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Log.Info($"Debug asstest: embedded test font unavailable ({ex.GetType().Name})");
            return [];
        }
    }

    /// <summary>libass in bale_gpu.dll: version, DirectWrite font lookups and fallback, render timings.</summary>
    internal static string RunAss()
    {
        try
        {
            var font = EmbeddedTestFont();
            var buffer = new byte[AssReportCapacity];
            uint written;
            fixed (byte* fontBytes = font)
            fixed (byte* report = buffer)
                written = bale_ass_probe(fontBytes, (uint)font.Length, report, (uint)buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length));
        }
        catch (Exception ex)
        {
            return $"libass probe: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})\n";
        }
    }

    /// <summary>The libass probe script rendered at <paramref name="timeMs"/> as a base64 PNG, or null.</summary>
    internal static string? RunAssPng(uint timeMs)
    {
        try
        {
            var font = EmbeddedTestFont();
            var buffer = new byte[AssPngCapacity];
            uint written;
            fixed (byte* fontBytes = font)
            fixed (byte* png = buffer)
                written = bale_ass_probe_png(fontBytes, (uint)font.Length, timeMs, png, (uint)buffer.Length);
            return written == 0 ? null : Convert.ToBase64String(buffer, 0, (int)written);
        }
        catch (Exception ex)
        {
            Log.Info($"Debug asstest png: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})");
            return null;
        }
    }

    /// <summary>Plays a live COM media source (borrowed; the caller keeps it alive) through the Rust engine probe.</summary>
    internal static string RunEngineSource(nint source, int seconds)
    {
        try
        {
            var abi = bale_gpu_abi_version();
            if (abi != ExpectedAbi) return $"Rust GPU layer ABI {abi} (expected {ExpectedAbi}) MISMATCH\n";
            var buffer = new byte[EngineReportCapacity];
            uint written;
            fixed (byte* report = buffer)
                written = bale_gpu_engine_probe_source(source, (uint)seconds, report, (uint)buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length));
        }
        catch (Exception ex)
        {
            return $"Rust engine probe: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})\n";
        }
    }

    /// <summary>
    /// The Rust port of <see cref="DebugMediaEngine.Run"/>; same report shape, URL never echoed.
    /// <paramref name="target"/> is the output format (bgra, fp16 or rgb10; null = bgra), parsed in Rust.
    /// </summary>
    internal static string RunEngine(Uri uri, int seconds, string? target = null)
    {
        try
        {
            var abi = bale_gpu_abi_version();
            if (abi != ExpectedAbi) return $"Rust GPU layer ABI {abi} (expected {ExpectedAbi}) MISMATCH\n";
            var url = uri.AbsoluteUri;
            var format = target ?? string.Empty;
            var buffer = new byte[EngineReportCapacity];
            uint written;
            fixed (char* text = url)
            fixed (char* targetText = format)
            fixed (byte* report = buffer)
                written = bale_gpu_engine_probe(text, (uint)url.Length, (uint)seconds, targetText, (uint)format.Length, report, (uint)buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length));
        }
        catch (Exception ex)
        {
            // Type and HRESULT only: never the message.
            return $"Rust engine probe: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})\n";
        }
    }

    internal static string Run()
    {
        var report = new StringBuilder("Rust GPU layer (bale_gpu.dll):\n");
        uint abi;
        try
        {
            Log.Info("Debug rust: before load");
            abi = bale_gpu_abi_version();
        }
        catch (Exception ex)
        {
            report.Append($"  Load: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})\n");
            return report.ToString();
        }
        report.Append($"  Load: ok; ABI {abi} (expected {ExpectedAbi}){(abi == ExpectedAbi ? string.Empty : " MISMATCH")}\n");
        if (abi != ExpectedAbi) return report.ToString();

        Log.Info("Debug rust: before std probe");
        var micros = bale_gpu_std_probe();
        report.Append(micros == ulong.MaxValue ? "  std thread + mutex: FAILS\n" : $"  std thread + mutex: ok ({micros} µs)\n");

        Log.Info("Debug rust: before D3D11 probe");
        uint level = 0;
        var hr = bale_gpu_d3d11_probe(&level);
        report.Append(hr >= 0 ? $"  D3D11 hardware device (windows crate): ok, feature level 0x{level:X4}\n"
            : $"  D3D11 hardware device (windows crate): FAILS 0x{unchecked((uint)hr):X8}\n");
        report.Append("GPU film grain:\n");
        try
        {
            var grainAbi = bale_grain_abi_version();
            report.Append($"  Grain ABI: {grainAbi} (expected {ExpectedGrainAbi}){(grainAbi == ExpectedGrainAbi ? string.Empty : " MISMATCH")}\n");
            if (grainAbi == ExpectedGrainAbi)
            {
                Log.Info("Debug rust: before GPU film grain probe");
                var buffer = new byte[GrainReportCapacity];
                uint written;
                fixed (byte* text = buffer)
                    written = bale_grain_gpu_probe(text, (uint)buffer.Length);
                report.Append(Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length)));
                if (report[report.Length - 1] != '\n') report.Append('\n');
            }
        }
        catch (Exception ex)
        {
            // Includes EntryPointNotFoundException for an older DLL; type and HRESULT only.
            report.Append($"  GPU grain probe: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})\n");
        }
        Log.Info("Debug rust: done");
        return report.ToString();
    }
}
#endif
