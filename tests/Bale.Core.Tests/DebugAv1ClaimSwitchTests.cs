using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugAv1ClaimSwitchTests
{
    [Fact]
    public void SwitchDefaultsTo1080AndPublishesOnlyValidatedMemoryOnlyValues()
    {
        var previous = DebugAv1ClaimSwitch.MaxHeight;
        try
        {
            Assert.Equal(1080, previous);
            Assert.True(DebugAv1ClaimSwitch.TrySet("2160"));
            var captured = DebugAv1ClaimSwitch.MaxHeight;
            Assert.Equal(2160, captured);
            foreach (var invalid in new string?[] { null, "", "status", "720", "4320", "2160p", "02160", "2160.0", " 2160", "1080 ", "2160&extra=1" })
            {
                Assert.False(DebugAv1ClaimSwitch.TrySet(invalid));
                Assert.Equal(2160, DebugAv1ClaimSwitch.MaxHeight);
            }
            Assert.True(DebugAv1ClaimSwitch.TrySet("1080"));
            Assert.Equal(1080, DebugAv1ClaimSwitch.MaxHeight);
            Assert.Equal(2160, captured);
        }
        finally { DebugAv1ClaimSwitch.TrySet(previous == 2160 ? "2160" : "1080"); }
        var source = Read("DebugAv1ClaimSwitch.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("private static int _maxHeight = 1080;", source);
        Assert.DoesNotContain("Settings", source);
        Assert.DoesNotContain("Storage", source);
    }

    [Fact]
    public void FfmpegOffStillWithdrawsTheRaisedSoftwareClaim()
    {
        var raised = ConsoleEvidence.Default with { VideoDecode = ConsoleEvidence.BuildVideoDecode(3840, 2160) };
        Assert.Same(raised, DebugFfmpegPlayback.Capabilities("policy", raised));
        var disabled = DebugFfmpegPlayback.Capabilities("off", raised);
        Assert.DoesNotContain("av1", disabled.VideoCodecs);
        Assert.DoesNotContain(disabled.VideoDecode, decode => decode.Codec == "av1");
        Assert.Contains(raised.VideoDecode, decode => decode.Codec == "av1" && decode.MaxHeight == 2160);
    }

    [Fact]
    public void PlayerCapturesTheClaimPerAttemptAndRaisesItOnlyInDebugBuilds()
    {
        var player = Read("PlayerPage.cs.txt");
        const string captureText = "public int Av1ClaimMaxHeight { get; } = DebugAv1ClaimSwitch.MaxHeight;";
        var capture = player.IndexOf(captureText, StringComparison.Ordinal);
        AssertDebugGuard(player, capture);
        Assert.DoesNotContain("DebugAv1ClaimSwitch", player[(capture + captureText.Length)..]);
        var raised = player.IndexOf("if (attempt.Av1ClaimMaxHeight == 2160 && softwareVideo)", StringComparison.Ordinal);
        var off = player.IndexOf("capabilities = DebugFfmpegPlayback.Capabilities(attempt.FfmpegMode, capabilities);", StringComparison.Ordinal);
        AssertDebugGuard(player, raised);
        Assert.True(raised < off);
        // The raised AV1 bound keeps the probed hardware claims.
        Assert.Contains("capabilities = capabilities with { VideoDecode = ConsoleEvidence.BuildVideoDecode(await HardwareDecodeProbe.Limits, 3840, 2160) };", player[raised..off]);
        var log = player.IndexOf("Log.Info(\"player: av1 claim=2160p (debug)\");", StringComparison.Ordinal);
        AssertDebugGuard(player, log);
        Assert.Contains("if (attempt.Av1ClaimMaxHeight == 2160 && attempt.FfmpegEnabled)", player[off..log]);
        Assert.DoesNotContain("DebugAv1ClaimSwitch", Read("DebugServer.Ffmpeg.cs.txt"));
    }

    [Fact]
    public void RoutesRejectUnexpectedAndDuplicateQueryKeysAndDocumentBothMethods()
    {
        var routes = Read("DebugServer.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", routes);
        var duplicateGuard = routes.Split('\n').Single(line => line.Contains("duplicateQueryKeys && path is", StringComparison.Ordinal));
        Assert.Contains("\"/av1-claim\"", duplicateGuard);
        var get = routes[routes.IndexOf("case (\"GET\", \"/av1-claim\")", StringComparison.Ordinal)
            ..routes.IndexOf("case (\"POST\", \"/av1-claim\")", StringComparison.Ordinal)];
        Assert.Contains("if (query.Count != 0)", get);
        Assert.Contains("\"400 Bad Request\"", get);
        Assert.Contains("av1-claim={DebugAv1ClaimSwitch.MaxHeight}\\n", get);
        Assert.DoesNotContain("TrySet", get);
        var post = routes[routes.IndexOf("case (\"POST\", \"/av1-claim\")", StringComparison.Ordinal)
            ..routes.IndexOf("case (\"GET\", \"/display-match\")", StringComparison.Ordinal)];
        Assert.Contains("query.Count != 1 || !DebugAv1ClaimSwitch.TrySet(query.GetValueOrDefault(\"height\"))", post);
        Assert.Contains("\"400 Bad Request\"", post);
        Assert.Contains("av1-claim={DebugAv1ClaimSwitch.MaxHeight}\\n", post);
        Assert.Contains("applies to the next playback attempt", post);
        Assert.Contains("///   GET  /av1-claim", routes);
        Assert.Contains("///   POST /av1-claim?height=2160", routes);
        var notFound = routes.Split('\n').Single(line => line.Contains("\"routes: ", StringComparison.Ordinal));
        Assert.Contains("GET /av1-claim, POST /av1-claim?height={1080|2160}", notFound);
        Assert.Contains("scripts/bale-debug.sh av1-claim [1080|2160]", Read("bale-debug.sh"));
    }

    private static void AssertDebugGuard(string source, int index)
    {
        Assert.True(index >= 0);
        Assert.True(source.LastIndexOf("#if DEBUG_SERVER", index, StringComparison.Ordinal)
            > source.LastIndexOf("#endif", index, StringComparison.Ordinal));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
