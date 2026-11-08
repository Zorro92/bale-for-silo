namespace Bale.Core.Tests;

// Source-contract checks for 0010 on top of immutable 0009, not D3D/MF execution.
public class FilmGrainFallbackPatchTests
{
    private static readonly string Patch = Read("0010-film-grain-fallback-policy.patch");
    private static readonly string UpdatedSource = string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith(' ') || line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..]));

    [Fact]
    public void PatchChangesOnlyTheGrainPolicyAndSoftwareProviderNativeSources()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(new[] { "Source/BaleFilmGrain.h", "Source/FFmpegMediaSource.cpp", "Source/UncompressedVideoSampleProvider.cpp" }, paths);
        Assert.All(paths, path => Assert.Matches(@"^Source/[^/]+\.(cpp|h)$", path));
        Assert.DoesNotContain("export_side_data", Patch);
        Assert.DoesNotContain("avcodec_open2", Patch);
    }

    [Fact]
    public void GpuWithoutRenderTargetUploadKeepsDav1dBeforeExportIsEnabled()
    {
        Assert.Contains("if (selected == 2 && uploadVariant != 1)", UpdatedSource);
        Assert.Contains("selected = 0;", UpdatedSource);
        Assert.Contains("reason = \"GPU requires render-target upload variant 1; keeping dav1d\";", UpdatedSource);
        Assert.DoesNotContain("selected = 1;", UpdatedSource);
        // 0009 still enables export only for a selected Rust mode or the independent export-only knob.
        Assert.Contains("if (dav1d && (baleDav1dExportFilmGrain > 0 || filmGrainMode > 0))", Read("0009-rust-film-grain.patch"));
    }

    [Fact]
    public void ProviderCapturesTheSameAbove1080pThresholdAsTheAppAtCreation()
    {
        Assert.Contains("bool above1080p = false;", UpdatedSource);
        Assert.Contains("uncompressed->FilmGrain.mode = filmGrainMode;\n            uncompressed->FilmGrain.above1080p = avVideoCodecCtx->width > 1920 || avVideoCodecCtx->height > 1080;", UpdatedSource);
        Assert.Single(UpdatedSource.Split("above1080p = avVideoCodecCtx").Skip(1));
    }

    [Theory]
    [InlineData("GpuFailed(HRESULT hr, bool creating)", "gpuLogged")]
    [InlineData("UploadDisabled(HRESULT hr)", "uploadLogged")]
    [InlineData("NoUpload()", "missingUploadLogged")]
    public void RuntimeLossOnlyDowngradesGpuAndPermanentlySelectsCpuOrNoGrain(string method, string guard)
    {
        var start = UpdatedSource.IndexOf("    void " + method, StringComparison.Ordinal);
        var end = UpdatedSource.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = UpdatedSource[start..end];
        // Explicit CPU and already-downgraded streams are unaffected, including after seek.
        Assert.Contains("if (mode != 2) return;\n        mode = above1080p ? 0 : 1;", body);
        Assert.Contains($"if (!{guard})", body);
        Assert.Contains($"{guard} = true;", body);
        Assert.Contains("showing ungrained frames (above 1080p).", body);
        Assert.Contains("using CPU grain.", body);
        Assert.DoesNotContain("mode = 2", body);
    }

    [Fact]
    public void ModeZeroSkipsPreparationAndClearsCachedGrainBeforeCurrentFrameCpuOutput()
    {
        Assert.Contains("if (!mode) return false;\n        auto side = av_frame_get_side_data", UpdatedSource);
        Assert.Contains("FilmGrain.NoUpload();\n    // Prepare ran before upload could fail: mode 0 must also skip CPU grain on this very frame.\n    hasGrain = hasGrain && FilmGrain.mode != 0;\n    if (outputDirectBuffer && !hasGrain)", UpdatedSource);
        Assert.Contains("hasGrain && !FilmGrain.ApplyCpu(grainParams", Read("0009-rust-film-grain.patch"));
    }

    [Theory]
    [InlineData("GPU create failed (hr=0x%08X)")]
    [InlineData("GPU apply failed (hr=0x%08X)")]
    [InlineData("upload disabled (hr=0x%08X)")]
    [InlineData("upload unavailable")]
    public void EachRuntimeLossHasFixedSafeDiagnosticsForBothSizeClasses(string diagnostic)
    {
        Assert.Contains($"\"Bale film grain: {diagnostic}; showing ungrained frames (above 1080p).\\n\"", UpdatedSource);
        Assert.Contains($"\"Bale film grain: {diagnostic}; using CPU grain.\\n\"", UpdatedSource);
        foreach (var line in UpdatedSource.Split('\n').Where(line => line.Contains("av_log(", StringComparison.Ordinal)))
        {
            Assert.Contains("\"Bale film grain:", line);
            Assert.DoesNotContain("%s", line);
            Assert.DoesNotContain("params.", line);
            Assert.DoesNotContain("frame->", line);
            Assert.DoesNotContain("what()", line);
            if (line.Contains("hr=", StringComparison.Ordinal)) Assert.Contains("static_cast<unsigned>(hr)", line);
        }
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));
}
