namespace Bale.Core.Tests;

// Source-contract checks only; Windows CI compiles the patch and the console must validate D3D/MF behavior.
public class D3d11UploadPatchTests
{
    private static readonly string Patch = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "0006-d3d11-software-upload.patch"));
    private static readonly string AddedSource = string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..]));

    [Fact]
    public void PatchChangesOnlyNativeSourceAndLeavesHardwareDecodingAndGrainAlone()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(8, paths.Length);
        Assert.All(paths, path => Assert.Matches(@"^Source/[^/]+\.(cpp|h)$", path));
        Assert.DoesNotContain("->SetHardwareDevice(", AddedSource);
        Assert.DoesNotContain("av_hwdevice_ctx_", AddedSource);
        Assert.DoesNotContain("CreateComputeShader", AddedSource);
        Assert.DoesNotContain("avcodec_open2", AddedSource);
        Assert.Contains("int baleD3d11Upload = 0;", AddedSource);
        Assert.Contains("key == \"bale_d3d11_upload\") { target = &baleD3d11Upload; minimum = 0; maximum = 1; }", AddedSource);
        Assert.Contains("isFirstSeek && !avHardwareContext && baleD3d11Upload > 0", AddedSource);
    }

    [Fact]
    public void UploadUsesWritableStagingRingAndShaderReadableDefaultTextures()
    {
        Assert.Contains("std::array<winrt::com_ptr<ID3D11Texture2D>, 3> stagingTextures", AddedSource);
        Assert.Contains("stagingDesc.Usage = D3D11_USAGE_STAGING;", AddedSource);
        Assert.Contains("stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;", AddedSource);
        Assert.Contains("D3D11_MAP_WRITE, 0, &target.mapped", AddedSource);
        Assert.Contains("desc_shared.Usage = D3D11_USAGE_DEFAULT;", AddedSource);
        Assert.Contains("desc_shared.BindFlags = D3D11_BIND_SHADER_RESOURCE;", AddedSource);
        Assert.Contains("data[0] + static_cast<size_t>(pitch) * outputFrameHeight", AddedSource);
        Assert.Contains("format == DXGI_FORMAT_P010 ? 2U : 1U", AddedSource);
        Assert.Contains("ScaleFrame(avFrame, data, linesize, buffer)", AddedSource);
        Assert.Contains("context->CopyResource(texture.get(), source.texture.get());\n        context->Flush();", AddedSource);
        Assert.Contains("MediaStreamSample::CreateFromDirect3D11Surface(directSurface, position)", AddedSource);
    }

    [Fact]
    public void SharedContextIsProtectedAndSeparateManagerHandleIsClosed()
    {
        Assert.Contains("D3D11_CREATE_DEVICE_SINGLETHREADED", AddedSource);
        Assert.Contains("try_as<ID3D10Multithread>()", AddedSource);
        Assert.Contains("multithread->SetMultithreadProtected(TRUE);", AddedSource);
        Assert.Contains("if (!multithread->GetMultithreadProtected())", AddedSource);
        Assert.Contains("uploadDeviceManager->CloseDeviceHandle(uploadDeviceHandle);", AddedSource);
        Assert.Contains("uploadDeviceHandle = nullptr;", AddedSource);
        Assert.Contains("uploadDeviceManager->TestDevice(uploadDeviceHandle)", AddedSource);
        Assert.Contains("AheadPauseScope aheadPause(this, true);\n                DisableUploadStreams(uploadHr);\n                CloseUploadDevice();", AddedSource);
    }

    [Fact]
    public void ProcessedReturnsTexturesAndFailuresPermanentlyResumeTheSameFrameOnCpu()
    {
        Assert.Contains("sample.Processed(weak_handler(this, &BaleD3D11Upload::OnProcessed))", AddedSource);
        Assert.Contains("trackedSamples.emplace(native, token);", AddedSource);
        Assert.Contains("texturePool.ReturnTexture(surface.as<ID3D11Texture2D>());", AddedSource);
        Assert.Contains("entry.first.as<MediaStreamSample>().Processed(entry.second);", AddedSource);
        Assert.Contains("trackedSamples.clear();", AddedSource);
        Assert.Contains("texturePool.Clear();", AddedSource);
        Assert.Contains("failure.store(winrt::to_hresult());", AddedSource);
        Assert.Contains("if (uploadFailed) return S_FALSE;", AddedSource);
        Assert.Contains("uploadFailed = true;", AddedSource);
        Assert.Contains("DisableUpload(winrt::to_hresult());\n            *surface = nullptr;", AddedSource);
        Assert.Contains("uploadSample = upload->CreateSample(target, ConvertPosition(framePts), surface);", AddedSource);
        Assert.Contains("uploadSample = nullptr;\n    upload.reset();", AddedSource);
    }
}
