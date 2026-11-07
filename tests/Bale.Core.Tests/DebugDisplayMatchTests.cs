using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugDisplayMatchTests
{
    [Fact]
    public void SwitchDefaultsToOffAndPublishesOnlyValidatedMemoryOnlyValues()
    {
        var previous = DebugDisplayMatch.Mode;
        try
        {
            Assert.False(DebugDisplayMatch.Enabled);
            foreach (var mode in new[] { "on", "off" })
            {
                Assert.True(DebugDisplayMatch.TrySet(mode));
                Assert.Equal(mode, DebugDisplayMatch.Mode);
                Assert.Equal(mode == "on", DebugDisplayMatch.Enabled);
            }
            foreach (var invalid in new string?[] { null, "", "status", "true", "ON", " on", "off " })
            {
                Assert.False(DebugDisplayMatch.TrySet(invalid));
                Assert.Equal("off", DebugDisplayMatch.Mode);
            }
        }
        finally { DebugDisplayMatch.TrySet(previous); }
        var source = Read("DebugDisplayMatch.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("private static int _enabled;", source);
        Assert.DoesNotContain("Settings", source);
        Assert.DoesNotContain("Storage", source);
    }

    [Fact]
    public void PlayerCapturesTheSwitchPerAttemptAndMatchesBeforeOpeningMedia()
    {
        var player = Read("PlayerPage.cs.txt");
        Assert.Contains("public bool MatchContentFrameRate { get; } = DebugDisplayMatch.Enabled;", player);
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("else if (attempt.MatchContentFrameRate) await _hdrOutput.TryMatchContentAsync(attempt.Plan!);", media);
        Assert.True(media.IndexOf("await _hdrOutput.TryMatchContentAsync", StringComparison.Ordinal)
            < media.IndexOf("Playback.OpenMedia", StringComparison.Ordinal));
        var stats = Read("PlayerPage.Stats.cs.txt");
        Assert.Contains("(\"Display mode\", DisplayHdrOutput.CurrentModeText())", stats);
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/display-match\")", server);
        Assert.Contains("case (\"POST\", \"/display-match\")", server);
        Assert.Contains("DebugDisplayMatch.TrySet(query.GetValueOrDefault(\"mode\"))", server);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
