using System.Globalization;
using System.Runtime.InteropServices;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugMemprobeTests
{
    [Theory]
    [InlineData("cpu", 0)]
    [InlineData("d3d11", 0)]
    [InlineData("d3d12", 0)]
    [InlineData("mixed", 1000)]
    public void DefaultsAndExplicitMiBBounds(string mode, int cpu)
    {
        Assert.True(DebugMemprobeOptions.TryParse(new Dictionary<string, string> { ["mode"] = mode }, out var defaults));
        Assert.Equal(new DebugMemprobeOptions(mode, 100, 3400, cpu), defaults);
        var query = new Dictionary<string, string> { ["mode"] = mode, ["step"] = "0001", ["max"] = "4096" };
        if (mode == "mixed") query["cpu"] = "0";
        Assert.True(DebugMemprobeOptions.TryParse(query, out var explicitOptions));
        Assert.Equal(1024ul * 1024, explicitOptions.StepBytes);
        Assert.Equal(4096ul * 1024 * 1024, explicitOptions.MaxBytes);
        Assert.Equal(0ul, explicitOptions.CpuBytes);
    }

    [Theory]
    [InlineData("", "100", "3400", null)]
    [InlineData("CPU", "100", "3400", null)]
    [InlineData("gpu", "100", "3400", null)]
    [InlineData("cpu", "0", "3400", null)]
    [InlineData("cpu", "101", "100", null)]
    [InlineData("d3d11", "1", "127", null)]
    [InlineData("d3d12", "100", "128", "0")]
    [InlineData("cpu", "100", "3400", "1000")]
    [InlineData("mixed", "100", "3400", "4097")]
    [InlineData("mixed", "100", "3400", "-1")]
    public void InvalidCombinationsAreRejected(string mode, string step, string max, string? cpu)
    {
        var query = new Dictionary<string, string> { ["mode"] = mode, ["step"] = step, ["max"] = max };
        if (cpu is not null) query["cpu"] = cpu;
        Assert.False(DebugMemprobeOptions.TryParse(query, out _));
    }

    [Fact]
    public void InvalidNumbersUnknownAndMissingKeysAreRejected()
    {
        Assert.False(DebugMemprobeOptions.TryParse(new Dictionary<string, string>(), out _));
        Assert.False(DebugMemprobeOptions.TryParse(new Dictionary<string, string> { ["mode"] = "cpu", ["extra"] = "1" }, out _));
        foreach (var key in new[] { "step", "max", "cpu" })
        foreach (var value in new[] { "", "-1", "+1", " 1", "1 ", "1.0", "1,0", "1e2", "0x10", "4097", "99999999999999999999999", "00001", "1&max=4096" })
            Assert.False(DebugMemprobeOptions.TryParse(new Dictionary<string, string> { ["mode"] = "mixed", [key] = value }, out _));
    }

    [Fact]
    public void StopRulesPreserveOneStepOfHeadroomWithoutOverflowAndRequireMoreThanFiveTimes()
    {
        Assert.False(DebugMemprobeReport.NearLimit(new(100, 201, "Low"), 100));
        Assert.True(DebugMemprobeReport.NearLimit(new(100, 200, "Low"), 100));
        Assert.True(DebugMemprobeReport.NearLimit(new(201, 200, "High"), 100));
        Assert.True(DebugMemprobeReport.NearLimit(new(0, 0, "Low"), 1));
        Assert.False(DebugMemprobeReport.NearLimit(new(0, ulong.MaxValue, "Low"), 1));
        Assert.True(DebugMemprobeReport.CopyJump(2, 10.001));
        foreach (var pair in new[] { (2.0, 10.0), (0.0, 1.0), (double.NaN, 10.0), (1.0, double.NaN), (1.0, double.PositiveInfinity), (-1.0, 10.0) })
            Assert.False(DebugMemprobeReport.CopyJump(pair.Item1, pair.Item2));
    }

    [Fact]
    public void ReportsUseInvariantMiBAndGpuSnapshotsMatchTheNativeAbi()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var memory = new DebugMemprobeMemory(200 * DebugMemprobeOptions.MiB, 3328 * DebugMemprobeOptions.MiB, "Low");
            Assert.Equal("memprobe mode=mixed step_mib=100 max_mib=3400 cpu_mib=1000 copy_mib=64 working_mib=128 ledger=excluded app_mib=200.0 limit_mib=3328.0 level=Low",
                DebugMemprobeReport.Header(new("mixed", 100, 3400, 1000), memory));
            Assert.Equal("step=1 phase=cpu cpu_mib=100.0 app_mib=200.0 limit_mib=3328.0 level=Low step_ms=12.500",
                DebugMemprobeReport.CpuStep("cpu", 1, 100 * DebugMemprobeOptions.MiB, memory, 12.5));
            Assert.Equal("freed cpu_mib=0 gpu_mib=0 app_mib=200.0 limit_mib=3328.0 level=Low", DebugMemprobeReport.Freed(memory));
            Assert.Equal("unavailable", DebugMemprobeReport.Milliseconds(double.NaN));
            Assert.Equal("0x8007000E", DebugMemprobeReport.Hr(unchecked((int)0x8007000E)));
            Assert.Equal("hr=0x8007000E", new DebugMemprobeGpu.Segment { Hr = unchecked((int)0x8007000E) }.ToString());
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal(40, Marshal.SizeOf<DebugMemprobeGpu.Segment>());
        Assert.Equal(104, Marshal.SizeOf<DebugMemprobeGpu.Sample>());
        Assert.Equal(16, Marshal.OffsetOf<DebugMemprobeGpu.Sample>(nameof(DebugMemprobeGpu.Sample.Local)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<DebugMemprobeGpu.Sample>(nameof(DebugMemprobeGpu.Sample.RemovedHr)).ToInt32());
    }

    [Fact]
    public void RouteRejectsDuplicatesAndPlaybackBeforeStartingWorkerAndAlwaysReleasesGate()
    {
        var routes = Read("DebugServer.cs.txt");
        Assert.Contains("\"/memprobe\"", routes.Split('\n').Single(line => line.Contains("duplicateQueryKeys && path is", StringComparison.Ordinal)));
        Assert.Contains("case (\"GET\", \"/memprobe\"):", routes);
        Assert.Contains("return await MemprobeAsync(query);", routes);
        var route = Read("DebugServer.Memprobe.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", route);
        Assert.Contains("Interlocked.CompareExchange(ref _playtestBusy, 1, 0)", route);
        Assert.Contains("\"409 Conflict\"", route);
        Assert.Contains("CurrentPage(out _) is PlayerPage", route);
        Assert.True(route.IndexOf("if (inPlayer)", StringComparison.Ordinal) < route.IndexOf("Task.Factory.StartNew", StringComparison.Ordinal));
        Assert.Contains("TaskCreationOptions.LongRunning, TaskScheduler.Default", route);
        Assert.Contains("finally { Interlocked.Exchange(ref _playtestBusy, 0); }", route);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
