using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public sealed class DebugOutputDepthSwitchTests
{
    [Fact]
    public void DefaultsToDisplayAndRejectsInvalidModesWithoutChangingTheValue()
    {
        var previous = DebugOutputDepthSwitch.Mode;
        try
        {
            Assert.Equal("display", DebugOutputDepthSwitch.Mode);
            Assert.True(DebugOutputDepthSwitch.TrySet("source"));
            Assert.Equal("source", DebugOutputDepthSwitch.Mode);
            foreach (var invalid in new string?[] { null, "", "auto", "default", "bgra", "DISPLAY", " source", "source " })
            {
                Assert.False(DebugOutputDepthSwitch.TrySet(invalid));
                Assert.Equal("source", DebugOutputDepthSwitch.Mode);
            }
            Assert.True(DebugOutputDepthSwitch.TrySet("display"));
            Assert.Equal("display", DebugOutputDepthSwitch.Mode);
        }
        finally { DebugOutputDepthSwitch.TrySet(previous); }
    }

    [Fact]
    public void EngineTargetOverrideIsAppliedAfterTheOutputDepthPolicy()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        var target = media[media.IndexOf("private static uint EngineTarget", StringComparison.Ordinal)..];
        // The policy (and with it the stage's output depth) is decided in one place, ceiling first.
        var decide = media[media.IndexOf("private static EngineOutputDecision DecideEngineOutput", StringComparison.Ordinal)..];
        Assert.Contains("var ceiling = \"display\";", decide);
        Assert.Contains("#if DEBUG_SERVER\n        ceiling = DebugOutputDepthSwitch.Mode;\n#endif", decide);
        Assert.True(decide.IndexOf("DebugOutputDepthSwitch.Mode", StringComparison.Ordinal)
            < decide.IndexOf("EngineOutputPolicy.Decide", StringComparison.Ordinal));
        Assert.Contains("media.EngineOutput ?? DecideEngineOutput(attempt, media, stageNv12Dithered: false)", target);
        Assert.True(target.IndexOf("var code = output.Code;", StringComparison.Ordinal)
            < target.IndexOf("if (DebugEngineTargetSwitch.Code is { } forced)", StringComparison.Ordinal));
        Assert.Contains("code = forced;", target);
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/output-depth\"):", server);
        Assert.Contains("case (\"POST\", \"/output-depth\"):", server);
        Assert.Contains("query.Count != 1 || !DebugOutputDepthSwitch.TrySet(query.GetValueOrDefault(\"mode\"))", server);
        Assert.Contains("output-depth={DebugOutputDepthSwitch.Mode}", server);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
