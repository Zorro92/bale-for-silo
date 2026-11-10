namespace Bale.Core.Tests;

// Source-contract checks for 0020 (software libdav1d AV1 with zero-copy requested is zero-copy only), not D3D/MF execution.
public class ZeroCopyOnlyPatchTests
{
    private static readonly string Patch = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "0020-zero-copy-only.patch"));
    private static readonly string Added = Lines('+');
    private static readonly string Removed = Lines('-');

    private static string Lines(char sign) => string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith(sign) && !line.StartsWith(new string(sign, 3), StringComparison.Ordinal))
        .Select(line => line[1..]));

    [Fact]
    public void PatchTouchesOnlyNativeSourcesAndDeletesTheCopyPool()
    {
        var sources = Patch.Split('\n').Where(line => line.StartsWith("--- a/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(new[]
        {
            "Source/BaleCopyPool.h",
            "Source/BaleD3D11Upload.h",
            "Source/BaleFilmGrain.h",
            "Source/FFmpegMediaSource.cpp",
            "Source/FFmpegMediaSource.h",
            "Source/UncompressedSampleProvider.cpp",
            "Source/UncompressedSampleProvider.h",
            "Source/UncompressedVideoSampleProvider.cpp",
            "Source/UncompressedVideoSampleProvider.h",
        }, sources);
        var targets = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(sources.Where(path => path != "Source/BaleCopyPool.h"), targets);
        // The only deletion: the debug repack's planar copy pool. No file is added.
        Assert.Contains("diff --git a/Source/BaleCopyPool.h b/Source/BaleCopyPool.h\ndeleted file mode 100644\n", Patch);
        Assert.Contains("--- a/Source/BaleCopyPool.h\n+++ /dev/null\n", Patch);
        Assert.Single(Patch.Split('\n'), line => line == "+++ /dev/null");
        Assert.DoesNotContain("new file mode", Patch);
        Assert.DoesNotContain("--- /dev/null", Patch);
        Assert.DoesNotContain("\r", Patch);
        Assert.Contains("#include \"BaleCopyPool.h\"", Removed);
        Assert.DoesNotContain("BaleCopyPool", Added);
    }

    [Fact]
    public void DebugAndStagingOptionKeysAreRejected()
    {
        foreach (var key in new[] { "bale_sws_threads", "bale_d3d11_upload_fault_frames", "bale_d3d11_repack" })
        {
            Assert.Contains($"else if (key == \"{key}\")", Removed);
            Assert.DoesNotContain(key, Added);
        }
        Assert.Contains("int baleSwsThreads = 1;", Removed);
        Assert.Contains("int baleD3d11UploadFaultFrames = 0;", Removed);
        Assert.Contains("int baleD3d11Repack = 0;", Removed);
        // The surviving keys keep their ranges; any nonzero upload value is the D3D12-writable texture.
        Assert.DoesNotContain("else if (key == \"bale_d3d11_upload\")", Removed);
        Assert.DoesNotContain("else if (key == \"bale_zero_copy\")", Removed);
        Assert.Contains("// 0 = off (upstream CPU output); any nonzero value = the D3D12-writable upload texture.", Added);
    }

    [Fact]
    public void OnlyTheD3d12WritablePoolTextureRemains()
    {
        Assert.Contains("case 6:", Removed);
        Assert.Contains("case 5: bindFlags = D3D11_BIND_SHADER_RESOURCE; miscFlags = D3D11_RESOURCE_MISC_SHARED; break;", Removed);
        Assert.Contains("UNREFERENCED_PARAMETER(variant);", Added);
        Assert.Contains("UINT bindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;", Added);
        Assert.Contains("UINT miscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED;", Added);
        Assert.Contains("uploadVariant == 6", Removed);
        Assert.DoesNotContain("uploadVariant", Added);
        Assert.DoesNotContain("upload->CanWriteDirect()", Added);
    }

    [Fact]
    public void StagingUploadThreadedSwscaleFaultInjectionAndRepackAreRemoved()
    {
        Assert.Contains("HRESULT Map(UINT width, UINT height, DXGI_FORMAT format, Mapping& target)", Removed);
        Assert.Contains("void UncompressedVideoSampleProvider::UploadFrame(", Removed);
        Assert.Contains("HRESULT UncompressedVideoSampleProvider::InitializeThreadedScaler(", Removed);
        Assert.Contains("sws_scale_frame(m_pSwsCtx, dst, avFrame)", Removed);
        Assert.Contains("Bale D3D11 upload fault injected after %d frames.", Removed);
        Assert.Contains("bool UncompressedVideoSampleProvider::CanRepack(", Removed);
        Assert.Contains("void UncompressedVideoSampleProvider::FillPlanar(", Removed);
        foreach (var gone in new[] { "D3D11_USAGE_STAGING", "sws_scale_frame", "SwsThreads", "UploadFaultFrames", "FillPlanar", "UploadFrame(", "CanRepack", "BaleRepackExports" })
        {
            Assert.DoesNotContain(gone, Added);
        }
        // Non-zero-copy streams keep the unchanged upstream CPU output path.
        Assert.Contains("if (ZeroCopy) return ZeroCopyFrame(avFrame, surface, framePts);", Added);
        Assert.Contains("if (sws_scale(m_pSwsCtx, (const UINT8**)(avFrame->data), avFrame->linesize, 0, avFrame->height, data, linesize) > 0)", Added);
    }

    [Fact]
    public void GrainLoaderNeedsOnlyGpuExportsAndAcceptsAbi1And2()
    {
        Assert.Contains("GetProcAddress(module, \"bale_grain_cpu_apply\")", Removed);
        Assert.Contains("!api.cpuApply", Removed);
        Assert.DoesNotContain("cpu_apply", Added);
        Assert.DoesNotContain("cpuApply", Added);
        Assert.Contains("auto version = api.abiVersion ? api.abiVersion() : 0;", Added);
        Assert.Contains("if (!api.validate || !api.gpuCreate || !api.gpuApply || !api.gpuDestroy || (version != 1 && version != 2))", Added);
        Assert.Contains("api.abiVersion() != 1)", Removed);
    }

    [Fact]
    public void CpuFilmGrainIsRemovedAndGpuFailureFailsTheStream()
    {
        Assert.Contains("bool ApplyCpu(const BaleGrainParams& params, AVPixelFormat output, uint8_t** data, const int* pitch)", Removed);
        Assert.DoesNotContain("ApplyCpu", Added);
        Assert.DoesNotContain("above1080p", Added);
        Assert.Contains("else if (!zeroCopy) reason = \"GPU grain needs the zero-copy upload; keeping dav1d\";", Added);
        Assert.Contains("selected = 2;", Added);
        Assert.Contains("if (requested == 1) reason = \"CPU grain removed; using GPU\";", Added);
        Assert.Contains("const char* name = selected == 2 ? \"gpu\" : \"dav1d\";", Added);
        Assert.Contains("\"Bale film grain: GPU %s failed (hr=0x%08X); failing the stream.\\n\"", Added);
    }

    [Fact]
    public void StreamsZeroCopyCannotTakeAreRefusedAtOpen()
    {
        Assert.Contains("bool zeroCopyRoute = dav1d && !hardware && baleD3d11Upload > 0 && baleZeroCopy > 0 && !config->IsFrameGrabber;", Added);
        Assert.Contains("UncompressedVideoSampleProvider::ZeroCopyEligible(avVideoCodecCtx->pix_fmt, outputFormat, eightBitZeroCopy);", Added);
        Assert.Contains("if (!BaleZeroCopyExports::Get().create) refusal = \"bale_gpu zero-copy ABI 1 not found\";", Added);
        Assert.Contains("else if (!zeroCopyStream) refusal = \"pixel format not zero-copy eligible\";", Added);
        Assert.Contains("refusal = \"GPU film grain unavailable\";", Added);
        Assert.Contains("baleZeroCopyRefused = true;", Added);
        Assert.Contains("if (SUCCEEDED(hr) && dav1d && (baleDav1dExportFilmGrain > 0 || filmGrainMode > 0))", Added);
        // A refused default stream, or no video stream left, fails the open so the app replans.
        Assert.Contains("if (!stream && baleZeroCopyRefused)", Added);
        Assert.Contains("if (index == videoStreamIndex) hr = MF_E_UNSUPPORTED_FORMAT;", Added);
        Assert.Contains("if (zeroCopyRefused && videoStreams.empty() && SUCCEEDED(hr))", Added);
        Assert.Equal(2, Count(Added, "MF_E_UNSUPPORTED_FORMAT"));
        // The provider takes the pre-open decision only.
        Assert.Contains("uncompressed->ZeroCopy = zeroCopyStream ? 1 : 0;", Added);
        Assert.Contains("uncompressed->ZeroCopyEightBit = eightBitZeroCopy && zeroCopyStream;", Added);
    }

    [Fact]
    public void RuntimeFailuresLatchAndRaiseNotifyErrorOnce()
    {
        Assert.Contains("HRESULT UncompressedVideoSampleProvider::Fail(HRESULT hr, const char* reason)", Added);
        Assert.Contains("if (fatal.compare_exchange_strong(expected, hr))", Added);
        Assert.Contains("std::atomic<HRESULT> fatal{ S_OK };", Added);
        Assert.Contains("HRESULT FatalError() const override { return fatal.load(); }", Added);
        Assert.Contains("virtual HRESULT FatalError() const { return S_OK; }", Added);
        Assert.Contains("winrt::check_hresult(CreateZeroCopy(newDevice, newContext));", Added);
        Assert.Contains("return Fail(winrt::to_hresult(), \"upload device or zero-copy setup failed\");", Added);
        Assert.Contains("if (ZeroCopy) Fail(hr, \"upload device lost or unavailable\");", Added);
        Assert.Contains("using staging upload", Removed);
        Assert.DoesNotContain("using staging upload", Added);
        // A latched failure is never skipped as an ordinary decode error.
        Assert.Contains("if (!SUCCEEDED(hr) && SUCCEEDED(FatalError()) && errorCount++ < m_config.General().SkipErrors())", Added);

        Assert.Contains("bool FFmpegMediaSource::RaiseBaleFatal(MediaStreamSource const& sender)", Added);
        Assert.Contains("if (baleFatalRaised) return true;", Added);
        Assert.Contains("baleFatalRaised = true;", Added);
        Assert.Equal(1, Count(Added, "sender.NotifyError("));
        Assert.Contains("MediaStreamSourceErrorStatus::UnsupportedMediaFormat", Added);
        Assert.Contains("MediaStreamSourceErrorStatus::DecodeError", Added);
        Assert.Contains("if (!sample && RaiseBaleFatal(sender))", Added);
        Assert.Contains("UNREFERENCED_PARAMETER(sender);", Removed);
    }

    [Fact]
    public void PicturesOutsideZeroCopyBuffersAreDroppedAndAStreakIsFatal()
    {
        Assert.Contains("static constexpr HRESULT BaleFrameDropped = static_cast<HRESULT>(0x00042B01L);", Added);
        Assert.Contains("static constexpr HRESULT ZeroCopyDeclined = static_cast<HRESULT>(0x80042B02L);", Added);
        Assert.Contains("static constexpr HRESULT ZeroCopyMissed = static_cast<HRESULT>(0x80042B03L);", Added);
        // UncompressedSampleProvider decodes the next frame instead of failing on a dropped one.
        Assert.Contains("            if (hr == BaleFrameDropped)\n            {\n                // Bale: the frame was dropped without a sample; decode the next one.\n"
            + "                av_frame_unref(avFrame);\n                hr = S_OK;\n                continue;\n            }", Added);
        Assert.Contains("if (!buffer || !LiveZeroCopyIds().Contains(av_buffer_get_opaque(buffer))) return BaleFrameDropped;", Added);
        Assert.Contains("if (hr == BaleFrameDropped) return DropFrame(avFrame);", Added);
        Assert.Contains("if (++zeroCopyMissRun > 8) return Fail(ZeroCopyMissed, \"more than 8 consecutive frames outside zero-copy buffers\");", Added);
        Assert.Contains("zeroCopyMissRun = 0;", Added);
    }

    [Fact]
    public void EightBitFramesNeitherStageNorLand()
    {
        Assert.Contains("if (eightBit && hr == S_FALSE) return false;", Removed);
        Assert.Contains("if (eightBit && hr == S_FALSE) return ZeroCopyDeclined;", Added);
        Assert.Contains("if (hr == S_FALSE) return ZeroCopyDeclined;", Added);
        Assert.DoesNotContain("if (eightBit && hr == S_FALSE) return false;", Added);
        Assert.DoesNotContain("LogZeroCopyGrainSkipped", Added);
        Assert.Contains("return Fail(hr, hr == ZeroCopyDeclined ? \"zero-copy write declined\" :", Added);
    }

    [Fact]
    public void StatsCountDroppedFrames()
    {
        Assert.Contains("direct=%u direct_grain=%u eight_bit=%u.", Removed);
        Assert.Contains("direct=%u direct_grain=%u eight_bit=%u dropped=%u.", Added);
        Assert.Contains("zeroCopyDirectGrainFrames, zeroCopyEightBitFrames, zeroCopyDropped);", Added);
        Assert.Contains("zeroCopyDropped++;", Added);
    }

    [Fact]
    public void UploadDeviceIsInitialisedForZeroCopyStreamsEvenWithAHardwareContext()
    {
        Assert.Contains("else if (isFirstSeek && !avHardwareContext && baleD3d11Upload > 0 && !config->IsFrameGrabber)", Removed);
        Assert.Contains("if (isFirstSeek && baleD3d11Upload > 0 && !config->IsFrameGrabber && HasZeroCopyStream())", Added);
        Assert.DoesNotContain("else if (isFirstSeek", Added);
        Assert.DoesNotContain("!avHardwareContext && baleD3d11Upload", Added);
        Assert.Contains("bool FFmpegMediaSource::HasZeroCopyStream()", Added);
        Assert.Contains("if (uncompressed && uncompressed->ZeroCopy > 0) return true;", Added);
    }

    private static int Count(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}
