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
            ("TotalAvg", 48), ("CopyAvg", 56), ("LumaAvg", 64), ("ChromaAvg", 72),
            ("TotalMax", 80), ("CopyMax", 88), ("LumaMax", 96), ("ChromaMax", 104),
        }) Assert.Equal(offset, Marshal.OffsetOf<DebugStageGpuTiming.Stats>(field).ToInt32());
    }

    private static DebugStageGpuTiming.Stats Window() => new()
    {
        Session = 2, Window = 1, State = 1, Frames = 240, Valid = 235, Disjoint = 2, Unavailable = 3, Pending = 4,
        TotalAvg = 10.5, TotalMax = 12, CopyAvg = 0, CopyMax = 0,
        LumaAvg = 4.5, LumaMax = 5, ChromaAvg = 6, ChromaMax = 7,
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
            Assert.Contains("frames=240 total avg/max 10.500/12.000 ms", row);
            Assert.Contains("input-copy 0.000/0.000 ms", row);
            Assert.Contains("luma 4.500/5.000 ms", row);
            Assert.Contains("chroma 6.000/7.000 ms", row);
            Assert.Contains("valid=235 disjoint=2 unavailable=3 pending=4", row);
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
        Assert.Contains("unavailable (no valid timestamps)", row);
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
}
