using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugHdrPlaybackTests
{
    [Fact]
    public void ModeIsMemoryOnlyAndDefaultsToOff()
    {
        var previous = DebugHdrPlayback.Mode;
        try
        {
            foreach (var mode in new[] { "pretend", "off" })
            {
                Assert.True(DebugHdrPlayback.TrySet(mode));
                Assert.Equal(mode, DebugHdrPlayback.Mode);
                Assert.Equal(mode == "pretend", DebugHdrPlayback.Pretend);
            }
            foreach (var invalid in new string?[] { null, "", "status", "on", "Pretend", " pretend" })
            {
                Assert.False(DebugHdrPlayback.TrySet(invalid));
                Assert.Equal("off", DebugHdrPlayback.Mode);
            }
        }
        finally { DebugHdrPlayback.TrySet(previous); }
        var source = Read("DebugHdrPlayback.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("private static string _mode = \"off\"", source);
        Assert.DoesNotContain("Settings", source);
        Assert.DoesNotContain("Storage", source);
    }

    [Theory]
    [InlineData("policy", "unknown")]
    [InlineData("policy", "exact")]
    [InlineData("off", "unknown")]
    [InlineData("off", "exact")]
    public void PretendChangesOnlyHdrAndKeepsTheFfmpegPolicy(string mode, string evidence)
    {
        var detected = ConsoleEvidence.Default with
        {
            Hdr = new() { Hdr10Plus = true, Hlg = true, DolbyVisionProfiles = [5] },
            DisplayHdrEvidence = evidence,
        };
        var capabilities = DebugFfmpegPlayback.Capabilities(mode, detected);
        Assert.Same(capabilities, DebugHdrPlayback.Capabilities(false, capabilities));
        var pretend = DebugHdrPlayback.Capabilities(true, capabilities);
        Assert.True(pretend.Hdr.Hdr10);
        Assert.False(pretend.Hdr.Hdr10Plus);
        Assert.False(pretend.Hdr.Hlg);
        Assert.Empty(pretend.Hdr.DolbyVisionProfiles);
        Assert.Equal("exact", pretend.DisplayHdrEvidence);
        Assert.Equal(capabilities, pretend with { Hdr = capabilities.Hdr, DisplayHdrEvidence = capabilities.DisplayHdrEvidence });
        Assert.Equal(mode == "policy", pretend.VideoCodecs.Contains("av1"));
        Assert.False(detected.Hdr.Hdr10);
        Assert.Equal(evidence, detected.DisplayHdrEvidence);

        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null,
            PlaybackSelection.Auto, pretend, PlaybackTestSupport.Identity);
        Assert.True(start.ClientCapabilities.Hdr);
        Assert.Same(pretend.Hdr, start.ClientCapabilities.HdrDetails);
        Assert.Equal("exact", start.ClientPlaybackContext.Output.Display!.HdrEvidence);
        Assert.Same(pretend.Hdr, start.ClientPlaybackContext.Output.Display.HdrTypes);
        Assert.Same(pretend.Hdr, start.ClientPlaybackContext.Output.HdrDetails);
        Assert.All(start.ClientPlaybackContext.Deliveries.Values, delivery => Assert.Same(pretend.Hdr, delivery.HdrDetails));
    }

    [Theory]
    [InlineData(true, "hdr10", "console tone map (debug)")]
    [InlineData(false, "hdr10", "HDR10 HDMI")]
    [InlineData(true, "sdr", "SDR (HDR pretend debug armed)")]
    [InlineData(false, "sdr", "SDR / server tone map")]
    public void HudDescribesTheAttemptAndActualPlan(bool pretend, string range, string expected)
    {
        var plan = new PlaybackPlan { Delivery = "original_http", Source = new PlaybackSource { DynamicRange = range } };
        Assert.Equal(expected, DebugHdrPlayback.Output(pretend, plan));
    }

    [Fact]
    public void OverrideIsCapturedBeforePlanningAndBypassesHdmiOnlyInDebug()
    {
        var player = Read("PlayerPage.cs.txt");
        Assert.Contains("public bool PretendHdr { get; } = DebugHdrPlayback.Pretend", player);
        Assert.Contains("capabilities = DebugHdrPlayback.Capabilities(attempt.PretendHdr, capabilities)", player);
        Assert.True(player.IndexOf("DebugFfmpegPlayback.Capabilities(attempt.FfmpegMode", StringComparison.Ordinal)
            < player.IndexOf("DebugHdrPlayback.Capabilities(attempt.PretendHdr", StringComparison.Ordinal));
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("#if DEBUG_SERVER\n        if (attempt.PretendHdr)", media);
        Assert.Contains("else\n#endif\n        if (DisplayHdrPolicy.RequiresHdr10", media);
        var bypass = media[media.IndexOf("if (attempt.PretendHdr)", StringComparison.Ordinal)
            ..media.IndexOf("else\n#endif", StringComparison.Ordinal)];
        Assert.DoesNotContain("_hdrOutput", bypass);
        Assert.DoesNotContain("Recover", bypass);
        var stats = Read("PlayerPage.Stats.cs.txt");
        Assert.Contains("DebugHdrPlayback.Output(_attempt?.PretendHdr == true, _attempt?.Plan)", stats);
        Assert.Contains("(\"HDR output\", DebugHdrOutput)", stats);
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/hdr\")", server);
        Assert.Contains("case (\"POST\", \"/hdr\")", server);
        Assert.Contains("Append(player.DebugHdrOutput)", server);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
