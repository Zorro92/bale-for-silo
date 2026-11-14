#if DEBUG_SERVER
using System.Globalization;
using System.Text;
using Windows.System;

namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    // Splits app memory into the managed GC heap and everything else (native decoders, MF, WinRT, Composition),
    // so a growing process commit can be attributed. gc=1 first forces a full blocking collection and runs
    // finalizers twice, which releases WinRT/COM wrappers that only finalization frees.
    private static async Task<(string Status, string Body)> MemoryAsync(bool collect)
    {
        var builder = new StringBuilder();
        if (collect)
        {
            // Off the UI thread: finalizers can take a while; the debug server is already on a worker.
            await Task.Run(() =>
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            });
            builder.Append("Forced full GC + finalizers\n");
        }

        var info = GC.GetGCMemoryInfo(GCKind.Any);
        static string MiB(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
        builder.Append("Managed heap: ").Append(MiB(GC.GetTotalMemory(false)))
            .Append(" · GC heap size ").Append(MiB(info.HeapSizeBytes))
            .Append(" · GC committed ").Append(MiB(info.TotalCommittedBytes))
            .Append(" · fragmented ").Append(MiB(info.FragmentedBytes)).Append('\n');
        builder.Append("Collections: gen0=").Append(GC.CollectionCount(0))
            .Append(" gen1=").Append(GC.CollectionCount(1))
            .Append(" gen2=").Append(GC.CollectionCount(2)).Append('\n');
        try
        {
            var usage = (long)MemoryManager.AppMemoryUsage;
            builder.Append("App memory: ").Append(MiB(usage))
                .Append(" of ").Append(MiB((long)MemoryManager.AppMemoryUsageLimit))
                .Append(" · non-GC ").Append(MiB(Math.Max(0, usage - info.TotalCommittedBytes))).Append('\n');
        }
        catch (Exception ex)
        {
            builder.Append("App memory: unavailable (0x").Append(ex.HResult.ToString("X8", CultureInfo.InvariantCulture)).Append(")\n");
        }
        // Xbox-only budget split (hevc-xbox GetExtendedMemoryInfo): general heap vs graphics partition.
        builder.Append("Xbox memory: ").Append(DebugXboxMemory.Describe()).Append('\n');
        builder.Append("Playback resources: ").Append(PlaybackResources.Describe()).Append('\n');
        return ("200 OK", builder.ToString());
    }
}
#endif
