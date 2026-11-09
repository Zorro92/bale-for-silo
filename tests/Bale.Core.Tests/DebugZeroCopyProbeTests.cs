namespace Bale.Core.Tests;

// Source-contract checks for the debug-only D3D12 zero-copy feasibility probe (bale_gpu.dll zerocopy.rs).
public class DebugZeroCopyProbeTests
{
    [Fact]
    public void RouteValidatesFramesAndRunsOffTheRequestThread()
    {
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/zero-copy-probe\"):", server);
        Assert.Contains("zeroCopyFrames is < 1 or > 60", server);
        Assert.Contains("await Task.Run(() => DebugRustProbe.RunZeroCopy(zeroCopyFrames))", server);
        Assert.Contains("GET /zero-copy-probe?frames=10", server);
    }

    [Fact]
    public void InteropLoadsD3D12AtRunTimeAndReportsOnlyTypeAndHresult()
    {
        var probe = Read("DebugRustProbe.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", probe);
        Assert.Contains("private static extern uint bale_gpu_zero_copy_probe(nint d3d12Module, uint iterations, byte* report, uint reportCapacity);", probe);
        Assert.Contains("written = bale_gpu_zero_copy_probe(module, (uint)frames, report, (uint)buffer.Length);", probe);
        Assert.Contains("Zero-copy probe: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})", probe);
        Assert.DoesNotContain("ex.Message", probe);
    }

    [Fact]
    public void ScriptExposesTheProbe()
    {
        var script = Read("bale-debug.sh");
        Assert.Contains("scripts/bale-debug.sh zero-copy-probe [frames]", script);
        Assert.Contains("\"$BASE/zero-copy-probe\"", script);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
