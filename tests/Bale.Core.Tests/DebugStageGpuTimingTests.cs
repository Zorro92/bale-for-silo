using System.Globalization;
using System.Runtime.InteropServices;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public sealed class DebugStageGpuTimingTests
{
    [Theory]
    [InlineData("on", true, true)]
    [InlineData("off", true, false)]
    [InlineData(null, false, false)]
    [InlineData("ON", false, false)]
    [InlineData("on,off", false, false)]
    public void SwitchOnlyAcceptsOnOrOff(string? mode, bool valid, bool enabled)
    {
        Assert.Equal(valid, DebugStageGpuTiming.TryParse(mode, out var actual));
        Assert.Equal(enabled, actual);
    }

    [Fact]
    public void NativeSnapshotMatchesTheHeaderLayout()
    {
        Assert.Equal(112, Marshal.SizeOf<DebugStageGpuTiming.Stats>());
        foreach (var (field, offset) in new (string, int)[]
        {
            ("Session", 0), ("Window", 8), ("State", 16), ("Frames", 20), ("Valid", 24),
            ("Disjoint", 28), ("Unavailable", 32), ("Pending", 36), ("Error", 40), ("Reserved", 44),
            ("TotalAvg", 48), ("ResolutionAvg", 56), ("UnusedAvg2", 64), ("UnusedAvg3", 72),
            ("TotalMax", 80), ("ResolutionMax", 88), ("UnusedMax2", 96), ("UnusedMax3", 104),
        }) Assert.Equal(offset, Marshal.OffsetOf<DebugStageGpuTiming.Stats>(field).ToInt32());
    }

    private static DebugStageGpuTiming.Stats Window() => new()
    {
        Session = 2, Window = 1, State = 1, Frames = 240, Valid = 60, Unavailable = 180, Pending = 1,
        TotalAvg = 10.5, TotalMax = 12, ResolutionAvg = 0.25, ResolutionMax = 0.5,
    };

    [Fact]
    public void RowAndLogUseInvariantMillisecondsAndPreserveWindowHealthCounts()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var timing = new DebugStageGpuTiming(1, true);
            var row = timing.Accept(Window(), out var line);
            Assert.Contains("frames=240 total avg/max 10.500/12.000 ms (upper bound)", row);
            Assert.Contains("end resolution 0.250/0.500 ms", row);
            Assert.Contains("valid=60 unavailable=180 pending=1", row);
            // Events measure the total only: no pass rows that could read as zero-ms passes.
            foreach (var gone in new[] { "input-copy", "luma", "chroma", "disjoint" })
                Assert.DoesNotContain(gone, row);
            Assert.StartsWith("Bale stage gpu: frames=240", line);
            Assert.Contains("Stage GPU", DebugStatsRowFilter.KnownLabels);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void NewMountRejectsOldPlaybackAndLogsEachWindowOnceEvenWithRepeatedStatsRequests()
    {
        var timing = new DebugStageGpuTiming(2, true);
        Assert.Contains("waiting", timing.Accept(Window(), out var old));
        Assert.Null(old);
        var stats = Window();
        stats.Session = 3;
        timing.Accept(stats, out var first);
        Assert.NotNull(first);
        timing.Accept(stats, out var duplicate);
        Assert.Null(duplicate);
        stats.Window++;
        timing.Accept(stats, out var next);
        Assert.NotNull(next);
        stats.State = 3;
        Assert.Contains("closed", timing.Accept(stats, out var closed));
        Assert.Null(closed);
    }

    [Fact]
    public void UnavailableResultsNeverLookLikeAZeroMillisecondMeasurement()
    {
        var timing = new DebugStageGpuTiming(0, true);
        var stats = Window();
        stats.Valid = 0;
        var row = timing.Accept(stats, out _);
        Assert.Contains("total unavailable (no completed event pairs)", row);
        Assert.DoesNotContain("avg/max 0", row);
        stats.Window = 0;
        stats.State = 2;
        stats.Error = unchecked((int)0x80004005);
        Assert.Contains("query HRESULT 0x80004005", timing.Accept(stats, out _));
        var off = new DebugStageGpuTiming(0, false);
        Assert.StartsWith("off", off.Accept(stats, out var line));
        Assert.Null(line);
        var missedBaseline = new DebugStageGpuTiming(null, true);
        Assert.Contains("native snapshot busy at mount", missedBaseline.Accept(stats, out var stale));
        Assert.Null(stale);
    }

    [Theory]
    [InlineData(0u, "query failure")]
    [InlineData(1u, "query creation failed")]
    [InlineData(2u, "query data size mismatch")]
    [InlineData(3u, "GetData failed")]
    [InlineData(4u, "zero QPC frequency")]
    [InlineData(5u, "invalid event sequence")]
    [InlineData(6u, "timing panic contained")]
    [InlineData(7u, "context is not immediate")]
    [InlineData(8u, "device failure")]
    [InlineData(9u, "no event poll thread")]
    [InlineData(10u, "event never completed")]
    [InlineData(11u, "query failure")]
    [InlineData(uint.MaxValue, "query failure")]
    public void DisabledStageLogsOnceEvenWhenCreationFailsBeforeTheFirstWindow(uint reason, string expected)
    {
        var timing = new DebugStageGpuTiming(1, true);
        var stats = new DebugStageGpuTiming.Stats
        {
            Session = 2, State = 2, Error = unchecked((int)0x80004005), Reserved = reason,
        };
        var row = timing.Accept(stats, out var line);
        Assert.Contains($"disabled ({expected}; query HRESULT 0x80004005)", row);
        Assert.Equal($"Bale stage gpu: disabled ({expected}; query HRESULT 0x80004005)", line);
        timing.Accept(stats, out var repeated);
        Assert.Null(repeated);
        stats.Window = 1; // a final partial window or teardown must not emit the failure twice
        timing.Accept(stats, out var closed);
        Assert.Null(closed);
        stats.Session++;
        timing.Accept(stats, out var nextSession);
        Assert.NotNull(nextSession);
    }

    [Fact]
    public void FailureAfterAValidWindowDoesNotReplayTheLastValidMeasurementAsItsLog()
    {
        var timing = new DebugStageGpuTiming(1, true);
        var stats = Window();
        timing.Accept(stats, out _);
        stats.State = 2;
        stats.Error = unchecked((int)0x887A0005);
        stats.Reserved = 3;
        Assert.Contains("disabled (GetData failed", timing.Accept(stats, out var line));
        Assert.Equal("Bale stage gpu: disabled (GetData failed; query HRESULT 0x887A0005)", line);
        timing.Accept(stats, out var repeated);
        Assert.Null(repeated);
    }
}
