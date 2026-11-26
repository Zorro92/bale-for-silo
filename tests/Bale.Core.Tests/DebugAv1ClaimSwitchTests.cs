using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugAv1ClaimSwitchTests
{
    [Fact]
    public void SwitchDefaultsTo2160AndPublishesOnlyValidatedMemoryOnlyValues()
    {
        var previous = DebugAv1ClaimSwitch.MaxHeight;
        try
        {
            Assert.Equal(2160, previous);
            Assert.True(DebugAv1ClaimSwitch.TrySet("2160"));
            var captured = DebugAv1ClaimSwitch.MaxHeight;
            Assert.Equal(2160, captured);
            foreach (var invalid in new string?[] { null, "", "status", "720", "4320", "2160p", "02160", "2160.0", " 2160", "1080 ", "2160&extra=1" })
            {
                Assert.False(DebugAv1ClaimSwitch.TrySet(invalid));
                Assert.Equal(2160, DebugAv1ClaimSwitch.MaxHeight);
            }
            Assert.False(DebugAv1ClaimSwitch.Dav1dToo);
            Assert.True(DebugAv1ClaimSwitch.TrySet("2160-dav1d"));
            Assert.Equal(2160, DebugAv1ClaimSwitch.MaxHeight);
            Assert.True(DebugAv1ClaimSwitch.Dav1dToo);
            Assert.Equal("2160-dav1d", DebugAv1ClaimSwitch.Status);
            Assert.True(DebugAv1ClaimSwitch.TrySet("1080"));
            Assert.Equal(1080, DebugAv1ClaimSwitch.MaxHeight);
            Assert.False(DebugAv1ClaimSwitch.Dav1dToo);
            Assert.Equal("1080", DebugAv1ClaimSwitch.Status);
            Assert.Equal(2160, captured);
        }
        finally { DebugAv1ClaimSwitch.TrySet(previous == 2160 ? "2160" : "1080"); }
        var source = Read("DebugAv1ClaimSwitch.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("private static int _maxHeight = 2160;", source);
        Assert.DoesNotContain("Settings", source);
        Assert.DoesNotContain("Storage", source);
    }

    [Fact]
    public void FfmpegOffStillWithdrawsTheRaisedSoftwareClaim()
    {
        var raised = ConsoleEvidence.Default with { VideoDecode = ConsoleEvidence.BuildVideoDecode(rav1g: true) };
        Assert.Same(raised, DebugFfmpegPlayback.Capabilities("policy", raised));
        var disabled = DebugFfmpegPlayback.Capabilities("off", raised);
        Assert.DoesNotContain("av1", disabled.VideoCodecs);
        Assert.DoesNotContain(disabled.VideoDecode, decode => decode.Codec == "av1");
        Assert.Contains(raised.VideoDecode, decode => decode.Codec == "av1" && decode.MaxHeight == 2160);
    }

    [Fact]
    public void PlayerCapturesTheClaimPerAttemptAndItsDefaultIsTheReleaseClaim()
    {
        var player = Read("PlayerPage.cs.txt");
        const string captureText = "public int Av1ClaimMaxHeight { get; } = DebugAv1ClaimSwitch.MaxHeight;";
        var capture = player.IndexOf(captureText, StringComparison.Ordinal);
        AssertDebugGuard(player, capture);
        Assert.DoesNotContain("DebugAv1ClaimSwitch", player[(capture + captureText.Length)..]);
        var at = player.IndexOf("private static async Task<DeviceCapabilities> DetectClaimsAsync(Attempt attempt)", StringComparison.Ordinal);
        Assert.True(at > 0);
        var claims = player[at..player.IndexOf("private async Task RemountSamePlanAsync(", at, StringComparison.Ordinal)];
        var debug = claims[..claims.IndexOf("#else", StringComparison.Ordinal)];
        var release = claims[claims.IndexOf("#else", StringComparison.Ordinal)..];
        AssertDebugGuard(player, at + claims.IndexOf("PlaybackClaims.DetectAsync(", StringComparison.Ordinal));
        // 2160 (default) with rav1g is the release claim; 1080 or av1-decoder dav1d keep dav1d's 1080p60.
        Assert.Contains("PlaybackClaims.DetectAsync(\n            rav1g: attempt.Av1ClaimMaxHeight == 2160 && attempt.Av1Decoder == \"rav1g\");", debug);
        // 2160-dav1d adds rav1g's entry with dav1d too, keeping the probed hardware claims, before ffmpeg off withdraws it.
        Assert.Contains("var forced = attempt.Av1ClaimDav1d && softwareVideo && !rav1g;", debug);
        var forced = debug.IndexOf("ConsoleEvidence.BuildVideoDecode(await HardwareDecodeProbe.Limits, rav1g: true)", StringComparison.Ordinal);
        var off = debug.IndexOf("capabilities = DebugFfmpegPlayback.Capabilities(attempt.FfmpegMode, capabilities);", StringComparison.Ordinal);
        Assert.True(forced > 0 && forced < off);
        Assert.Contains("player: av1 claim=1080p60 (dav1d; debug av1-claim 1080)", debug);
        Assert.Contains("player: av1 claim=1080p60 (dav1d; debug av1-decoder dav1d)", debug);
        Assert.Equal(1, player.Split("debug av1-claim 2160-dav1d").Length - 1);
        // Release: the production claims as they are, logged once per attempt.
        Assert.Contains("var (capabilities, softwareVideo, rav1g) = await PlaybackClaims.DetectAsync();", release);
        Assert.Contains("player: av1 claim=2160p60 40 Mbps (rav1g)", release);
        Assert.DoesNotContain("DebugAv1ClaimSwitch", Read("DebugServer.Ffmpeg.cs.txt"));
        // The debug URL player takes the same claims.
        Assert.Contains("var capabilities = await DetectClaimsAsync(attempt);", Read("PlayerPage.DebugUrl.cs.txt"));
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
        Assert.Contains("av1-claim={DebugAv1ClaimSwitch.Status}\\n", get);
        Assert.DoesNotContain("TrySet", get);
        var post = routes[routes.IndexOf("case (\"POST\", \"/av1-claim\")", StringComparison.Ordinal)
            ..routes.IndexOf("case (\"GET\", \"/display-match\")", StringComparison.Ordinal)];
        Assert.Contains("query.Count != 1 || !DebugAv1ClaimSwitch.TrySet(query.GetValueOrDefault(\"height\"))", post);
        Assert.Contains("\"400 Bad Request\"", post);
        Assert.Contains("av1-claim={DebugAv1ClaimSwitch.Status}\\n", post);
        Assert.Contains("applies to the next playback attempt", post);
        Assert.Contains("///   GET  /av1-claim", routes);
        Assert.Contains("///   POST /av1-claim?height=2160", routes);
        var notFound = routes.Split('\n').Single(line => line.Contains("\"routes: ", StringComparison.Ordinal));
        Assert.Contains("GET /av1-claim, POST /av1-claim?height={1080|2160|2160-dav1d}", notFound);
        Assert.Contains("scripts/bale-debug.sh av1-claim [1080|2160|2160-dav1d]", Read("bale-debug.sh"));
    }

    private static void AssertDebugGuard(string source, int index)
    {
        Assert.True(index >= 0);
        Assert.True(source.LastIndexOf("#if DEBUG_SERVER", index, StringComparison.Ordinal)
            > source.LastIndexOf("#endif", index, StringComparison.Ordinal));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
