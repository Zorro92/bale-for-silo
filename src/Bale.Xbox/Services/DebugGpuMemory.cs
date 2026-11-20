#if DEBUG_SERVER
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Bale.Xbox.Services;

/// <summary>Independent bale_gpu memory ABI 1. Missing exports are expected with an older DLL.</summary>
internal static class DebugGpuMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        public uint Category;
        public int Count;
        public long Bytes;
        public long PeakBytes;
    }

    private static string MiB(double bytes) => (bytes / (1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";

    internal static unsafe string Describe()
    {
        try
        {
            if (bale_mem_abi_version() != 1) return "GPU ledger: unavailable\n";
            // A full-capacity snapshot avoids a size-query race with a newly used category.
            var entries = stackalloc Entry[64];
            var count = Math.Min(64u, bale_mem_snapshot(entries, 64));
            var builder = new StringBuilder("GPU ledger:\n");
            double total = 0;
            for (var i = 0u; i < count; i++)
            {
                var entry = entries[i];
                total += entry.Bytes;
                builder.Append("  ").Append(entry.Category.ToString(CultureInfo.InvariantCulture))
                    .Append(' ').Append(Name(entry.Category)).Append(": count ")
                    .Append(entry.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" · current ").Append(MiB(entry.Bytes)).Append(" · peak ").Append(MiB(entry.PeakBytes));
                var underflows = bale_mem_underflows(entry.Category);
                if (underflows != 0) builder.Append(" · underflows ").Append(underflows.ToString(CultureInfo.InvariantCulture));
                builder.Append('\n');
            }
            builder.Append("  counted total: ").Append(MiB(total)).Append('\n');
            var (memory, _) = DebugXboxMemory.Read();
            builder.Append("  uncounted graphics = Xbox graphics usage minus counted total: ")
                .Append(memory is { } value ? MiB(value.ExtendedMemoryUsage - total) : "unavailable")
                .Append(" (Media Foundation/driver/rav1g internals and padding; includes uninstrumented allocations)\n");
            return builder.ToString();
        }
        catch (DllNotFoundException) { return "GPU ledger: unavailable\n"; }
        catch (EntryPointNotFoundException) { return "GPU ledger: unavailable\n"; }
        catch (BadImageFormatException) { return "GPU ledger: unavailable\n"; }
    }

    internal static bool ResetPeaks()
    {
        try
        {
            if (bale_mem_abi_version() != 1) return false;
            bale_mem_reset_peaks();
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    private static string Name(uint category) => category switch
    {
        1 => "bale_gpu tone map",
        2 => "bale_gpu film grain",
        3 => "bale_gpu subtitle overlay",
        4 => "bale_gpu engine target/staging",
        5 => "bale_gpu zero-copy resources",
        16 => "FFmpeg decoder frames",
        17 => "FFmpeg tone-map output pool",
        18 => "FFmpeg dav1d upload pool",
        19 => "FFmpeg rav1g output image pool",
        20 => "FFmpeg rav1g prewarm resident",
        21 => "FFmpeg other textures",
        32 => "Bale player swap chain (estimate)",
        _ => "reserved/unassigned",
    };

    /// <summary>One real player's output: MF hides the buffer count, so assume two buffers.</summary>
    internal sealed class PlayerSwapChain : IDisposable
    {
        private long _bytes;
        private bool _active;

        // EngineBackend serializes setup, resize and teardown under its session lock.
        internal void Set(uint width, uint height, uint target)
        {
            if (width == 0 || height == 0) return;
            // Engine target 1 is RGBA16F (8 B/px); BGRA8 and RGB10A2 are both 4 B/px.
            var bytesPerPixel = target == 1 ? 8L : 4L;
            var pixels = Math.Min((ulong)width * height, (ulong)(long.MaxValue / (bytesPerPixel * 2)));
            var bytes = (long)pixels * bytesPerPixel * 2;
            try
            {
                if (bale_mem_abi_version() != 1) return;
                bale_mem_account(32, bytes - _bytes, _active ? 0 : 2);
                _bytes = bytes;
                _active = true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
        }

        public void Dispose()
        {
            if (!_active) return;
            bale_mem_account(32, -_bytes, -2);
            _bytes = 0;
            _active = false;
        }
    }

    [DllImport("bale_gpu.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint bale_mem_abi_version();
    [DllImport("bale_gpu.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bale_mem_account(uint category, long deltaBytes, int deltaCount);
    [DllImport("bale_gpu.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe uint bale_mem_snapshot(Entry* entries, uint capacity);
    [DllImport("bale_gpu.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bale_mem_reset_peaks();
    [DllImport("bale_gpu.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong bale_mem_underflows(uint category);
}
#endif
