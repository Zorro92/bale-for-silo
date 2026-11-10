namespace Bale.Core.Tests;

public class DebugSubtitleProbeTests
{
    [Fact]
    public void ProbeIsDebugOnlyCountsCuesWithoutRenderingAndNeverPrintsExceptionText()
    {
        var source = Read("DebugServer.Subtitles.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("TimedMetadataTrackPresentationMode.ApplicationPresented", source);
        Assert.DoesNotContain("PlatformPresented", source);
        // Shares the playtest gate so two isolated players never run at once.
        Assert.Contains("Interlocked.CompareExchange(ref _playtestBusy, 1, 0)", source);
        Assert.Contains("DisposeLateFfmpegSourceAsync(creation)", source);
        Assert.DoesNotContain("ex.Message", source);
        Assert.DoesNotContain("Log.Error", source);
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/subtest\"):", server);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
