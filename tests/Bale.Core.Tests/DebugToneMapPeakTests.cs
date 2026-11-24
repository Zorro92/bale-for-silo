using System.Globalization;
using System.Runtime.InteropServices;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public sealed class DebugToneMapPeakTests
{
    [Fact]
    public void NativeRecordMatchesTheHeaderLayout()
    {
        Assert.Equal(40, Marshal.SizeOf<DebugToneMapPeak.Info>());
        foreach (var (field, offset) in new (string, int)[]
        {
            ("Stage", 0), ("Changes", 8), ("Source", 12), ("PeakNits", 16), ("ConfigPeakNits", 20),
            ("MaxCll", 24), ("MasteringMaxNits", 28), ("Transfer", 32), ("Flags", 36),
        }) Assert.Equal(offset, Marshal.OffsetOf<DebugToneMapPeak.Info>(field).ToInt32());
    }

    private static DebugToneMapPeak.Info MaxCll() => new()
    {
        Stage = 3, Changes = 1, Source = 2, PeakNits = 1500, ConfigPeakNits = 1000,
        MaxCll = 1500, MasteringMaxNits = 4000, Transfer = 16,
    };

    [Fact]
    public void EachStreamLogsItsPeakAndSourceOnceAndAgainOnlyWhenItChanges()
    {
        var peak = new DebugToneMapPeak(2);
        var row = peak.Accept(MaxCll(), out var line);
        Assert.Equal("1500 nits from maxcll (PQ · MaxCLL 1500 · mastering 4000 · config 1000) · stage 3", row);
        Assert.Equal("Bale tone map peak: " + row, line);
        peak.Accept(MaxCll(), out var repeated);
        Assert.Null(repeated);
        var mastering = MaxCll();
        mastering.Changes = 2;
        mastering.Source = 3;
        mastering.PeakNits = 1000;
        mastering.MaxCll = 4000;
        mastering.MasteringMaxNits = 1000;
        Assert.Contains("1000 nits from mastering (PQ · MaxCLL 4000 · mastering 1000", peak.Accept(mastering, out var changed));
        Assert.NotNull(changed);
        var next = MaxCll();
        next.Stage = 4; // a remount's stage restarts its change count
        peak.Accept(next, out var nextStream);
        Assert.NotNull(nextStream);
        Assert.Contains("Tone-map peak", DebugStatsRowFilter.KnownLabels);
    }

    [Fact]
    public void EarlierPlaybacksAndStagesWithoutAToneMappedFrameStayQuiet()
    {
        var peak = new DebugToneMapPeak(3);
        Assert.Equal("no tone-map stage", peak.Accept(MaxCll(), out var old));
        Assert.Null(old);
        var fresh = MaxCll();
        fresh.Stage = 4;
        fresh.Changes = 0;
        Assert.Equal("no PQ or HLG frame yet · stage 4", peak.Accept(fresh, out var none));
        Assert.Null(none);
        var busy = new DebugToneMapPeak(null);
        Assert.Contains("native snapshot busy at mount", busy.Accept(MaxCll(), out var stale));
        Assert.Null(stale);
    }

    [Fact]
    public void ConfigPeaksHlgAndTheDebugSwitchReadPlainlyInAnyCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var hlg = new DebugToneMapPeak.Info { Stage = 1, Changes = 1, Source = 1, PeakNits = 1000.5f, Transfer = 18, Flags = 1 };
            Assert.Equal("1000.5 nits from config (HLG · MaxCLL none · mastering none · config unset) · debug fixedpeak · stage 1",
                DebugToneMapPeak.Describe(hlg));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
