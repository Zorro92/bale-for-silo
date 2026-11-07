using System.Diagnostics;

namespace Bale.Core.Tests;

public class DebugPlaytestScriptTests
{
    [Theory]
    [InlineData("video")]
    [InlineData("gpu")]
    [InlineData("audio")]
    [InlineData("display")]
    [InlineData("platform")]
    [InlineData("device")]
    [InlineData("graph")]
    public async Task CapsForwardsEachDocumentedSection(string section)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["caps", section]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/caps\n", result.Arguments);
        Assert.Contains("--data-urlencode\nonly=" + section + "\n", result.Arguments);
    }

    [Theory]
    [InlineData("playtest", "3", null, 12)]
    [InlineData("playtest", "10", null, 19)]
    [InlineData("playtest-ffmpeg", "60", "software", 110)]
    [InlineData("playtest-engine", "60", null, 90)]
    [InlineData("playtest-engine", "3", null, 33)]
    [InlineData("playtest-engine", "3", "rust", 33)]
    [InlineData("playtest-engine", "60", "cs", 90)]
    [InlineData("playtest-engine", "10", "rust-ffmpeg", 70)]
    [InlineData("playtest-ffmpeg", "1", "system", 51)]
    public async Task WrapperUsesTheCorrectRouteEncodingModeAndOpenBudget(string command, string seconds, string? mode, int timeout)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(mode is null ? [command, "https://example.org/clip", seconds]
            : [command, "https://example.org/clip", seconds, mode]);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Arguments.Contains("--max-time\n" + timeout + "\n", StringComparison.Ordinal));
        Assert.True(result.Arguments.Contains("/" + command + "\n", StringComparison.Ordinal));
        Assert.True(result.Arguments.Contains("--data-urlencode\nurl=", StringComparison.Ordinal));
        if (mode is not null) Assert.Contains("mode=" + mode, result.Arguments);
        Assert.DoesNotContain("example.org", result.Output);
    }

    [Fact]
    public async Task PrivateUrlFileIsPassedDirectlyToCurlWithoutReadingOrEchoingItsContent()
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["playtest-ffmpeg", "@fixture", "30", "software"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--data-urlencode\nurl@", result.Arguments);
        Assert.DoesNotContain("not-a-real-grant", result.Arguments);
        Assert.DoesNotContain("not-a-real-grant", result.Output);
        Assert.DoesNotContain("example.org", result.Output);
        Assert.Contains("mode=software", result.Arguments);
    }

    [Theory]
    [InlineData("threads=0", "10bit=off", "rate=0.5")]
    [InlineData("rate=2.0", "threads=16", "10bit=on")]
    [InlineData("10bit=off", "rate=1.25", "threads=4")]
    public async Task FfmpegTuningIsEncodedInAnyOrderAndAppliedToEverySuiteSample(string first, string second, string third)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["playtest-ffmpeg", "@fixture", "30", "software", first, second, third]);
        Assert.Equal(0, result.ExitCode);
        foreach (var option in new[] { first, second, third }) Assert.Contains("--data-urlencode\n" + option + "\n", result.Arguments);
        Assert.DoesNotContain("not-a-real-grant", result.Output + result.Arguments);
        var suite = await RunAsync(["playtest-ffmpeg", "suite", "30", "software", first, second, third]);
        Assert.Equal(0, suite.ExitCode);
        foreach (var option in new[] { first, second, third })
            Assert.Equal(11, suite.Arguments.Split('\n').Count(line => line == option));
    }

    [Theory]
    [InlineData("threads=0", "ahead=0", "10bit=off", "prio=0", "sws=1", "rate=0.5", "delay=0", "maxres=0", "dpb=0")]
    [InlineData("prio=2", "maxres=1", "delay=64", "rate=2.0", "sws=16", "10bit=on", "dpb=16", "ahead=64", "threads=16")]
    [InlineData("sws=04", "10bit=off", "ahead=0008", "dpb=003", "threads=4", "delay=001", "rate=0.75", "prio=001", "maxres=001")]
    [InlineData("maxres=default", "threads=4", "sws=4", "10bit=on", "ahead=8", "dpb=default", "rate=1", "prio=1", "delay=8")]
    public async Task AllNineProbeTuningKeysWorkTogetherInAnyOrderAndForEverySuiteSample(string first, string second,
        string third, string fourth, string fifth, string sixth, string seventh, string eighth, string ninth)
    {
        if (!OperatingSystem.IsLinux()) return;
        string[] options = [first, second, third, fourth, fifth, sixth, seventh, eighth, ninth];
        foreach (var target in new[] { "@fixture", "suite" })
        {
            var result = await RunAsync(["playtest-ffmpeg", target, "30", "software", .. options]);
            Assert.Equal(0, result.ExitCode);
            foreach (var option in options)
                Assert.Equal(target == "suite" ? 11 : 1, result.Arguments.Split('\n').Count(line => line == option));
            Assert.DoesNotContain("not-a-real-grant", result.Output + result.Arguments);
        }
    }

    [Theory]
    [InlineData("ahead", 0, 64)]
    [InlineData("sws", 1, 16)]
    [InlineData("delay", 0, 64)]
    [InlineData("prio", 0, 2)]
    [InlineData("maxres", 0, 1)]
    [InlineData("dpb", 0, 16)]
    public async Task InvalidForkTuningAndDuplicatesNeverInvokeCurlOnEitherPath(string key, int minimum, int maximum)
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var value in new[] { "-1", "+1", " 1", "1 ", "1.0", "1,0", "1e0", "0x1", "", "DEFAULT", "2147483648",
            (minimum - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (maximum + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            foreach (var args in new[]
            {
                new[] { "playtest-ffmpeg", "@fixture", "30", "software", key + "=" + value },
                ["ffmpeg-tune", "readahead=on", key + "=" + value],
            })
            {
                var result = await RunAsync(args);
                Assert.Equal(2, result.ExitCode);
                Assert.Empty(result.Arguments);
            }
        }
        List<string[]> invalidArguments =
        [
            ["playtest-ffmpeg", "@fixture", "30", "software", key + "=1", key + "=1"],
            ["ffmpeg-tune", key + "=1", key + "=default"],
            ["playtest-ffmpeg", "@fixture", "30", "software", key + "=1", key + "=default"],
        ];
        // Only the system-decoder hints accept default in the one-shot probe.
        if (key is not ("maxres" or "dpb"))
            invalidArguments.Add(["playtest-ffmpeg", "@fixture", "30", "software", key + "=default"]);
        foreach (var args in invalidArguments)
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Arguments);
        }
    }

    [Theory]
    [InlineData("hdr=on")]
    [InlineData("hdr=off")]
    [InlineData("hdr=default")]
    public async Task HdrProbeOverrideWorksAlongsideAllOtherTuningKeys(string hdr)
    {
        if (!OperatingSystem.IsLinux()) return;
        string[] options = ["threads=4", "10bit=on", "rate=1", "ahead=8", "sws=4", "delay=8", "prio=1", "maxres=default", "dpb=default", hdr];
        var result = await RunAsync(["playtest-ffmpeg", "@fixture", "30", "software", .. options]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--data-urlencode\n" + hdr + "\n", result.Arguments);
        Assert.DoesNotContain("not-a-real-grant", result.Output + result.Arguments);
    }

    [Theory]
    [InlineData("grain=on")]
    [InlineData("grain=off")]
    [InlineData("grain=default")]
    public async Task GrainProbeOverrideWorksAlongsideHdr(string grain)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["playtest-ffmpeg", "@fixture", "30", "software", "hdr=on", grain]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--data-urlencode\n" + grain + "\n", result.Arguments);
    }

    [Theory]
    [InlineData("grain=off")]
    [InlineData("grain=on")]
    [InlineData("grain=default")]
    public async Task GrainTuneOverrideIsSentToTheRealPlayer(string grain)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["ffmpeg-tune", "prio=2", grain]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/ffmpeg-tune\n", result.Arguments);
        Assert.Contains("--data-urlencode\n" + grain + "\n", result.Arguments);
    }

    [Theory]
    [InlineData("ffmpeg-tune", "grain=OFF")]
    [InlineData("ffmpeg-tune", "grain=1")]
    [InlineData("playtest-ffmpeg", "grain=OFF")]
    [InlineData("playtest-ffmpeg", "grain=1")]
    [InlineData("playtest-engine", "csharp")]
    [InlineData("playtest-engine", "RUST")]
    public async Task InvalidGrainOrEngineModeNeverInvokesCurl(string command, string option)
    {
        if (!OperatingSystem.IsLinux()) return;
        string[] args = command switch
        {
            "playtest-engine" => [command, "@fixture", "3", option],
            "ffmpeg-tune" => [command, option],
            _ => [command, "@fixture", "30", "software", option],
        };
        var result = await RunAsync(args);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Arguments);
    }

    [Theory]
    [InlineData("hdr=")]
    [InlineData("hdr=ON")]
    [InlineData("hdr=1")]
    [InlineData("hdr=pretend")]
    [InlineData("hdr=on hdr=off")]
    public async Task InvalidHdrProbeOverrideNeverInvokesCurl(string option)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["playtest-ffmpeg", "@fixture", "30", "software", .. option.Split(' ')]);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Arguments);
        var tune = await RunAsync(["ffmpeg-tune", "hdr=on"]);
        Assert.Equal(2, tune.ExitCode);
        Assert.Empty(tune.Arguments);
    }

    [Fact]
    public async Task OmittedTuningDoesNotSendNativeOverrides()
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["playtest-ffmpeg", "@fixture"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("seconds=3", result.Arguments);
        Assert.Contains("mode=auto", result.Arguments);
        Assert.DoesNotContain("threads=", result.Arguments);
        Assert.DoesNotContain("10bit=", result.Arguments);
        Assert.DoesNotContain("rate=", result.Arguments);
        foreach (var key in new[] { "ahead", "sws", "delay", "prio", "maxres", "dpb" }) Assert.DoesNotContain("\n" + key + "=", result.Arguments);
        var rateOnly = await RunAsync(["playtest-ffmpeg", "@fixture", "30", "software", "rate=0.75"]);
        Assert.Equal(0, rateOnly.ExitCode);
        Assert.Contains("rate=0.75", rateOnly.Arguments);
        Assert.DoesNotContain("threads=", rateOnly.Arguments);
        Assert.DoesNotContain("10bit=", rateOnly.Arguments);
        foreach (var key in new[] { "ahead", "sws", "delay", "prio", "maxres", "dpb" }) Assert.DoesNotContain(key + "=", rateOnly.Arguments);
    }

    [Theory]
    [InlineData("threads=-1")]
    [InlineData("threads=17")]
    [InlineData("threads=18446744073709551616")]
    [InlineData("threads=")]
    [InlineData("10bit=true")]
    [InlineData("10bit=OFF")]
    [InlineData("rate=0.49")]
    [InlineData("rate=2.01")]
    [InlineData("rate=NaN")]
    [InlineData("rate=1,5")]
    [InlineData("rate=1e0")]
    [InlineData("rate=")]
    [InlineData("unknown=1")]
    [InlineData("")]
    public async Task InvalidTuningNeverInvokesCurl(string option)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["playtest-ffmpeg", "@fixture", "30", "software", option]);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public async Task DuplicateTuningAndTuningWithoutSecondsAndModeNeverInvokeCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[]
        {
            new[] { "playtest-ffmpeg", "@fixture", "30", "software", "threads=4", "threads=8" },
            ["playtest-ffmpeg", "@fixture", "30", "rate=1.5"],
            ["playtest-ffmpeg", "@fixture", "rate=1.5"],
        })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Arguments);
        }
    }

    [Theory]
    [InlineData("playtest", "11", "auto")]
    [InlineData("playtest-ffmpeg", "61", "auto")]
    [InlineData("playtest-ffmpeg", "0", "auto")]
    [InlineData("playtest-ffmpeg", "30", "hardware")]
    public async Task InvalidArgumentsNeverInvokeCurl(string command, string seconds, string mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var args = command == "playtest" ? new[] { command, "@fixture", seconds } : [command, "@fixture", seconds, mode];
        var result = await RunAsync(args);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public async Task BothSuitesUseTheSameElevenSamplesAndOnlyPrintSampleIds()
    {
        if (!OperatingSystem.IsLinux()) return;
        var builtin = await RunAsync(["playtest", "suite", "3"]);
        var ffmpeg = await RunAsync(["playtest-ffmpeg", "suite", "3", "auto"]);
        Assert.Equal(0, builtin.ExitCode);
        Assert.Equal(0, ffmpeg.ExitCode);
        var urls = builtin.Arguments.Split('\n').Where(line => line.StartsWith("url=", StringComparison.Ordinal)).ToArray();
        Assert.Equal(11, urls.Length);
        Assert.Equal(urls, ffmpeg.Arguments.Split('\n').Where(line => line.StartsWith("url=", StringComparison.Ordinal)));
        Assert.DoesNotContain("https://", builtin.Output);
        Assert.DoesNotContain("https://", ffmpeg.Output);
        Assert.Equal(builtin.Output, ffmpeg.Output);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("policy")]
    public async Task FfmpegTogglePostsOnlyAValidatedMode(string mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["ffmpeg", mode]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/ffmpeg\n", result.Arguments);
        Assert.Contains("-X\nPOST\n", result.Arguments);
        Assert.Contains("--data-urlencode\nmode=" + mode, result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
    }

    [Fact]
    public async Task InvalidFfmpegToggleArgumentsNeverReachCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[] { new[] { "ffmpeg" }, ["ffmpeg", "hardware"], ["ffmpeg", "Software"], ["ffmpeg", "auto"], ["ffmpeg", "software"], ["ffmpeg", "system"], ["ffmpeg", "policy", "extra"] })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Arguments);
        }
    }

    [Fact]
    public async Task FfmpegInputTuningWithoutArgumentsOnlyReadsStatus()
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["ffmpeg-tune"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/ffmpeg-tune\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        Assert.DoesNotContain("POST", result.Arguments);
        Assert.DoesNotContain("--data-urlencode", result.Arguments);
    }

    [Theory]
    [InlineData("readahead=on", "readsize=16384", "prefetch=2")]
    [InlineData("prefetch=16", "readahead=off", "readsize=1048576")]
    [InlineData("readsize=default", "prefetch=default", "readahead=default")]
    [InlineData("ahead=0", "sws=1", "delay=0")]
    [InlineData("delay=64", "ahead=64", "sws=16")]
    [InlineData("sws=04", "delay=001", "ahead=0008")]
    [InlineData("delay=default", "sws=default", "ahead=default")]
    [InlineData("prio=0", "sws=4", "ahead=16")]
    [InlineData("ahead=8", "prio=2", "delay=1")]
    [InlineData("prio=001", "delay=001", "ahead=0008")]
    [InlineData("delay=default", "prio=default", "ahead=default")]
    [InlineData("maxres=0", "dpb=0", "prio=0")]
    [InlineData("dpb=16", "maxres=1", "prio=2")]
    [InlineData("maxres=001", "prio=001", "dpb=003")]
    [InlineData("dpb=default", "maxres=default", "prio=default")]
    public async Task FfmpegInputTuningPostsValidatedOptionsInAnyOrder(string first, string second, string third)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["ffmpeg-tune", first, second, third]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/ffmpeg-tune\n", result.Arguments);
        Assert.Contains("-X\nPOST\n", result.Arguments);
        foreach (var option in new[] { first, second, third }) Assert.Contains("--data-urlencode\n" + option + "\n", result.Arguments);
    }

    [Fact]
    public async Task FfmpegInputTuningDoesNotSendOmittedKeys()
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["ffmpeg-tune", "readahead=on"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("readahead=on", result.Arguments);
        Assert.DoesNotContain("readsize=", result.Arguments);
        Assert.DoesNotContain("prefetch=", result.Arguments);
        foreach (var key in new[] { "ahead", "sws", "delay", "prio", "maxres", "dpb" }) Assert.DoesNotContain("\n" + key + "=", result.Arguments);
    }

    [Theory]
    [InlineData("readahead=ON")]
    [InlineData("readahead=true")]
    [InlineData("readahead=")]
    [InlineData("readsize=16383")]
    [InlineData("readsize=1048577")]
    [InlineData("readsize=18446744073709551616")]
    [InlineData("readsize=16KB")]
    [InlineData("readsize=-16384")]
    [InlineData("readsize=")]
    [InlineData("prefetch=1")]
    [InlineData("prefetch=17")]
    [InlineData("prefetch=18446744073709551616")]
    [InlineData("prefetch=2.0")]
    [InlineData("prefetch=")]
    [InlineData("unknown=1")]
    [InlineData("=on")]
    [InlineData("")]
    public async Task InvalidFfmpegInputTuningNeverReachesCurl(string option)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["ffmpeg-tune", "readahead=on", option]);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Arguments);
    }

    [Theory]
    [InlineData("readahead=on", "readahead=off")]
    [InlineData("readsize=16384", "readsize=65536")]
    [InlineData("prefetch=2", "prefetch=8")]
    public async Task DuplicateFfmpegInputTuningNeverReachesCurl(string first, string second)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["ffmpeg-tune", first, second]);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Arguments);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("status")]
    [InlineData(null)]
    public async Task DisplayMatchingValidatesModesAndDefaultsToReadOnlyStatus(string? mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(mode is null ? ["display-match"] : ["display-match", mode]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/display-match\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        if (mode is null or "status")
        {
            Assert.DoesNotContain("-X\nPOST", result.Arguments);
            Assert.DoesNotContain("--data-urlencode", result.Arguments);
        }
        else
        {
            Assert.Contains("-X\nPOST\n", result.Arguments);
            Assert.Contains("--data-urlencode\nmode=" + mode + "\n", result.Arguments);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryReadoutIsAGetWithOptionalForcedCollection(bool collect)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(collect ? ["mem", "gc"] : ["mem"]);
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("-X\nPOST", result.Arguments);
        Assert.Contains(collect ? "/mem?gc=1\n" : "/mem\n", result.Arguments);
        Assert.Contains(collect ? "--max-time\n60\n" : "--max-time\n5\n", result.Arguments);
    }

    [Fact]
    public async Task InvalidMemoryArgumentsNeverReachCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[] { new[] { "mem", "" }, ["mem", "GC"], ["mem", "gc", "extra"] })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Arguments);
        }
    }

    [Fact]
    public async Task InvalidDisplayMatchingArgumentsNeverReachCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[] { new[] { "display-match", "" }, ["display-match", "ON"],
            ["display-match", "true"], ["display-match", "on", "extra"], ["display-match", "status", "extra"] })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Arguments);
        }
    }

    [Theory]
    [InlineData("pretend")]
    [InlineData("off")]
    [InlineData("status")]
    public async Task HdrComparisonUsesValidatedModesAndAReadOnlyStatus(string mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["hdr", mode]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/hdr\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        if (mode == "status") Assert.DoesNotContain("-X\nPOST", result.Arguments);
        else
        {
            Assert.Contains("-X\nPOST\n", result.Arguments);
            Assert.Contains("--data-urlencode\nmode=" + mode, result.Arguments);
        }
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("123.456", true)]
    public async Task SeekPostsAbsoluteSecondsAndOptionalPause(string seconds, bool pause)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(pause ? ["seek", seconds, "pause"] : ["seek", seconds]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/seek\n", result.Arguments);
        Assert.Contains("-X\nPOST\n", result.Arguments);
        Assert.Contains("--max-time\n45\n", result.Arguments);
        Assert.Contains("--data-urlencode\nseconds=" + seconds, result.Arguments);
        Assert.Contains("--data-urlencode\npause=" + (pause ? "true" : "false"), result.Arguments);
    }

    [Fact]
    public async Task InvalidHdrAndSeekArgumentsNeverReachCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[] { new[] { "hdr" }, ["hdr", "on"], ["hdr", "Pretend"], ["hdr", "status", "extra"],
            ["seek"], ["seek", "-1"], ["seek", "NaN"], ["seek", "1", "play"], ["seek", "1", "pause", "extra"] })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Arguments);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatsRowsDefaultsToAReadOnlyList(bool explicitList)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(explicitList ? ["stats-rows", "list"] : ["stats-rows"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/stats-rows\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        Assert.Contains("--fail-with-body", result.Arguments);
        Assert.DoesNotContain("POST", result.Arguments);
        Assert.DoesNotContain("--data-urlencode", result.Arguments);
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("show")]
    [InlineData("only")]
    public async Task StatsRowsForwardsOneCommaSeparatedArgumentWithoutSplittingLabels(string action)
    {
        if (!OperatingSystem.IsLinux()) return;
        const string labels = "Read-ahead,Stream reads,Dropped frames,Future (decoder)";
        var result = await RunAsync(["stats-rows", action, labels]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/stats-rows\n", result.Arguments);
        Assert.Contains("-X\nPOST\n", result.Arguments);
        Assert.Contains("--data-urlencode\naction=" + action + "\n", result.Arguments);
        Assert.Contains("--data-urlencode\nlabels=" + labels + "\n", result.Arguments);
        Assert.DoesNotContain(labels, result.Output);
    }

    [Fact]
    public async Task StatsRowsResetPostsWithoutLabels()
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["stats-rows", "reset"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("-X\nPOST\n", result.Arguments);
        Assert.Contains("/stats-rows\n", result.Arguments);
        Assert.Contains("--data-urlencode\naction=reset\n", result.Arguments);
        Assert.DoesNotContain("labels=", result.Arguments);
    }

    [Fact]
    public async Task InvalidStatsArgumentsNeverReachCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[]
        {
            new[] { "stats", "extra" }, ["stats-rows", ""], ["stats-rows", "unknown"], ["stats-rows", "HIDE", "CPU"],
            ["stats-rows", "list", "CPU"], ["stats-rows", "reset", "CPU"], ["stats-rows", "hide"],
            ["stats-rows", "hide", "CPU", "Read-ahead"], ["stats-rows", "only", ""], ["stats-rows", "show", " "],
            ["stats-rows", "hide", ",CPU"], ["stats-rows", "hide", "CPU,"], ["stats-rows", "hide", "CPU, ,Read-ahead"],
            ["stats-rows", "hide", "CPU,,Read-ahead"], ["stats-rows", "hide", "CPU\nRead-ahead"],
            ["stats-rows", "hide", "CPU\r"], ["stats-rows", "hide", "CPU\t"],
        })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Arguments);
        }
    }

    [Fact]
    public async Task StatsPrintsTheCompleteReadoutIncludingHiddenMarkersWithoutOtherOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string rows = "CPU: 1.2 cores\nRead-ahead: on [hidden]\nDropped frames: unavailable [hidden]\n";
        var result = await RunAsync(["stats"], rows);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(rows, result.Output);
        Assert.Contains("/stats\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        Assert.Contains("--fail-with-body", result.Arguments);
        Assert.DoesNotContain("POST", result.Arguments);
    }

    [Theory]
    [InlineData("stats", 22)]
    [InlineData("stats-rows", 0)]
    public async Task NoPlayerMessageAndExitStatusArePreserved(string command, int exitCode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync([command], "no active player page\n", exitCode);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("no active player page\n", result.Output);
        Assert.Contains("--fail-with-body", result.Arguments);
    }

    private static async Task<(int ExitCode, string Output, string Arguments)> RunAsync(string[] arguments,
        string output = "probe result\n", int exitCode = 0)
    {
        var directory = Directory.CreateTempSubdirectory("bale-curl-stub-").FullName;
        try
        {
            var curl = Path.Combine(directory, "curl");
            await File.WriteAllTextAsync(curl, "#!/usr/bin/env bash\nprintf '%s\\n' \"$@\" >> \"$BALE_CURL_ARGS\"\nprintf '%s' \"$BALE_CURL_OUTPUT\"\nexit \"$BALE_CURL_EXIT_CODE\"\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(curl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var privateFile = Path.Combine(directory, "url");
            await File.WriteAllTextAsync(privateFile, "https://example.org/direct?st=not-a-real-grant");
            var captured = Path.Combine(directory, "args");
            var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "bale-debug.sh"));
            foreach (var argument in arguments) info.ArgumentList.Add(argument == "@fixture" ? "@" + privateFile : argument);
            info.Environment["PATH"] = directory + ":" + Environment.GetEnvironmentVariable("PATH");
            info.Environment["XBOX_HOST"] = "debug.invalid";
            info.Environment["BALE_CURL_ARGS"] = captured;
            info.Environment["BALE_CURL_OUTPUT"] = output;
            info.Environment["BALE_CURL_EXIT_CODE"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await stdout + await stderr, File.Exists(captured) ? await File.ReadAllTextAsync(captured) : string.Empty);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
