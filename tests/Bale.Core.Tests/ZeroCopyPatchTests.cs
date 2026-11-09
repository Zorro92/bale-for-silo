namespace Bale.Core.Tests;

// Source-contract checks for 0015 (debug zero-copy), not D3D/MF execution.
public class ZeroCopyPatchTests
{
    private static readonly string Patch = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "0015-zero-copy-landing.patch"));
    private static readonly string Added = string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..]));
    // Post-patch side of every hunk (added plus context lines), for ordering against unchanged code.
    private static readonly string Patched = string.Join('\n', Patch.Split('\n')
        .Where(line => (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)) || line.StartsWith(' '))
        .Select(line => line[1..]));

    private static string Body(string signature, string? text = null)
    {
        text ??= Added;
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = text.IndexOf("\n}\n", start, StringComparison.Ordinal);
        Assert.True(end > start, signature);
        return text[start..end];
    }

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
    public void OptionIsBinaryAndNeedsTheUpload()
    {
        Assert.Contains("else if (key == \"bale_zero_copy\") { target = &baleZeroCopy; minimum = 0; maximum = 1; }", Added);
        Assert.Contains("int baleZeroCopy = 0;", Added);
        Assert.Contains("uncompressed->ZeroCopy = baleD3d11Upload > 0 && !config->IsFrameGrabber ? baleZeroCopy : 0;", Added);
    }

    [Fact]
    public void ExportsNeedAbiOneAndEveryFunction()
    {
        foreach (var name in new[] { "abi_version", "create", "alloc", "release", "repack", "stats", "destroy" })
        {
            Assert.Contains($"GetProcAddress(module, \"bale_zc_{name}\")", Added);
        }
        Assert.Contains("!api.destroy || api.abiVersion() != 1)", Added);
        Assert.Contains("static_assert(sizeof(BaleZcFrame) == 40", Added);
        Assert.Contains("static_assert(sizeof(BaleZcStats) == 56", Added);
    }

    [Fact]
    public void HookIsOnlyForSoftwareTenBitDav1dToP010()
    {
        var install = Added[Added.IndexOf("else if (ZeroCopy && decoder == DecoderEngine::FFmpegSoftwareDecoder", StringComparison.Ordinal)..];
        Assert.Contains("std::string(m_pAvCodecCtx->codec->name) == \"libdav1d\"", install);
        Assert.Contains("m_pAvCodecCtx->pix_fmt == AV_PIX_FMT_YUV420P10LE && m_OutputPixelFormat == AV_PIX_FMT_P010LE", install);
        Assert.Contains("m_pAvCodecCtx->get_buffer2 = get_zero_copy_buffer;", install);
    }

    [Fact]
    public void HookDeclinesWithoutAHandleAndReleasesThroughTheBuffer()
    {
        var hook = Body("int UncompressedVideoSampleProvider::get_zero_copy_buffer(");
        Assert.Contains("if (!handle || frame->format != AV_PIX_FMT_YUV420P10LE) return AVERROR(ENOSYS);", hook);
        Assert.Contains("static constexpr size_t align = 64;", hook);
        Assert.Contains("size_t offsetU = alignUp(lumaSize + align);", hook);
        Assert.Contains("size_t offsetV = alignUp(offsetU + chromaSize + align);", hook);
        Assert.Contains("size_t bytes = offsetV + chromaSize + align;", hook);
        Assert.Contains("BaleZeroCopyExports::Get().release(opaque); }, id, 0);", hook);
        Assert.DoesNotContain("avcodec_default_get_buffer2", hook);
    }

    [Fact]
    public void OnlyForeignOrRefusedFramesKeepTheHandle()
    {
        var path = Body("bool UncompressedVideoSampleProvider::TryZeroCopy(");
        Assert.Contains("if (!handle || (params && FilmGrain.mode == 1)) return false;", path);
        Assert.Contains("api.repack(handle, av_buffer_get_opaque(buffer), &frame, &landing);", path);
        Assert.Contains("if (hr == S_FALSE) return false;", path);
        Assert.Contains("if (hr == E_INVALIDARG)", path);
        Assert.Contains("DisableZeroCopy(hr == S_OK ? E_UNEXPECTED : hr);", path);
        Assert.Contains("uploadSample = upload->CreateLandedSample(texture.get(), ConvertPosition(framePts), surface, FilmGrain, params);", path);
        Assert.Contains("timing.EndFrame(2);", path);
        Assert.Contains("if (TryZeroCopy(avFrame, surface, framePts, params)) return S_OK;\n            if (CanRepack(avFrame, params))", Patched);
    }

    [Fact]
    public void UploadLossAndTeardownDestroyTheHandleFirst()
    {
        Assert.Contains("DisableZeroCopy(hr);\n    upload.reset();", Patched);
        var teardown = Body("UncompressedVideoSampleProvider::~UncompressedVideoSampleProvider()", Patched);
        var unhook = teardown.IndexOf("m_pAvCodecCtx->get_buffer2 = avcodec_default_get_buffer2;", StringComparison.Ordinal);
        Assert.True(unhook >= 0 && unhook < teardown.IndexOf("DisableZeroCopy(S_OK);", StringComparison.Ordinal));
        Assert.Contains("auto handle = zeroCopy.exchange(nullptr);", Body("void UncompressedVideoSampleProvider::DisableZeroCopy("));
    }

    [Fact]
    public void LandedSamplesCopyOrGrainLikeStaging()
    {
        Assert.Contains("if (!params || grain.mode != 2 || !ApplyGrain(grain, *params, landing, texture))", Added);
        Assert.Contains("context->CopyResource(texture, landing);", Added);
    }
}
