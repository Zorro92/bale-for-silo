namespace Bale.Core.Tests;

public class DebugGcTests
{
    [Fact]
    public void DiagnosticsAreDebugOnlyAndOfferOnlySupportedLatencyModes()
    {
        var source = Read("DebugServer.Gc.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        foreach (var required in new[] { "GCSettings.IsServerGC", "GCSettings.LatencyMode", "GC.GetTotalPauseDuration()",
            "GC.CollectionCount(0)", "GC.CollectionCount(1)", "GC.CollectionCount(2)",
            "GCKind.FullBlocking", "GCKind.Background", "GCKind.Ephemeral", "info.Index == 0", "info.PauseDurations",
            "info.PromotedBytes", "info.HeapSizeBytes", "info.FragmentedBytes", "info.PinnedObjectsCount",
            "info.GenerationInfo", "SizeBeforeBytes", "SizeAfterBytes", "\"gen0\", \"gen1\", \"gen2\", \"loh\", \"poh\"",
            "CultureInfo.InvariantCulture", "\"batch\" => GCLatencyMode.Batch", "\"interactive\" => GCLatencyMode.Interactive",
            "\"sustained\" => GCLatencyMode.SustainedLowLatency", "\"default\" => defaultLatencyMode",
            "PlaybackGcLatency.Shared.OriginalMode", "\" playback-lease=\"", "PlaybackGcLatency.Shared.IsHeld ? 1 : 0",
            "catch (Exception ex)", "latency change failed hr=0x", "ex.HResult.ToString(\"X8\"" })
            Assert.Contains(required, source);
        Assert.True(source.IndexOf("PlaybackGcLatency.Shared.OriginalMode", StringComparison.Ordinal)
            < source.IndexOf("GCSettings.LatencyMode =", StringComparison.Ordinal));
        Assert.DoesNotContain("DefaultGcLatencyMode", source);
        Assert.DoesNotContain("GCLatencyMode.LowLatency", source);
        Assert.DoesNotContain("GCLatencyMode.NoGCRegion", source);
        Assert.DoesNotContain("ex.Message", source);
    }

    [Fact]
    public void RoutesRejectUnexpectedAndDuplicateQueryKeysAndDocumentBothMethods()
    {
        var routes = Read("DebugServer.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", routes);
        var duplicateGuard = routes.Split('\n').Single(line => line.Contains("duplicateQueryKeys && path is", StringComparison.Ordinal));
        Assert.Contains("\"/gc\"", duplicateGuard);
        var get = routes[routes.IndexOf("case (\"GET\", \"/gc\")", StringComparison.Ordinal)
            ..routes.IndexOf("case (\"POST\", \"/gc\")", StringComparison.Ordinal)];
        Assert.Contains("if (query.Count != 0)", get);
        Assert.Contains("\"400 Bad Request\", GcUsage", get);
        Assert.Contains("return GcStatus()", get);
        var post = routes[routes.IndexOf("case (\"POST\", \"/gc\")", StringComparison.Ordinal)
            ..routes.IndexOf("case (\"GET\", \"/frame-delivery\")", StringComparison.Ordinal)];
        Assert.Contains("query.Count != 1 || !query.ContainsKey(\"latency\")", post);
        Assert.Contains("\"400 Bad Request\", GcUsage", post);
        Assert.Contains("return GcStatus(query[\"latency\"])", post);
        Assert.Contains("///   GET  /gc", routes);
        Assert.Contains("///   POST /gc?latency=", routes);
        var notFound = routes.Split('\n').Single(line => line.Contains("\"routes: ", StringComparison.Ordinal));
        Assert.Contains("GET /gc, POST /gc?latency={batch|interactive|sustained|default}", notFound);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
