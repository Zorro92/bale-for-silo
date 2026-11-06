using System.Diagnostics;

namespace Bale.Core.Tests;

public class DebugPlaytestScriptTests
{
    [Theory]
    [InlineData("playtest", "3", null, 12)]
    [InlineData("playtest", "10", null, 19)]
    [InlineData("playtest-ffmpeg", "60", "software", 110)]
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
        var rateOnly = await RunAsync(["playtest-ffmpeg", "@fixture", "30", "software", "rate=0.75"]);
        Assert.Equal(0, rateOnly.ExitCode);
        Assert.Contains("rate=0.75", rateOnly.Arguments);
        Assert.DoesNotContain("threads=", rateOnly.Arguments);
        Assert.DoesNotContain("10bit=", rateOnly.Arguments);
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

    private static async Task<(int ExitCode, string Output, string Arguments)> RunAsync(string[] arguments)
    {
        var directory = Directory.CreateTempSubdirectory("bale-curl-stub-").FullName;
        try
        {
            var curl = Path.Combine(directory, "curl");
            await File.WriteAllTextAsync(curl, "#!/usr/bin/env bash\nprintf '%s\\n' \"$@\" >> \"$BALE_CURL_ARGS\"\nprintf 'probe result\\n'\n");
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
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await stdout + await stderr, File.Exists(captured) ? await File.ReadAllTextAsync(captured) : string.Empty);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
