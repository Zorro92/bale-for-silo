using System.Text.Json;
using System.Xml.Linq;
using Bale.Xbox.Services;
using FFmpegInteropX;

namespace Bale.Core.Tests;

public sealed class DebugFfmpegToneMapTests
{
    [Theory]
    [InlineData("off", 0, "0")]
    [InlineData("bt2390", 1, "1")]
    [InlineData("hable", 2, "2")]
    public void SupportedWrapperMapsCapturedTuningToTheExpectedOption(string value, int expected, string code)
    {
        var current = new DebugFfmpegInputTuning(ReadAhead: true);
        // Reverse key order: validation must use the complete immutable tuning.
        var options = new Dictionary<string, string> { ["tonemap"] = value, ["decoder"] = "auto" };
        Assert.True(DebugFfmpegInputTuning.TryUpdate(current, options, "2.1.0-bale.25", out var tuning, out var error));
        Assert.Null(error);
        Assert.Equal((DebugToneMap)expected, tuning.ToneMap);
        Assert.Equal(value, tuning.ToneMapOverride);
        Assert.Null(current.ToneMap);
        Assert.True(tuning.ReadAhead);
        Assert.Contains("tonemap=" + value, tuning.Description);
        var config = new MediaSourceConfig();
        DebugFfmpegToneMap.Apply(config, tuning, "2.1.0-bale.25");
        Assert.Equal(code, config.FFmpegOptions["bale_tonemap"]);
        Assert.True(DebugFfmpegInputTuning.TryUpdate(tuning, new Dictionary<string, string> { ["prio"] = "1" },
            "2.1.0-bale.26", out var next, out _));
        Assert.Equal((DebugToneMap)expected, next.ToneMap);
        Assert.True(DebugFfmpegInputTuning.TryUpdate(next, new Dictionary<string, string> { ["tonemap"] = "default" },
            "2.1.0-bale.25", out var cleared, out _));
        Assert.Null(cleared.ToneMap);
        Assert.DoesNotContain("tonemap=", cleared.Description);
        DebugFfmpegToneMap.Apply(config, cleared, "2.1.0-bale.25");
        Assert.Equal(code, config.FFmpegOptions["bale_tonemap"]); // Unset tuning preserves the route's existing choice.
    }

    [Theory]
    [InlineData("system")]
    [InlineData("software")]
    [InlineData("default")]
    public void SettingOrKeepingAToneMapRequiresAutoAndRejectsTheEntireUpdate(string decoder)
    {
        var current = new DebugFfmpegInputTuning(ReadAhead: true, Decoder: DebugFfmpegDecoder.Auto, ToneMap: DebugToneMap.Bt2390);
        Assert.False(DebugFfmpegInputTuning.TryUpdate(current,
            new Dictionary<string, string> { ["decoder"] = decoder, ["buffer"] = "1500" },
            "2.1.0-bale.25", out var next, out var error));
        Assert.Same(current, next);
        Assert.Contains("requires decoder=auto in the same tuning", error);
        Assert.False(DebugFfmpegInputTuning.TryUpdate(new(), new Dictionary<string, string> { ["tonemap"] = "off" },
            "2.1.0-bale.25", out _, out error));
        Assert.Contains("requires decoder=auto", error);
        Assert.True(DebugFfmpegInputTuning.TryUpdate(current,
            new Dictionary<string, string> { ["tonemap"] = "default", ["decoder"] = decoder },
            "2.1.0-bale.25", out var cleared, out _));
        Assert.Null(cleared.ToneMap);
    }

    [Theory]
    [InlineData("2.1.0-bale.23", false)]
    [InlineData("2.1.0-bale.24", false)]
    [InlineData("2.1.0-bale.25", true)]
    [InlineData("2.1.0-bale.26", true)]
    [InlineData("2.1.0-bale.100", true)]
    [InlineData("2.1.0-bale.25-beta", false)]
    [InlineData("2.1.0", false)]
    [InlineData("", false)]
    public void OnlyReleasedForkRevisionsAtOrAfter25AreEnabled(string version, bool supported)
        => Assert.Equal(supported, DebugFfmpegToneMap.Supported(version));

