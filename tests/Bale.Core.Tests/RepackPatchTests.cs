namespace Bale.Core.Tests;

// Source-contract checks for the 0012 debug GPU repack, not D3D/MF execution.
public class RepackPatchTests
{
    private static readonly string Patch = Read("0012-d3d11-gpu-repack.patch");
    private static readonly string Added = string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..]));

    [Fact]
    public void PatchTouchesOnlyNativeSources()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(new[]
        {
            "Source/BaleD3D11Upload.h", "Source/FFmpegMediaSource.cpp", "Source/FFmpegMediaSource.h",
            "Source/UncompressedVideoSampleProvider.cpp", "Source/UncompressedVideoSampleProvider.h",
        }, paths);
    }

    [Fact]
    public void OptionIsBoundedAndOffByDefault()
    {
        Assert.Contains("else if (key == \"bale_d3d11_repack\") { target = &baleD3d11Repack; minimum = 0; maximum = 1; }", Added);
        Assert.Contains("int baleD3d11Repack = 0;", Added);
        Assert.Contains("int Repack = 0;", Added);
        Assert.Contains("uncompressed->Repack = baleD3d11Repack;", Added);
    }

    [Fact]
    public void LoadsTheRepackAbiIndependentlyOfGrain()
    {
        foreach (var export in new[] { "bale_repack_abi_version", "bale_repack_gpu_create", "bale_repack_gpu_apply", "bale_repack_gpu_destroy" })
            Assert.Contains($"GetProcAddress(module, \"{export}\")", Added);
        Assert.Contains("api.abiVersion() != 1", Added);
        Assert.Contains("LoadPackagedLibrary(L\"bale_gpu.dll\", 0)", Added);
    }

    [Fact]
    public void RepackIsLimitedToUnscaledPlanarInputWithoutCpuGrain()
    {
        var check = Added[Added.IndexOf("bool UncompressedVideoSampleProvider::CanRepack(", StringComparison.Ordinal)..];
        check = check[..check.IndexOf("\n}", StringComparison.Ordinal)];
        foreach (var condition in new[]
        {
            "!Repack || repackFailed || !upload || !upload->CanRepack()",
            "params && FilmGrain.mode == 1",
            "input == AV_PIX_FMT_YUV420P && m_OutputPixelFormat == AV_PIX_FMT_NV12",
            "input == AV_PIX_FMT_YUV420P10LE && m_OutputPixelFormat == AV_PIX_FMT_P010LE",
            "avFrame->width == outputWidth && avFrame->height == outputHeight",
            "outputFrameWidth % 4 == 0 && outputFrameHeight % 2 == 0",
        })
            Assert.Contains(condition, check);
        Assert.DoesNotContain("YUVJ420P", check.Replace("YUVJ is excluded", "", StringComparison.Ordinal));
        Assert.Contains("bool CanRepack() const { return (bindFlags & D3D11_BIND_RENDER_TARGET) && BaleRepackExports::Get().create; }", Added);
    }

    [Fact]
    public void PlanarFillPutsVAtHalfTheTextureRow()
    {
        Assert.Contains("av_image_copy_plane(data[1] + static_cast<size_t>(outputFrameWidth / 2) * bytes, linesize,", Added);
        Assert.Contains("avFrame->data[2], avFrame->linesize[2], chromaWidth * bytes, chromaHeight);", Added);
    }

    [Fact]
    public void FailureDisablesRepackAndRetriesTheSameFrameThroughSwscale()
    {
        var planar = Added.IndexOf("UploadFrame(avFrame, surface, framePts, params, true);", StringComparison.Ordinal);
        var disable = Added.IndexOf("DisableRepack(winrt::to_hresult());", StringComparison.Ordinal);
        var retry = Added.IndexOf("UploadFrame(avFrame, surface, framePts, params, false);", StringComparison.Ordinal);
        Assert.True(planar >= 0 && disable > planar && retry > disable);
        Assert.Contains("\"Bale D3D11 repack disabled (hr=0x%08X); using swscale.\\n\"", Added);
        Assert.Contains("if (upload) upload->DestroyRepack();", Added);
    }

    [Fact]
    public void PlanarStagingAlwaysEndsInARepackDraw()
    {
        var path = Added[Added.IndexOf("if (planar)\n            {", StringComparison.Ordinal)..];
        var scratch = path.IndexOf("ApplyRepack(source.texture.get(), scratch);", StringComparison.Ordinal);
        var grain = path.IndexOf("grained = ApplyGrain(grain, *params, scratch, texture.get());", StringComparison.Ordinal);
        var clean = path.IndexOf("if (!grained) ApplyRepack(source.texture.get(), texture.get());", StringComparison.Ordinal);
        Assert.True(scratch >= 0 && grain > scratch && clean > grain);
        Assert.Contains("repackScratch = nullptr;", Added);
    }

    [Fact]
    public void LogsNeverIncludeUrlsOrPaths()
    {
        foreach (var line in Added.Split('\n').Where(line => line.Contains("av_log(", StringComparison.Ordinal)))
            Assert.Contains("\"Bale D3D11 repack ", line);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
