namespace Bale.Core.Tests;

// Source-contract checks, not D3D/MF execution. The wrapper workflow compiles the complete patch series.
public class FilmGrainPatchTests
{
    private static readonly string Patch = Read("0009-rust-film-grain.patch");
    private static readonly string AddedSource = string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..]));
    private static readonly string[] Exports =
    [
        "bale_grain_abi_version", "bale_grain_validate", "bale_grain_cpu_apply",
        "bale_grain_gpu_create", "bale_grain_gpu_apply", "bale_grain_gpu_destroy",
    ];

    [Fact]
    public void PatchChangesOnlyNativeSourceAndConsumesAnIndependentOptInOption()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(6, paths.Length);
        Assert.All(paths, path => Assert.Matches(@"^Source/[^/]+\.(cpp|h)$", path));
        Assert.Contains("Source/BaleFilmGrain.h", paths);
        Assert.Contains("int baleFilmGrainMode = 0;", AddedSource);
        Assert.Contains("key == \"bale_film_grain_mode\") { target = &baleFilmGrainMode; minimum = 0; maximum = 2; }", AddedSource);
        Assert.Contains("if (exportOnly)\n            reason = requested ? \"export-only override; mode ignored\" : \"export-only override\";", AddedSource);
        Assert.Contains("if (dav1d && (baleDav1dExportFilmGrain > 0 || filmGrainMode > 0))", AddedSource);
        Assert.Contains("avVideoCodecCtx->export_side_data |= AV_CODEC_EXPORT_DATA_FILM_GRAIN;", Patch);
        var select = Patch.IndexOf("filmGrainMode = BaleFilmGrain::SelectMode", StringComparison.Ordinal);
        // The open call itself sits just past the hunk; this context line directly precedes it.
        var open = Patch.IndexOf("// Bale: dav1d spawns its workers inside avcodec_open2.", StringComparison.Ordinal);
        Assert.True(select >= 0 && open > select);
        Assert.Contains("!dav1d || hardware", AddedSource);
        Assert.Contains("!BitDepth(input)", AddedSource);
        Assert.Contains("!Compatible(input, output)", AddedSource);
        Assert.Contains("selected == 2 && uploadVariant != 1", AddedSource);
        Assert.Contains("selected = 1;", AddedSource);
    }

    [Fact]
    public void LoaderIsOncePerProcessAndRequiresTheCompleteGrainAbi()
    {
        Assert.Contains("static const BaleGrainExports exports = []", AddedSource);
        Assert.Contains("LoadPackagedLibrary(L\"bale_gpu.dll\", 0)", AddedSource);
        foreach (var symbol in Exports) Assert.Contains($"GetProcAddress(module, \"{symbol}\")", AddedSource);
        Assert.Contains("!api.abiVersion || !api.validate || !api.cpuApply || !api.gpuCreate || !api.gpuApply || !api.gpuDestroy || api.abiVersion() != 1", AddedSource);
        Assert.Contains("FreeLibrary(module);\n                return BaleGrainExports{};", AddedSource);
        Assert.Contains("static_assert(sizeof(BaleGrainParams) == 184);", AddedSource);
        Assert.Contains("static_assert(alignof(BaleGrainParams) == 4);", AddedSource);
    }

    [Theory]
    [InlineData("struct_size", 0)]
    [InlineData("seed", 4)]
    [InlineData("width", 8)]
    [InlineData("height", 12)]
    [InlineData("bit_depth", 16)]
    [InlineData("matrix_identity", 17)]
    [InlineData("num_y_points", 18)]
    [InlineData("chroma_scaling_from_luma", 19)]
    [InlineData("num_uv_points", 20)]
    [InlineData("scaling_shift", 22)]
    [InlineData("ar_coeff_lag", 23)]
    [InlineData("ar_coeff_shift", 24)]
    [InlineData("grain_scale_shift", 25)]
    [InlineData("overlap_flag", 26)]
    [InlineData("clip_to_restricted_range", 27)]
    [InlineData("y_points", 28)]
    [InlineData("uv_points", 56)]
    [InlineData("ar_coeffs_y", 96)]
    [InlineData("ar_coeffs_uv", 120)]
    [InlineData("reserved0", 170)]
    [InlineData("uv_mult", 172)]
    [InlineData("uv_luma_mult", 176)]
    [InlineData("uv_offset", 180)]
    public void EveryRustAbiFieldOffsetIsAsserted(string field, int offset) =>
        Assert.Contains($"static_assert(offsetof(BaleGrainParams, {field}) == {offset});", AddedSource);

    [Fact]
    public void MapperUsesVisibleFrameMetadataAndZeroesUnusedStorageBeforeRustValidation()
    {
        foreach (var text in new[]
        {
            "AV_FRAME_DATA_FILM_GRAIN_PARAMS", "if (!side) return false;", "source->type != AV_FILM_GRAIN_PARAMS_AV1",
            "side->size < offsetof(AVFilmGrainParams, codec) + sizeof(AVFilmGrainAOMParams)",
            "width != frame->width || height != frame->height", "params = {};", "source.seed & 0xffff",
            "params.width = frame->width;", "params.height = frame->height;",
            "BitDepth(static_cast<AVPixelFormat>(frame->format))", "frame->colorspace == AVCOL_SPC_RGB",
            "static_cast<int16_t>(aom.uv_mult_luma[plane])", "static_cast<uint8_t>(aom.limit_output_range)",
            "if (aom.num_y_points) std::memcpy(params.ar_coeffs_y", "if (aom.num_uv_points[plane] || aom.chroma_scaling_from_luma)",
            "yCoeffs + (aom.num_y_points != 0)", "BaleGrainExports::Get().validate(&params) != S_OK",
        }) Assert.Contains(text, AddedSource);
        Assert.DoesNotContain("memcpy(&params", AddedSource);
    }

    [Fact]
    public void CpuGrainOnlyTouchesOwnedOutputAndRestoresTheCleanFrameOnFailure()
    {
        Assert.Contains("if (outputDirectBuffer && !hasGrain)", AddedSource);
        Assert.Contains("CopyFrameToOutput(avFrame, data, linesize, buffer);", AddedSource);
        Assert.Contains("hasGrain && !FilmGrain.ApplyCpu(grainParams, m_OutputPixelFormat, data, linesize)", AddedSource);
        Assert.Contains("params && FilmGrain.mode == 1 && !FilmGrain.ApplyCpu(*params, m_OutputPixelFormat, data, linesize)", AddedSource);
        Assert.Contains("winrt::check_hresult(CopyFrameToOutput(avFrame, data, linesize, nullptr));", AddedSource);
        Assert.Contains("FilmGrain.UploadDisabled(hr);", AddedSource);
        Assert.Contains("FilmGrain.NoUpload();", AddedSource);
        Assert.DoesNotContain("cpuApply(&params, output == AV_PIX_FMT_NV12 ? 1U : 2U,\n            frame->data", AddedSource);
    }

    [Fact]
    public void GpuGrainReplacesTheCopyAndFailureOverwritesTheTargetBeforePermanentCpuFallback()
    {
        Assert.Contains("if (!params || grain.mode != 2 || !ApplyGrain(grain, *params, source.texture.get(), texture.get()))\n+                context->CopyResource(texture.get(), source.texture.get());", Patch);
        Assert.Contains("api.gpuCreate(device.get(), context.get(), &grainHandle)", AddedSource);
        Assert.Contains("api.gpuApply(grainHandle, &params, source, target)", AddedSource);
        Assert.Contains("grain.GpuFailed(hr == S_OK ? E_UNEXPECTED : hr, true);", AddedSource);
        Assert.Contains("grain.GpuFailed(hr, false);\n        DestroyGrain();", AddedSource);
        Assert.Contains("void GpuFailed(HRESULT hr, bool creating)\n    {\n        mode = 1;", AddedSource);
        Assert.Contains("void EndGrain() { DestroyGrain(); }", AddedSource);
        Assert.Contains("if (hr != S_OK && upload) upload->EndGrain();", AddedSource);
        Assert.Contains("DestroyGrain();\n         std::lock_guard lock(samplesMutex);", Patch);
        Assert.Contains("BaleGrainExports::Get().gpuDestroy(grainHandle);\n            grainHandle = nullptr;", AddedSource);
    }

    [Fact]
    public void EachRuntimeFallbackHasAPerStreamOnceGuardAndFixedDiagnostic()
    {
        foreach (var flag in new[] { "invalidLogged", "scalingLogged", "formatLogged", "cpuLogged", "gpuLogged", "uploadLogged", "missingUploadLogged" })
            Assert.Contains($"if (!{flag})", AddedSource);
        foreach (var line in AddedSource.Split('\n').Where(line => line.Contains("av_log(", StringComparison.Ordinal)))
        {
            Assert.Contains("\"Bale film grain:", line);
            Assert.DoesNotContain("params.", line);
            Assert.DoesNotContain("frame->", line);
            Assert.DoesNotContain("what()", line);
        }
    }

    [Fact]
    public void AppCiInspectsAllSixExportsFromTheActualPackagedDll()
    {
        var workflow = Read("ci.yml.txt");
        var start = workflow.IndexOf("$entry = $zip.GetEntry('bale_gpu.dll')", StringComparison.Ordinal);
        var end = workflow.IndexOf("Packaged bale_gpu.dll: all six grain ABI exports verified", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var inspection = workflow[start..end];
        Assert.Contains("ExtractToFile($entry, $packagedDll)", inspection);
        Assert.Contains("$exports = & $dumpbin.FullName /exports $packagedDll", inspection);
        Assert.Contains("if ($LASTEXITCODE -ne 0)", inspection);
        foreach (var symbol in Exports) Assert.Contains($"'{symbol}'", inspection);
        Assert.Contains("throw \"Packaged bale_gpu.dll is missing export $symbol\"", inspection);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));
}
