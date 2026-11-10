using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugAv1FpsSwitchTests
{
    [Fact]
    public void SwitchDefaultsTo30AndAcceptsOnly30Or60()
    {
        var previous = DebugAv1FpsSwitch.MaxFps;
        try
        {
            Assert.Equal(30, previous);
            Assert.True(DebugAv1FpsSwitch.TrySet("60"));
            Assert.Equal(60, DebugAv1FpsSwitch.MaxFps);
            foreach (var invalid in new string?[] { null, "", "status", "24", "120", "60.0", " 60", "060", "60&extra=1" })
            {
                Assert.False(DebugAv1FpsSwitch.TrySet(invalid));
                Assert.Equal(60, DebugAv1FpsSwitch.MaxFps);
            }
            Assert.True(DebugAv1FpsSwitch.TrySet("30"));
            Assert.Equal(30, DebugAv1FpsSwitch.MaxFps);
        }
        finally { DebugAv1FpsSwitch.TrySet(previous == 60 ? "60" : "30"); }
    }

    [Fact]
    public void PlayerRaisesTheFrameRateOnlyInDebugBuildsWithSoftwareVideo()
    {
        var player = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlayerPage.cs.txt"));
        var capture = player.IndexOf("public int Av1ClaimMaxFps { get; } = DebugAv1FpsSwitch.MaxFps;", StringComparison.Ordinal);
        var raised = player.IndexOf("if (attempt.Av1ClaimMaxFps == 60 && softwareVideo)", StringComparison.Ordinal);
        foreach (var index in new[] { capture, raised })
        {
            Assert.True(index >= 0);
            Assert.True(player.LastIndexOf("#if DEBUG_SERVER", index, StringComparison.Ordinal)
                > player.LastIndexOf("#endif", index, StringComparison.Ordinal));
        }
        var routes = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.cs.txt"));
        Assert.Contains("query.Count != 1 || !DebugAv1FpsSwitch.TrySet(query.GetValueOrDefault(\"fps\"))", routes);
        Assert.Contains("or \"/av1-fps\" or", routes);
    }
}
