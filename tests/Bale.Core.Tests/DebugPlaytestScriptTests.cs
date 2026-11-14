using System.Diagnostics;

namespace Bale.Core.Tests;

public class DebugPlaytestScriptTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("hls")]
    [InlineData("auto")]
    public async Task DeliverySwitchUsesStatusOrAnEncodedPost(string? mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(mode is null ? ["delivery"] : ["delivery", mode]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/delivery\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        if (mode is null)
        {
            Assert.DoesNotContain("POST", result.Arguments);
            Assert.DoesNotContain("--data-urlencode", result.Arguments);
        }
        else
        {
            Assert.Contains("-X\nPOST\n", result.Arguments);
            Assert.Contains("--data-urlencode\nmode=" + mode + "\n", result.Arguments);
        }
    }

    [Fact]
    public async Task InvalidDeliveryModesAndExtraArgumentsNeverInvokeCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[] { new[] { "delivery", "" }, ["delivery", "HLS"], ["delivery", "hls "],
            ["delivery", "status"], ["delivery", "off"], ["delivery", "hls", "auto"] })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Arguments);
        }
    }

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
    [InlineData("threads=0", "ahead=0", "10bit=off", "prio=0", "hdr=on", "rate=0.5", "delay=0", "maxres=0", "dpb=0")]
    [InlineData("prio=2", "maxres=1", "delay=64", "rate=2.0", "hdr=off", "10bit=on", "dpb=16", "ahead=64", "threads=16")]
    [InlineData("hdr=default", "10bit=off", "ahead=0008", "dpb=003", "threads=4", "delay=001", "rate=0.75", "prio=001", "maxres=001")]
    [InlineData("maxres=default", "threads=4", "hdr=on", "10bit=on", "ahead=8", "dpb=default", "rate=1", "prio=1", "delay=8")]
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
        string[] options = ["threads=4", "10bit=on", "rate=1", "ahead=8", "delay=8", "prio=1", "maxres=default", "dpb=default", hdr];
        var result = await RunAsync(["playtest-ffmpeg", "@fixture", "30", "software", .. options]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--data-urlencode\n" + hdr + "\n", result.Arguments);
        Assert.DoesNotContain("not-a-real-grant", result.Output + result.Arguments);
    }

    [Theory]
    [InlineData("grain=on")]
    [InlineData("grain=off")]
    [InlineData("grain=default")]
    [InlineData("grain=dav1d")]
    [InlineData("grain=gpu")]
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
    [InlineData("grain=dav1d")]
    [InlineData("grain=gpu")]
    public async Task GrainTuneOverrideIsSentToTheRealPlayer(string grain)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["ffmpeg-tune", "prio=2", grain]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/ffmpeg-tune\n", result.Arguments);
        Assert.Contains("--data-urlencode\n" + grain + "\n", result.Arguments);
    }

    [Theory]
    [InlineData("sws=4")]
    [InlineData("sws=default")]
    [InlineData("upload=on")]
    [InlineData("upload=6")]
    [InlineData("upload=default")]
    [InlineData("uploadfault=5")]
    [InlineData("repack=on")]
    [InlineData("zerocopy=on")]
    [InlineData("zerocopy=off")]
    [InlineData("grain=cpu")]
    public async Task RemovedSoftwareFallbackKnobsNeverInvokeCurlOnEitherPath(string option)
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[]
        {
            new[] { "ffmpeg-tune", "prio=2", option },
            ["playtest-ffmpeg", "@fixture", "30", "software", "hdr=on", option],
        })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Arguments);
        }
    }

    [Fact]
    public async Task TuneUsageSaysSoftwareAv1IsFixedToZeroCopy()
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["ffmpeg-tune", "upload=6"]);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("software AV1 is fixed to zero-copy direct write (upload variant 6", result.Output);
        foreach (var text in new[] { "sws=", "upload=", "uploadfault=", "repack=", "zerocopy=", "cpu|" })
            Assert.DoesNotContain(text, result.Output);
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
    [InlineData("bgra")]
    [InlineData("fp16")]
    [InlineData("rgb10")]
    public async Task EngineTargetIsSentOnlyForTheRustProbeAndForEverySuiteSample(string target)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["playtest-engine", "@fixture", "10", "rust", "target=" + target]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/playtest-engine\n", result.Arguments);
        Assert.Contains("--data-urlencode\nmode=rust\n", result.Arguments);
        Assert.Contains("--data-urlencode\ntarget=" + target + "\n", result.Arguments);
        Assert.Contains("--max-time\n40\n", result.Arguments);
        Assert.DoesNotContain("not-a-real-grant", result.Output + result.Arguments);
        var suite = await RunAsync(["playtest-engine", "suite", "3", "rust", "target=" + target]);
        Assert.Equal(0, suite.ExitCode);
        Assert.Equal(11, suite.Arguments.Split('\n').Count(line => line == "target=" + target));
        var omitted = await RunAsync(["playtest-engine", "@fixture", "10", "rust"]);
        Assert.Equal(0, omitted.ExitCode);
        Assert.DoesNotContain("target=", omitted.Arguments);
    }

    [Theory]
    [InlineData("cs", "target=fp16")]
    [InlineData("rust-ffmpeg", "target=rgb10")]
    [InlineData("rust", "target=FP16")]
    [InlineData("rust", "target=p010")]
    [InlineData("rust", "target=")]
    [InlineData("rust", "fp16")]
    [InlineData("rust", "format=fp16")]
    [InlineData("rust", "target=fp16 target=rgb10")]
    [InlineData("target=fp16", null)]
    public async Task InvalidEngineTargetNeverInvokesCurl(string mode, string? option)
    {
        if (!OperatingSystem.IsLinux()) return;
        string[] args = option is null ? ["playtest-engine", "@fixture", "3", mode]
            : ["playtest-engine", "@fixture", "3", mode, .. option.Split(' ')];
        var result = await RunAsync(args);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Arguments);
        Assert.Contains("[target=bgra|fp16|rgb10]", result.Output);
    }

    [Theory]
    [InlineData("hdr=")]
    [InlineData("hdr=ON")]
    [InlineData("hdr=1")]
    [InlineData("hdr=pretend")]
    [InlineData("hdr=on hdr=off")]
    public async Task InvalidHdrOverrideNeverInvokesCurlForEitherPlayer(string option)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["playtest-ffmpeg", "@fixture", "30", "software", .. option.Split(' ')]);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Arguments);
        var tune = await RunAsync(["ffmpeg-tune", .. option.Split(' ')]);
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
        foreach (var key in new[] { "ahead", "sws", "delay", "prio", "maxres", "dpb", "upload" }) Assert.DoesNotContain("\n" + key + "=", result.Arguments);
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
    [InlineData("ahead=0", "buffer=0", "delay=0")]
    [InlineData("delay=64", "ahead=64", "buffer=5000")]
    [InlineData("buffer=0500", "delay=001", "ahead=0008")]
    [InlineData("delay=default", "buffer=default", "ahead=default")]
    [InlineData("prio=0", "buffer=1000", "ahead=16")]
    [InlineData("ahead=8", "prio=2", "delay=1")]
    [InlineData("prio=001", "delay=001", "ahead=0008")]
    [InlineData("delay=default", "prio=default", "ahead=default")]
    [InlineData("maxres=0", "dpb=0", "prio=0")]
    [InlineData("dpb=16", "maxres=1", "prio=2")]
    [InlineData("maxres=001", "prio=001", "dpb=003")]
    [InlineData("dpb=default", "maxres=default", "prio=default")]
    [InlineData("hdr=on", "maxres=1", "dpb=3")]
    [InlineData("ahead=8", "hdr=off", "prio=2")]
    [InlineData("hdr=default", "readahead=default", "buffer=default")]
    [InlineData("decoder=auto", "hdr=on", "ahead=default")]
    [InlineData("hdr=default", "decoder=system", "buffer=default")]
    [InlineData("prio=2", "buffer=1000", "decoder=software")]
    [InlineData("decoder=default", "readahead=default", "buffer=default")]
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
        foreach (var key in new[] { "ahead", "sws", "delay", "prio", "maxres", "dpb", "hdr", "upload", "decoder" }) Assert.DoesNotContain("\n" + key + "=", result.Arguments);
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
    [InlineData("decoder=AUTO")]
    [InlineData("decoder=Automatic")]
    [InlineData("decoder=d3d11")]
    [InlineData("decoder=1")]
    [InlineData("decoder=")]
    [InlineData("decoder= auto")]
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
    [InlineData("decoder=auto", "decoder=system")]
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
    public async Task GcReadoutIsAGetThatPreservesTheDiagnosticOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string readout = "GC: server=False latency=Interactive playback-lease=0 pause-total=350.0 ms collections gen0=3 gen1=2 gen2=1 allocated=128.5 MiB\n";
        var result = await RunAsync(["gc"], readout);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(readout, result.Output);
        Assert.Contains("/gc\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        Assert.Contains("--fail-with-body", result.Arguments);
        Assert.DoesNotContain("POST", result.Arguments);
        Assert.DoesNotContain("latency=", result.Arguments);
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("interactive")]
    [InlineData("sustained")]
    [InlineData("default")]
    public async Task GcLatencyPostsOnlyAValidatedMode(string mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["gc", "latency", mode]);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("probe result\n", result.Output);
        Assert.Contains("/gc?latency=" + mode + "\n", result.Arguments);
        Assert.Contains("-X\nPOST\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        Assert.Contains("--fail-with-body", result.Arguments);
        Assert.DoesNotContain("--data-urlencode", result.Arguments);
    }

    [Fact]
    public async Task InvalidGcArgumentsShowUsageWithoutInvokingCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[]
        {
            new[] { "gc", "" }, ["gc", "status"], ["gc", "batch"], ["gc", "latency"], ["gc", "LATENCY", "sustained"],
            ["gc", "latency", ""], ["gc", "latency", "BATCH"], ["gc", "latency", " interactive"],
            ["gc", "latency", "sustained "], ["gc", "latency", "unknown"], ["gc", "latency", "low"],
            ["gc", "latency", "LowLatency"], ["gc", "latency", "NoGCRegion"], ["gc", "latency", "sustainedlowlatency"],
            ["gc", "latency", "sustained&extra=1"], ["gc", "latency", "batch", "extra"],
            ["gc", "latency", "interactive", "extra"], ["gc", "latency", "sustained", "extra"],
            ["gc", "latency", "default", ""],
        })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("usage: scripts/bale-debug.sh gc [latency <batch|interactive|sustained|default>]\n", result.Output);
            Assert.Empty(result.Arguments);
        }
    }

    [Fact]
    public async Task GcLatencyFailurePreservesTheResponseBodyAndExitStatus()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string failure = "latency change failed hr=0x80131509\n";
        var result = await RunAsync(["gc", "latency", "sustained"], failure, 22);
        Assert.Equal(22, result.ExitCode);
        Assert.Equal(failure, result.Output);
        Assert.Contains("--fail-with-body", result.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("native")]
    [InlineData("managed")]
    [InlineData("on")]
    [InlineData("off")]
    public async Task FrameDeliveryReadsStatusOrPostsOnlyAValidatedMode(string? mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var readout = "frame-delivery=" + (mode is null or "on" ? "native" : mode) + "\n";
        var result = await RunAsync(mode is null ? ["frame-delivery"] : ["frame-delivery", mode], readout);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(readout, result.Output);
        Assert.Contains("/frame-delivery\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        Assert.Contains("--fail-with-body", result.Arguments);
        if (mode is null)
        {
            Assert.DoesNotContain("POST", result.Arguments);
            Assert.DoesNotContain("--data-urlencode", result.Arguments);
        }
        else
        {
            Assert.Contains("-X\nPOST\n", result.Arguments);
            Assert.Contains("--data-urlencode\nmode=" + mode + "\n", result.Arguments);
        }
    }

    [Fact]
    public async Task InvalidFrameDeliveryArgumentsShowUsageWithoutInvokingCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[]
        {
            new[] { "frame-delivery", "" }, ["frame-delivery", "status"], ["frame-delivery", "ON"],
            ["frame-delivery", "true"], ["frame-delivery", "NATIVE"], ["frame-delivery", "Managed"],
            ["frame-delivery", " on"], ["frame-delivery", "native "], ["frame-delivery", "off "],
            ["frame-delivery", "on&extra=1"], ["frame-delivery", "on", "off"], ["frame-delivery", "off", ""],
        })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("usage: scripts/bale-debug.sh frame-delivery [native|managed|off]\n", result.Output);
            Assert.Empty(result.Arguments);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    public async Task FrameDeliveryFailurePreservesTheResponseBodyAndExitStatus(string? mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        const string failure = "mode must be native, managed or off (on aliases native)\n";
        var result = await RunAsync(mode is null ? ["frame-delivery"] : ["frame-delivery", mode], failure, 22);
        Assert.Equal(22, result.ExitCode);
        Assert.Equal(failure, result.Output);
        Assert.Contains("--fail-with-body", result.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pooled")]
    [InlineData("builtin")]
    public async Task StreamAdapterReadsStatusOrPostsOnlyAValidatedMode(string? mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var readout = "stream-adapter=" + (mode ?? "pooled") + "\n";
        var result = await RunAsync(mode is null ? ["stream-adapter"] : ["stream-adapter", mode], readout);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(readout, result.Output);
        Assert.Contains("/stream-adapter\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        Assert.Contains("--fail-with-body", result.Arguments);
        if (mode is null)
        {
            Assert.DoesNotContain("POST", result.Arguments);
            Assert.DoesNotContain("--data-urlencode", result.Arguments);
        }
        else
        {
            Assert.Contains("-X\nPOST\n", result.Arguments);
            Assert.Contains("--data-urlencode\nmode=" + mode + "\n", result.Arguments);
        }
    }

    [Fact]
    public async Task InvalidStreamAdapterArgumentsShowUsageWithoutInvokingCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[]
        {
            new[] { "stream-adapter", "" }, ["stream-adapter", "status"], ["stream-adapter", "POOLED"],
            ["stream-adapter", "on"], ["stream-adapter", " pooled"], ["stream-adapter", "builtin "],
            ["stream-adapter", "pooled&extra=1"], ["stream-adapter", "pooled", "builtin"], ["stream-adapter", "builtin", ""],
        })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("usage: scripts/bale-debug.sh stream-adapter [pooled|builtin]\n", result.Output);
            Assert.Empty(result.Arguments);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("builtin")]
    public async Task StreamAdapterFailurePreservesTheResponseBodyAndExitStatus(string? mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        const string failure = "mode must be pooled or builtin\n";
        var result = await RunAsync(mode is null ? ["stream-adapter"] : ["stream-adapter", mode], failure, 22);
        Assert.Equal(22, result.ExitCode);
        Assert.Equal(failure, result.Output);
        Assert.Contains("--fail-with-body", result.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1080")]
    [InlineData("2160")]
    public async Task Av1ClaimReadsStatusOrPostsOnlyAValidatedBound(string? height)
    {
        if (!OperatingSystem.IsLinux()) return;
        var readout = "av1-claim=" + (height ?? "1080") + "\n";
        var result = await RunAsync(height is null ? ["av1-claim"] : ["av1-claim", height], readout);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(readout, result.Output);
        Assert.Contains("/av1-claim\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        Assert.Contains("--fail-with-body", result.Arguments);
        if (height is null)
        {
            Assert.DoesNotContain("POST", result.Arguments);
            Assert.DoesNotContain("--data-urlencode", result.Arguments);
        }
        else
        {
            Assert.Contains("-X\nPOST\n", result.Arguments);
            Assert.Contains("--data-urlencode\nheight=" + height + "\n", result.Arguments);
        }
    }

    [Fact]
    public async Task InvalidAv1ClaimArgumentsShowUsageWithoutInvokingCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[]
        {
            new[] { "av1-claim", "" }, ["av1-claim", "status"], ["av1-claim", "720"], ["av1-claim", "4320"],
            ["av1-claim", "2160p"], ["av1-claim", "02160"], ["av1-claim", "2160.0"], ["av1-claim", " 2160"],
            ["av1-claim", "1080 "], ["av1-claim", "2160&extra=1"], ["av1-claim", "1080", "2160"], ["av1-claim", "2160", ""],
        })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("usage: scripts/bale-debug.sh av1-claim [1080|2160]\n", result.Output);
            Assert.Empty(result.Arguments);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2160")]
    public async Task Av1ClaimFailurePreservesTheResponseBodyAndExitStatus(string? height)
    {
        if (!OperatingSystem.IsLinux()) return;
        const string failure = "height must be 1080 or 2160\n";
        var result = await RunAsync(height is null ? ["av1-claim"] : ["av1-claim", height], failure, 22);
        Assert.Equal(22, result.ExitCode);
        Assert.Equal(failure, result.Output);
        Assert.Contains("--fail-with-body", result.Arguments);
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
    [InlineData("play", "Playing")]
    [InlineData("pause", "Paused")]
    [InlineData("toggle", "Playing")]
    public async Task PlaybackControlPostsStateAndPrintsThePosition(string command, string state)
    {
        if (!OperatingSystem.IsLinux()) return;
        var readout = $"Position: source=123.5s player=3.5s state={state}\n";
        var result = await RunAsync([command], readout);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(readout, result.Output);
        Assert.Contains("/playback\n", result.Arguments);
        Assert.Contains("-X\nPOST\n", result.Arguments);
        Assert.Contains("--max-time\n15\n", result.Arguments);
        Assert.Contains("--data-urlencode\nstate=" + command + "\n", result.Arguments);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("pause")]
    [InlineData("toggle")]
    public async Task PlaybackControlRejectsExtraArgumentsAndPreservesHttpFailures(string command)
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var extra in new[] { "", "extra" })
        {
            var invalid = await RunAsync([command, extra]);
            Assert.Equal(2, invalid.ExitCode);
            Assert.Equal("usage: scripts/bale-debug.sh {play|pause|toggle}\n", invalid.Output);
            Assert.Empty(invalid.Arguments);
        }
        const string failure = "player is still loading or buffering from open\n";
        var result = await RunAsync([command], failure, 22);
        Assert.Equal(22, result.ExitCode);
        Assert.Equal(failure, result.Output);
        Assert.Contains("--fail-with-body", result.Arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrameTraceReadsOrResetsAndPreservesTheTraceOutput(bool reset)
    {
        if (!OperatingSystem.IsLinux()) return;
        const string trace = "samples=2 late=1 stream=0 gc=0 gap=0 other=1 max-gap=10.0 long-gaps=0\n"
            + "t=10.0 pts=100.0 late=1.0 ahead=-61.0 gap=10.0 dw=- gc=0.0 g0=0 g1=0 g2=0 class=other\n";
        var result = await RunAsync(reset ? ["frame-trace", "reset"] : ["frame-trace"], trace);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(trace, result.Output);
        Assert.Contains("/frame-trace\n", result.Arguments);
        Assert.Contains("--max-time\n5\n", result.Arguments);
        Assert.Contains("--fail-with-body", result.Arguments);
        if (reset)
        {
            Assert.Contains("-X\nPOST\n", result.Arguments);
            Assert.Contains("--data-urlencode\naction=reset\n", result.Arguments);
        }
        else
        {
            Assert.DoesNotContain("POST", result.Arguments);
            Assert.DoesNotContain("--data-urlencode", result.Arguments);
        }
    }

    [Fact]
    public async Task InvalidFrameTraceArgumentsShowUsageWithoutInvokingCurl()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[] { new[] { "frame-trace", "" }, ["frame-trace", "list"], ["frame-trace", "RESET"],
            ["frame-trace", "unknown"], ["frame-trace", "reset", "extra"], ["frame-trace", "reset", ""] })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("usage: scripts/bale-debug.sh frame-trace [reset]\n", result.Output);
            Assert.Empty(result.Arguments);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrameTracePreservesUnavailableObserverOutput(bool reset)
    {
        if (!OperatingSystem.IsLinux()) return;
        const string unavailable = "frame trace unavailable (no software video observer)\n";
        var result = await RunAsync(reset ? ["frame-trace", "reset"] : ["frame-trace"], unavailable);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(unavailable, result.Output);
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
    [InlineData("frame-trace", 22)]
    public async Task NoPlayerMessageAndExitStatusArePreserved(string command, int exitCode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync([command], "no active player page\n", exitCode);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("no active player page\n", result.Output);
        Assert.Contains("--fail-with-body", result.Arguments);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("bt2390")]
    [InlineData("hable")]
    [InlineData("default")]
    public async Task ToneMapTuningIsEncodedForTheRealPlayerOnly(string value)
    {
        if (!OperatingSystem.IsLinux()) return;
        var result = await RunAsync(["ffmpeg-tune", "tonemap=" + value, "decoder=auto"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/ffmpeg-tune\n", result.Arguments);
        Assert.Contains("--data-urlencode\ntonemap=" + value + "\n", result.Arguments);
        Assert.Contains("--data-urlencode\ndecoder=auto\n", result.Arguments);
        var probe = await RunAsync(["playtest-ffmpeg", "https://example.org/clip", "3", "auto", "tonemap=" + value]);
        Assert.Equal(2, probe.ExitCode);
        Assert.Empty(probe.Arguments);
    }

    [Theory]
    [InlineData("BT2390")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("")]
    public async Task InvalidOrDuplicateToneMapTuningNeverInvokesCurl(string value)
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var args in new[] {
            new[] { "ffmpeg-tune", "decoder=auto", "tonemap=" + value },
            new[] { "ffmpeg-tune", "tonemap=off", "tonemap=default" },
        })
        {
            var result = await RunAsync(args);
            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.Arguments);
            Assert.Contains("requires decoder=auto", result.Output);
            Assert.Contains("2.1.0-bale.25", result.Output);
        }
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
