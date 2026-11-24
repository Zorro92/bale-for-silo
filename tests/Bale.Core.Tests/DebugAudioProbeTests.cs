namespace Bale.Core.Tests;

/// <summary>
/// Source guards for the debug audio passthrough probe (docs/audio-passthrough.md, "Capability probe"). What it asks
/// and how it words the answers is tested in Rust (native/bale-gpu/src/audio_probe.rs); the answers need the console.
/// </summary>
public class DebugAudioProbeTests
{
    [Fact]
    public void RouteIsDebugOnlyTakesNoQueryKeysAndIsDocumented()
    {
        var server = Read("DebugServer.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", server);
        var start = server.IndexOf("case (\"GET\", \"/audio-probe\"):", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var route = server[start..server.IndexOf("case (", start + 1, StringComparison.Ordinal)];
        Assert.Contains("if (query.Count != 0) return (\"400 Bad Request\", DebugAudioProbe.Usage);", route);
        Assert.Contains("return await DebugAudioProbe.RunAsync();", route);
        Assert.Contains("///   GET  /audio-probe", server);
        var notFound = server.Split('\n').Single(line => line.Contains("\"routes: ", StringComparison.Ordinal));
        Assert.Contains("GET /caps, GET /audio-probe,", notFound);
    }

    [Fact]
    public void ProbeRunsOnThePoolWithTheModuleItLoadedAndLogsOneSummaryLine()
    {
        var probe = Read("DebugAudioProbe.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", probe);
        Assert.Contains("[DllImport(\"bale_gpu.dll\", ExactSpelling = true)]", probe);
        Assert.Contains("private static extern unsafe uint bale_gpu_audio_probe(nint mmdevapi, byte* report, uint reportCapacity);", probe);
        // Not an unsafe class: RunAsync awaits, which C# forbids in an unsafe context (CS4004).
        Assert.Contains("internal static class DebugAudioProbe", probe);
        // bale_gpu blocks its caller for the activation: a pool (MTA) thread, never the UI thread.
        Assert.Contains("var report = await Task.Run(Probe);", probe);
        Assert.DoesNotContain("OnUiAsync", probe);
        // mmdevapi.dll is loaded here from System32 (else the in-process copy is borrowed) and freed after the call
        // unless borrowed; bale_gpu never imports it.
        var load = probe.IndexOf("NativeLibrary.TryLoad(\"mmdevapi.dll\", typeof(DebugAudioProbe).Assembly, DllImportSearchPath.System32, out mmdevapi)", StringComparison.Ordinal);
        Assert.Contains("fixed (char* name = \"mmdevapi.dll\") mmdevapi = GetModuleHandleW(name);", probe);
        var breadcrumb = probe.IndexOf("Log.Info(\"Debug audio-probe: before bale_gpu_audio_probe\");", StringComparison.Ordinal);
        var call = probe.IndexOf("written = bale_gpu_audio_probe(mmdevapi, report, (uint)buffer.Length);", StringComparison.Ordinal);
        var free = probe.IndexOf("if (mmdevapi != 0 && !borrowed) NativeLibrary.Free(mmdevapi);", StringComparison.Ordinal);
        Assert.True(load >= 0 && breadcrumb > load && call > breadcrumb && free > call);
        // One probe at a time, and the gate always reopens.
        Assert.Contains("if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)", probe);
        Assert.Contains("\"409 Conflict\"", probe);
        Assert.Contains("finally { Interlocked.Exchange(ref _busy, 0); }", probe);
        // The report's own Summary line is what bale.log gets.
        Assert.Contains("private const string SummaryPrefix = \"Summary: \";", probe);
        Assert.Contains("Log.Info(\"Debug audio-probe: \" + (summary is null ? \"no summary\" : summary[SummaryPrefix.Length..]));", probe);
        // Exceptions are reported by type and HRESULT only, never by message.
        Assert.DoesNotContain("ex.Message", probe);
    }

    [Fact]
    public void ScriptAndPackagedDllChecksKnowTheProbe()
    {
        var script = Read("bale-debug.sh");
        Assert.Contains("#   scripts/bale-debug.sh audio-probe ", script);
        Assert.Contains("  audio-probe)\n", script);
        Assert.Contains("curl --fail-with-body -sS --max-time 15 \"$BASE/audio-probe\"", script);
        var workflow = Read("ci.yml.txt");
        Assert.Contains("'bale_gpu_d3d11_probe', 'bale_gpu_audio_probe',", workflow);
        Assert.Contains("if ($dependents -match '(?i)mmdevapi\\.dll') { throw 'bale_gpu.dll must not import mmdevapi.dll' }", workflow);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
