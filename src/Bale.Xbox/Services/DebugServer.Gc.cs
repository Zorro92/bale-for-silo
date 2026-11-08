#if DEBUG_SERVER
using System.Globalization;
using System.Runtime;
using System.Text;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    private const string GcUsage = "usage: GET /gc or POST /gc?latency={batch|interactive|sustained|default}\n";

    private static (string Status, string Body) GcStatus(string? latency = null)
    {
        // The first diagnostic request may arrive while playback has already changed the mode.
        var defaultLatencyMode = PlaybackGcLatency.Shared.OriginalMode;
        if (latency is not null)
        {
            GCLatencyMode? mode = latency switch
            {
                "batch" => GCLatencyMode.Batch,
                "interactive" => GCLatencyMode.Interactive,
                "sustained" => GCLatencyMode.SustainedLowLatency,
                "default" => defaultLatencyMode,
                _ => null,
            };
            if (mode is null) return ("400 Bad Request", GcUsage);
            try
            {
                GCSettings.LatencyMode = mode.Value;
            }
            catch (Exception ex)
            {
                return ("500 Internal Server Error", "latency change failed hr=0x"
                    + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "\n");
            }
        }

        static string Milliseconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        static string MiB(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

        var builder = new StringBuilder();
        builder.Append("GC: server=").Append(GCSettings.IsServerGC)
            .Append(" latency=").Append(GCSettings.LatencyMode)
            .Append(" playback-lease=").Append(PlaybackGcLatency.Shared.IsHeld ? 1 : 0)
            .Append(" pause-total=").Append(Milliseconds(GC.GetTotalPauseDuration().TotalMilliseconds))
            .Append(" ms collections gen0=").Append(GC.CollectionCount(0))
            .Append(" gen1=").Append(GC.CollectionCount(1))
            .Append(" gen2=").Append(GC.CollectionCount(2))
            .Append(" allocated=").Append(MiB(GC.GetTotalAllocatedBytes(false))).Append(" MiB\n");
        foreach (var kind in new[] { GCKind.FullBlocking, GCKind.Background, GCKind.Ephemeral })
        {
            var info = GC.GetGCMemoryInfo(kind);
            builder.Append("last ").Append(kind).Append(": ");
            if (info.Index == 0)
            {
                builder.Append("none\n");
                continue;
            }

            var pauseMilliseconds = 0.0;
            foreach (var pause in info.PauseDurations) pauseMilliseconds += pause.TotalMilliseconds;
            builder.Append('#').Append(info.Index)
                .Append(" gen=").Append(info.Generation)
                .Append(" compacted=").Append(info.Compacted)
                .Append(" concurrent=").Append(info.Concurrent)
                .Append(" pause=").Append(Milliseconds(pauseMilliseconds)).Append(" ms")
                .Append(" promoted=").Append(MiB(info.PromotedBytes)).Append(" MiB")
                .Append(" heap=").Append(MiB(info.HeapSizeBytes)).Append(" MiB")
                .Append(" fragmented=").Append(MiB(info.FragmentedBytes)).Append(" MiB")
                .Append(" pinned=").Append(info.PinnedObjectsCount).Append('\n');
            if (kind == GCKind.FullBlocking)
            {
                string[] labels = ["gen0", "gen1", "gen2", "loh", "poh"];
                var generations = info.GenerationInfo;
                builder.Append("  ");
                for (var i = 0; i < Math.Min(labels.Length, generations.Length); i++)
                {
                    if (i > 0) builder.Append(' ');
                    builder.Append(labels[i]).Append(' ').Append(MiB(generations[i].SizeBeforeBytes))
                        .Append('→').Append(MiB(generations[i].SizeAfterBytes));
                }
                builder.Append('\n');
            }
        }
        return ("200 OK", builder.ToString());
    }
}
#endif
