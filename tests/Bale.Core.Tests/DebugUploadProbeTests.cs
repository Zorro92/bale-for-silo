namespace Bale.Core.Tests;

// Source-contract checks for the debug-only upload bandwidth probe (bale_gpu.dll upload.rs).
public class DebugUploadProbeTests
{
    [Fact]
    public void RouteValidatesFramesAndRunsOffTheRequestThread()
    {
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/upload-probe\"):", server);
        Assert.Contains("uploadFrames is < 1 or > 120", server);
        Assert.Contains("await Task.Run(() => DebugRustProbe.RunUpload(uploadFrames))", server);
        Assert.Contains("GET /upload-probe?frames=20", server);
    }

    [Fact]
    public void InteropChecksTheAbiAndReportsOnlyTypeAndHresult()
    {
        var probe = Read("DebugRustProbe.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", probe);
        Assert.Contains("private static extern uint bale_gpu_upload_probe(uint iterations, byte* report, uint reportCapacity);", probe);
        Assert.Contains("Upload probe: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})", probe);
        Assert.DoesNotContain("ex.Message", probe);
    }

    [Fact]
    public void ScriptExposesTheProbe()
    {
        var script = Read("bale-debug.sh");
        Assert.Contains("scripts/bale-debug.sh upload-probe [frames]", script);
        Assert.Contains("\"$BASE/upload-probe\"", script);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
