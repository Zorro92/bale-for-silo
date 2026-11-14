using System.Text.Json;
using System.Xml.Linq;
using Bale.Xbox.Services;
using FFmpegInteropX;

namespace Bale.Core.Tests;

public sealed class DebugFfmpegColorInfoTests
{
    [Theory]
    [InlineData("on", true, "1")]
    [InlineData("off", false, "0")]
    public void CapturedOverrideMapsToTheWrapperKeyAndClearsIndependently(string value, bool enabled, string code)
    {
        var current = new DebugFfmpegInputTuning(ReadAhead: true);
        Assert.True(DebugFfmpegInputTuning.TryUpdate(current, new Dictionary<string, string> { ["colorinfo"] = value },
            "2.1.0-bale.26", out var tuning, out var error));
        Assert.Null(error);
        Assert.Equal(enabled, tuning.ColorInfo);
        Assert.Equal(value, tuning.ColorInfoOverride);
        Assert.Contains("colorinfo=" + value, tuning.Description);
        Assert.Null(tuning.Decoder);
        Assert.Null(current.ColorInfo);
        var config = new MediaSourceConfig();
        DebugFfmpegColorInfo.Apply(config, tuning, "2.1.0-bale.26");
        Assert.Equal(code, config.FFmpegOptions["bale_compressed_color_info"]);
        Assert.True(DebugFfmpegInputTuning.TryUpdate(tuning, new Dictionary<string, string> { ["hdr"] = "off" },
            "2.1.0-bale.27", out var next, out _));
        Assert.Equal(enabled, next.ColorInfo);
        Assert.True(DebugFfmpegInputTuning.TryUpdate(next, new Dictionary<string, string> { ["colorinfo"] = "default" },
            "2.1.0-bale.25", out var cleared, out _));
        Assert.Null(cleared.ColorInfo);
        Assert.Equal("default", cleared.ColorInfoOverride);
        Assert.True(cleared.ReadAhead);
        Assert.False(cleared.HdrSupport);
        Assert.DoesNotContain("colorinfo=", cleared.Description);
        DebugFfmpegColorInfo.Apply(config, cleared, "2.1.0-bale.25");
        Assert.DoesNotContain("bale_compressed_color_info", config.FFmpegOptions.Keys);
        Assert.Equal(enabled, tuning.ColorInfo);
    }

    [Theory]
    [InlineData("2.1.0-bale.25", false)]
    [InlineData("2.1.0-bale.26", true)]
    [InlineData("2.1.0-bale.27", true)]
    [InlineData("2.1.0-bale.100", true)]
    [InlineData("2.1.0-bale.26-beta", false)]
    [InlineData("2.1.0-bale.-26", false)]
    [InlineData("2.1.0", false)]
    [InlineData("", false)]
    public void OnlyReleasedForkRevisionsAtOrAfter26AreEnabled(string version, bool supported)
        => Assert.Equal(supported, DebugFfmpegColorInfo.Supported(version));

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    public void OlderWrappersRejectEvenOffWithoutChangingAnyTuningOrPassingTheKey(string value)
    {
        var current = new DebugFfmpegInputTuning(ReadAhead: true);
        Assert.False(DebugFfmpegInputTuning.TryUpdate(current,
            new Dictionary<string, string> { ["colorinfo"] = value, ["buffer"] = "1500" },
            "2.1.0-bale.25", out var next, out var error));
        Assert.Same(current, next);
        Assert.Contains(DebugFfmpegColorInfo.MinimumWrapperVersion, error);
        Assert.Contains("No tuning changed", error);
        var config = new MediaSourceConfig();
        Assert.Throws<InvalidDataException>(() => DebugFfmpegColorInfo.Apply(config,
            new(ColorInfo: value == "on"), "2.1.0-bale.25"));
        Assert.Empty(config.FFmpegOptions);
    }

