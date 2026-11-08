using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugFfmpegLogFilterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bale")]
    [InlineData("BaleD3D11 upload")]
    [InlineData("bale D3D11 upload")]
    [InlineData("BALE D3D11 upload")]
    [InlineData(" Bale D3D11 upload")]
    [InlineData("\nBale D3D11 upload")]
    [InlineData("[ffmpeg] Bale D3D11 upload")]
    [InlineData("Opening private media input")]
    [InlineData("HTTP request headers")]
    public void DropsEveryMessageWithoutTheExactPrefix(string? message)
    {
        Assert.Null(DebugFfmpegLogFilter.Message(message));
    }

    [Theory]
    [InlineData("Bale D3D11 upload active.", "ffmpeg: Bale D3D11 upload active.")]
    [InlineData("Bale D3D11 upload Map failed (hr=0x80070057).\n", "ffmpeg: Bale D3D11 upload Map failed (hr=0x80070057).")]
    [InlineData("Bale D3D11 upload Processed index=1.\r\n\r\n", "ffmpeg: Bale D3D11 upload Processed index=1.")]
    [InlineData("Bale diagnostic \n", "ffmpeg: Bale diagnostic ")]
    public void ForwardsBaleDiagnosticsWithATagAndOnlyTrimsTrailingNewlines(string message, string expected)
    {
        Assert.Equal(expected, DebugFfmpegLogFilter.Message(message));
    }

    [Fact]
    public void BothSourcePathsShareAnExceptionSafeAotProviderThatIsAbsentFromProduction()
    {
        foreach (var file in new[] { "FfmpegSource.cs.txt", "DebugFfmpegSource.cs.txt" })
        {
            var source = Read(file);
            Assert.Contains("FfmpegLogging.Configure();", source);
            Assert.DoesNotContain("SetLogProvider", source);
        }
        var logging = Read("FfmpegLogging.cs.txt");
        var debugStart = logging.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal);
        var productionStart = logging.IndexOf("#else", debugStart, StringComparison.Ordinal);
        var end = logging.IndexOf("#endif", productionStart, StringComparison.Ordinal);
        Assert.Contains("FFmpegInteropLogging.SetLogLevel(LogLevel.Info);", logging[debugStart..productionStart]);
        Assert.Contains("FFmpegInteropLogging.SetLogProvider(new DebugFfmpegLogProvider());", logging[debugStart..productionStart]);
        Assert.Contains("FFmpegInteropLogging.SetLogProvider(null);", logging[productionStart..end]);
        Assert.DoesNotContain("DebugFfmpegLogProvider", logging[productionStart..end]);
        Assert.Contains("#if DEBUG_SERVER\n// Partial", logging);
        Assert.Contains("internal sealed partial class DebugFfmpegLogProvider : ILogProvider", logging);
        Assert.Contains("public void Log(LogLevel level, string message)", logging);
        Assert.Contains("try\n        {\n            if (DebugFfmpegLogFilter.Message(message) is { } safe) Bale.Xbox.Services.Log.Info(safe);\n        }\n        catch", logging);
        Assert.DoesNotContain("Log.Error", logging);
        Assert.DoesNotContain("Dispatcher", logging);
        Assert.DoesNotContain("ex.Message", logging);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