    [Theory]
    [InlineData("off")]
    [InlineData("bt2390")]
    [InlineData("hable")]
    public void OlderWrappersRejectEvenOffWithoutPassingAnUnknownKeyOrChangingTuning(string value)
    {
        var current = new DebugFfmpegInputTuning(ReadAhead: true);
        var update = new Dictionary<string, string> { ["decoder"] = "auto", ["tonemap"] = value, ["buffer"] = "1500" };
        Assert.False(DebugFfmpegInputTuning.TryUpdate(current, update, "2.1.0-bale.24", out var next, out var error));
        Assert.Same(current, next);
        Assert.Contains("2.1.0-bale.25", error);
        var config = new MediaSourceConfig();
        Assert.Throws<InvalidDataException>(() => DebugFfmpegToneMap.Apply(config,
            new(Decoder: DebugFfmpegDecoder.Auto, ToneMap: DebugToneMap.Off), "2.1.0-bale.24"));
        Assert.Empty(config.FFmpegOptions);
        Assert.True(DebugFfmpegInputTuning.TryUpdate(current,
            new Dictionary<string, string> { ["tonemap"] = "default" }, "2.1.0-bale.24", out _, out _));
    }

    [Theory]
    [InlineData("BT2390")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("")]
    [InlineData("hable ")]
    public void InvalidToneMapValuesDoNotChangeTheTuning(string value)
    {
        var current = new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.Auto);
        Assert.False(DebugFfmpegInputTuning.TryUpdate(current, new Dictionary<string, string> { ["tonemap"] = value },
            "2.1.0-bale.25", out var next, out _));
        Assert.Same(current, next);
    }

    [Fact]
    public void BuiltGateUsesTheActualPackagePinAndDefaultNonToneMapRoutesLeaveItUnset()
    {
        var package = XDocument.Parse(Read("Bale.Xbox.csproj.txt")).Descendants("PackageReference")
            .Single(p => p.Attribute("Include")!.Value == "FFmpegInteropX.UWP.Lib").Attribute("Version")!.Value.Trim('[', ']');
        using var pin = JsonDocument.Parse(Read("ffmpeginteropx-wrapper.json"));
        Assert.Equal(FfmpegWrapperVersion.Value, pin.RootElement.GetProperty("version").GetString());
        Assert.Equal(FfmpegWrapperVersion.Value, package);
        var tuning = new DebugFfmpegInputTuning(Decoder: DebugFfmpegDecoder.Auto, ToneMap: DebugToneMap.Bt2390);
        if (DebugFfmpegToneMap.Supported(FfmpegWrapperVersion.Value))
            Assert.Equal("1", FfmpegSource.Configuration(new(true, false, "hevc", "aac", 0, 1920, 1080), tuning).FFmpegOptions["bale_tonemap"]);
        else
            Assert.Throws<InvalidDataException>(() => FfmpegSource.Configuration(new(true, false, "hevc", "aac", 0, 1920, 1080), tuning));
        Assert.DoesNotContain("bale_tonemap", FfmpegSource.Configuration(new(true, false, "hevc", "aac", 0, 1920, 1080)).FFmpegOptions.Keys);
        Assert.Contains("if (route.ConsoleToneMap)", DebugFfmpegDecoderTests.ProductionSource());
        var source = Read("FfmpegSource.cs.txt");
        Assert.Contains("DebugFfmpegToneMap.Apply(config, tuning, FfmpegWrapperVersion.Value)", source);
        Assert.Contains("FFmpeg tone map override", Read("PlayerPage.Stats.cs.txt"));
        Assert.Contains("media.FfmpegToneMapOverride = attempt.FfmpegTuning.ToneMapOverride;", Read("PlayerPage.Media.cs.txt"));
        Assert.Contains("tonemap={media.FfmpegToneMapOverride}", Read("PlayerPage.Media.cs.txt"));
        Assert.Contains("tuningError + \"\\n\"", Read("DebugServer.cs.txt"));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