    [Theory]
    [InlineData("ON")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData("off ")]
    public void InvalidValuesRejectTheWholeUpdate(string value)
    {
        var current = new DebugFfmpegInputTuning(ColorInfo: true);
        Assert.False(DebugFfmpegInputTuning.TryUpdate(current,
            new Dictionary<string, string> { ["hdr"] = "on", ["colorinfo"] = value },
            "2.1.0-bale.26", out var next, out var error));
        Assert.Same(current, next);
        Assert.Contains("colorinfo=on|off|default", error);
    }

    [Theory]
    [InlineData(null, "ForceSystemDecoder")]
    [InlineData("system", "ForceSystemDecoder")]
    [InlineData("auto", "Automatic")]
    [InlineData("software", "ForceFFmpegSoftwareDecoder")]
    public void ColorInfoDoesNotRequireAutoOrChangeTheDecoderOrHdrPolicy(string? decoder, string mode)
    {
        var options = new Dictionary<string, string> { ["colorinfo"] = "on", ["hdr"] = "off" };
        if (decoder is not null) options["decoder"] = decoder;
        Assert.True(DebugFfmpegInputTuning.TryUpdate(new(), options, FfmpegWrapperVersion.Value, out var tuning, out _));
        var config = FfmpegSource.Configuration(new(true, false, "vp9", "aac", 0, 1920, 1080), tuning);
        Assert.Equal("1", config.FFmpegOptions["bale_compressed_color_info"]);
        Assert.Equal(mode, config.Video.VideoDecoderMode.ToString());
        Assert.Equal(HdrSupport.Disabled, config.Video.HdrSupport);
        Assert.True(DebugFfmpegInputTuning.TryUpdate(tuning, new Dictionary<string, string> { ["decoder"] = "system" },
            FfmpegWrapperVersion.Value, out var next, out _));
        Assert.True(next.ColorInfo);
    }

    [Fact]
    public void BuiltGateUsesThePackagePinAndProductionCannotSetColorInfo()
    {
        var package = XDocument.Parse(Read("Bale.Xbox.csproj.txt")).Descendants("PackageReference")
            .Single(p => p.Attribute("Include")!.Value == "FFmpegInteropX.UWP.Lib").Attribute("Version")!.Value.Trim('[', ']');
        using var pin = JsonDocument.Parse(Read("ffmpeginteropx-wrapper.json"));
        Assert.Equal(FfmpegWrapperVersion.Value, pin.RootElement.GetProperty("version").GetString());
        Assert.Equal(FfmpegWrapperVersion.Value, package);
        Assert.True(DebugFfmpegColorInfo.Supported(FfmpegWrapperVersion.Value));
        Assert.DoesNotContain("bale_compressed_color_info", FfmpegSource.Configuration(new(true, false, "vp9", "aac", 0, 1920, 1080)).FFmpegOptions.Keys);
        var production = DebugFfmpegDecoderTests.ProductionSource();
        Assert.DoesNotContain("ColorInfo", production);
        Assert.DoesNotContain("bale_compressed_color_info", production);
        var helper = Read("DebugFfmpegColorInfo.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER\n", helper);
        Assert.EndsWith("#endif\n", helper);
        Assert.Contains("DebugFfmpegColorInfo.Apply(config, tuning, FfmpegWrapperVersion.Value)", Read("FfmpegSource.cs.txt"));
        Assert.Contains("(\"FFmpeg colour info override\", media?.FfmpegColorInfoOverride ?? \"unavailable\")", Read("PlayerPage.Stats.cs.txt"));
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("media.FfmpegColorInfoOverride = attempt.FfmpegTuning.ColorInfoOverride;", media);
        Assert.Contains("colorinfo={media.FfmpegColorInfoOverride}", media);
        Assert.DoesNotContain("DebugFfmpegInputTuning.Current", media);
        Assert.Contains("tuningError + \"\\n\"", Read("DebugServer.cs.txt"));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
