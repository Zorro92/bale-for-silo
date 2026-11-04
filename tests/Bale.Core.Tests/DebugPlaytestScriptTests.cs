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
    [InlineData("auto")]
    [InlineData("software")]
    [InlineData("system")]
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
        foreach (var args in new[] { new[] { "ffmpeg" }, ["ffmpeg", "hardware"], ["ffmpeg", "Software"], ["ffmpeg", "auto", "extra"] })
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
