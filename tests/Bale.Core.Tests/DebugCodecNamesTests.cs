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
        ("e06d802c-db46-11cf-b4d1-00805f6cbbea", "AC3"),
        ("a7fb87af-2d02-42fb-a4d4-05cd93843bdd", "EAC3"),
        ("00002001-0000-0010-8000-00aa00389b71", "DTS"),
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
        Assert.Equal("    TrueHD/MLP: no\n", Names().Summary([("Decoder MFT", [raw])], ["TrueHD/MLP"]));
    }

    [Fact]
    public void AliasSupportIsUnionedAndEveryNamedCodecGetsADecodeOrNativeStatus()
    {
        string[] observed = ["{31435641-0000-0010-8000-00AA00389B71}", "00000003-0000-0010-8000-00aa00389b71"];
        Assert.Equal("    H264: decode yes\n    HEVC: no\n    PCM: native (no decoder needed)\n    TrueHD/MLP: no\n",
            Names().Summary([("Decoder MFT", observed)], ["H264", "HEVC", "PCM", "TrueHD/MLP"]));
    }

    [Theory]
    [InlineData("eb27cec4-163e-4ca3-8b74-8e25f91b517e")]
    [InlineData("20504c4d-0000-0010-8000-00aa00389b71")]
    public void TrueHdAndMlpAreBothRecognizedWithoutNameGuessing(string raw)
    {
        Assert.Equal("    TrueHD/MLP: decode yes\n", Names().Summary([("Decoder MFT", [raw])], ["TrueHD/MLP"]));
    }

    [Fact]
    public void DuplicateAliasesAndRequestedNamesDoNotDuplicateSummaryLines()
    {
        var names = new DebugCodecNames(
        [
            ("34363248-0000-0010-8000-00aa00389b71", "H264"),
            ("{34363248-0000-0010-8000-00AA00389B71}", "H264"),
        ]);
        Assert.Equal("    H264: decode yes\n", names.Summary([("Decoder MFT", ["34363248-0000-0010-8000-00aa00389b71"])], ["H264", "h264"]));
    }

    [Theory]
    [InlineData("DTS IEC-61937 converter MFT", "00002001-0000-0010-8000-00aa00389b71", "DTS")]
    [InlineData("Dolby TrueHD IEC-61937 converter MFT", "eb27cec4-163e-4ca3-8b74-8e25f91b517e", "TrueHD/MLP")]
    [InlineData("Dolby MLP IEC61937 converter MFT", "20504c4d-0000-0010-8000-00aa00389b71", "TrueHD/MLP")]
    [InlineData("dts iec61937 converter mft", "{00002001-0000-0010-8000-00AA00389B71}", "DTS")]
    public void Iec61937ConvertersAloneNeverClaimDecoding(string displayName, string subtype, string name)
    {
        Assert.Equal($"    {name}: passthrough only (IEC-61937 converter)\n",
            Names().Summary([(displayName, [subtype])], [name]));
    }

    [Fact]
    public void DolbyDecodersAndConvertersAreReportedSeparatelyPerSubtype()
    {
        const string ac3 = "e06d802c-db46-11cf-b4d1-00805f6cbbea";
        const string eac3 = "a7fb87af-2d02-42fb-a4d4-05cd93843bdd";
        Assert.Equal("    AC3: decode yes, passthrough converter yes\n    EAC3: decode yes, passthrough converter yes\n    DTS: no\n",
            Names().Summary(
            [
                ("Microsoft Dolby Digital Plus Decoder MFT", [ac3, eac3]),
                ("Dolby Digital IEC-61937 converter MFT", [ac3]),
                ("Dolby Digital Plus IEC61937 converter MFT", [eac3]),
            ], ["AC3", "EAC3", "DTS"]));
    }

    [Fact]
    public void AnotherCodecsDecoderCannotTurnAConverterIntoDecodeSupport()
    {
        Assert.Equal("    H264: decode yes\n    DTS: passthrough only (IEC-61937 converter)\n    EAC3: no\n",
            Names().Summary(
            [
                ("Video Decoder MFT", ["34363248-0000-0010-8000-00aa00389b71"]),
                ("DTS IEC-61937 converter MFT", ["00002001-0000-0010-8000-00aa00389b71"]),
                ("Dolby Digital Plus Decoder MFT", []),
            ], ["H264", "DTS", "EAC3"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARealTrueHdDecoderCountsEvenWhenAConverterListsADifferentAlias(bool reverse)
    {
        (string DisplayName, IEnumerable<string> Subtypes)[] codecs =
        [
            ("TrueHD Decoder MFT", ["{EB27CEC4-163E-4CA3-8B74-8E25F91B517E}"]),
            ("Dolby MLP IEC-61937 converter MFT", ["20504c4d-0000-0010-8000-00aa00389b71"]),
        ];
        Assert.Equal("    TrueHD/MLP: decode yes, passthrough converter yes\n",
            Names().Summary(reverse ? codecs.Reverse() : codecs, ["TrueHD/MLP"]));
    }

    [Fact]
    public void NativePcmNeedsNoDecoderMft()
    {
        Assert.Equal("    PCM: native (no decoder needed)\n    DTS: no\n", Names().Summary([], ["PCM", "DTS"]));
    }

    [Fact]
    public void EncodedOutputSubtypesDoNotImplyInputSupportEvenOnAConverter()
    {
        Assert.Equal("    TrueHD/MLP: no\n",
            Names().Summary([("Dolby TrueHD IEC61937 converter MFT", ["0000000c-0cea-0010-8000-00aa00389b71"])], ["TrueHD/MLP"]));
    }

    [Fact]
    public void Av1UsesItsGuidWithoutRequiringANewerUwpProjection()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.Capabilities.cs.txt"));
        Assert.Contains("(\"31305641-0000-0010-8000-00aa00389b71\", \"AV1\")", source);
        Assert.DoesNotContain("CodecSubtypes.VideoFormatAv1", source);
    }

    [Theory]
    [InlineData("1b81bea3-a0c7-11d3-b984-00c04f2e73c5", "VC1_VLD")]
    [InlineData("efd64d74-c9e8-41d7-a5e9-e9b0e39fa319", "MPEG4PT2_VLD_SIMPLE")]
    [InlineData("705b9d82-76cf-49d6-b7e6-ac8872db013c", "H264_VLD_MULTIVIEW_NOFGT")]
    public void SeriesXGpuProfilesHaveExplicitNames(string guid, string name)
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.Capabilities.cs.txt"));
        Assert.Contains($"[new Guid(\"{guid}\")] = \"{name}\"", source);
    }

    [Fact]
    public void CapabilityProbesStayGuardedSeparateAndReadOnly()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.Capabilities.cs.txt"));
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("FindAllAsync(kind, CodecCategory.Decoder, string.Empty).AsTask()", source);
        Assert.Contains("CodecKind.Video", source);
        Assert.Contains("CodecKind.Audio", source);
        Assert.Contains("if (Run(\"gpu\")) AppendGpuCapabilities(builder);", source);
        Assert.Contains("IntPtr* device, int* featureLevel, IntPtr* immediateContext", source);
        Assert.Contains("observed.Add((codec.DisplayName, supported));", source);
        Assert.Contains("names.Summary(observed, codecNames)", source);
        Assert.Contains("await OnUiAsync(DescribeDisplayCapabilities)", source);
        Assert.Contains("var capabilities = DisplayHdrOutput.DetectCapabilities()", source);
        Assert.Contains("  Bale HDR: Hdr10={capabilities.Hdr.Hdr10}", source);
        Assert.Contains("hdr_evidence={capabilities.DisplayHdrEvidence}", source);
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
