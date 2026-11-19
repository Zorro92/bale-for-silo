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
        Assert.Contains("var ceiling = \"display\";", target);
        Assert.Contains("#if DEBUG_SERVER\n        ceiling = DebugOutputDepthSwitch.Mode;\n#endif", target);
        Assert.True(target.IndexOf("DebugOutputDepthSwitch.Mode", StringComparison.Ordinal)
            < target.IndexOf("EngineOutputPolicy.Decide", StringComparison.Ordinal));
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
