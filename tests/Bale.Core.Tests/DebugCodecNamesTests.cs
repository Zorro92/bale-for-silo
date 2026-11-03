using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugCodecNamesTests
{
    private static DebugCodecNames Names() => new(
    [
        ("34363248-0000-0010-8000-00aa00389b71", "H264"),
        ("31435641-0000-0010-8000-00aa00389b71", "H264"),
        ("00000001-0000-0010-8000-00aa00389b71", "PCM"),
        ("00000003-0000-0010-8000-00aa00389b71", "PCM"),
        ("eb27cec4-163e-4ca3-8b74-8e25f91b517e", "TrueHD/MLP"),
        ("20504c4d-0000-0010-8000-00aa00389b71", "TrueHD/MLP"),
    ]);

    [Fact]
    public void KnownGuidsAreMatchedAcrossCaseAndBracesWithoutLosingTheRawValue()
    {
        const string raw = "{EB27CEC4-163E-4CA3-8B74-8E25F91B517E}";
        Assert.Equal($"TrueHD/MLP [{raw}]", Names().Describe(raw));
    }

    [Theory]
    [InlineData("dc3ad569-9b62-4fdb-9437-6e6e798ebfa6")]
    [InlineData("future-subtype")]
    [InlineData("0000000c-0cea-0010-8000-00aa00389b71")]
    public void UnknownAndEncodedOutputSubtypesStayRaw(string raw)
    {
        Assert.Equal(raw, Names().Describe(raw));
        Assert.Equal("    TrueHD/MLP: supported no\n", Names().Summary([raw], ["TrueHD/MLP"]));
    }

    [Fact]
    public void AliasSupportIsUnionedAndEveryNamedCodecGetsAYesOrNo()
    {
        string[] observed = ["{31435641-0000-0010-8000-00AA00389B71}", "00000003-0000-0010-8000-00aa00389b71"];
        Assert.Equal("    H264: supported yes\n    HEVC: supported no\n    PCM: supported yes\n    TrueHD/MLP: supported no\n",
            Names().Summary(observed, ["H264", "HEVC", "PCM", "TrueHD/MLP"]));
    }

    [Theory]
    [InlineData("eb27cec4-163e-4ca3-8b74-8e25f91b517e")]
    [InlineData("20504c4d-0000-0010-8000-00aa00389b71")]
    public void TrueHdAndMlpAreBothRecognizedWithoutNameGuessing(string raw)
    {
        Assert.Equal("    TrueHD/MLP: supported yes\n", Names().Summary([raw], ["TrueHD/MLP"]));
    }

    [Fact]
    public void DuplicateAliasesAndRequestedNamesDoNotDuplicateSummaryLines()
    {
        var names = new DebugCodecNames(
        [
            ("00000001-0000-0010-8000-00aa00389b71", "PCM"),
            ("{00000001-0000-0010-8000-00AA00389B71}", "PCM"),
        ]);
        Assert.Equal("    PCM: supported yes\n", names.Summary(["00000001-0000-0010-8000-00aa00389b71"], ["PCM", "pcm"]));
    }

    [Fact]
    public void Av1UsesItsGuidWithoutRequiringANewerUwpProjection()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.Capabilities.cs.txt"));
        Assert.Contains("(\"31305641-0000-0010-8000-00aa00389b71\", \"AV1\")", source);
        Assert.DoesNotContain("CodecSubtypes.VideoFormatAv1", source);
    }

    [Fact]
    public void CapabilityProbesStayGuardedSeparateAndReadOnly()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.Capabilities.cs.txt"));
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("FindAllAsync(kind, CodecCategory.Decoder, string.Empty).AsTask()", source);
        Assert.Contains("CodecKind.Video", source);
        Assert.Contains("CodecKind.Audio", source);
        Assert.Contains("await OnUiAsync(DescribeDisplayCapabilities)", source);
        Assert.Contains("if (hdmi is null)", source);
        Assert.Contains("GetSupportedDisplayModes().Select(DescribeDisplayMode).Distinct", source);
        Assert.Contains("GetDefaultAudioRenderId(AudioDeviceRole.Default)", source);
        Assert.Contains("AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Media)).AsTask()", source);
        Assert.Contains("using var graph = created.Graph;", source);
        Assert.Contains("var baseline = XboxAudioCapabilities.Current;", source);
        Assert.DoesNotContain(".GetType(", source);
        Assert.DoesNotContain(".Resources[", source);
        Assert.DoesNotContain("graph.Start(", source);
        var server = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.cs.txt"));
        Assert.Contains("case (\"GET\", \"/caps\"):", server);
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bale-debug.sh"));
        Assert.Contains("caps) curl -fsS --max-time 18 -G \"$BASE/caps\"", script);
    }
}
