using System.Text.RegularExpressions;

namespace Bale.Core.Tests;

// Source contracts only: Windows CI compiles the full series; MF acceptance needs console testing.
public class D3d11UploadVariantPatchTests
{
    private static readonly string Patch = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "0007-d3d11-upload-variants-diagnostics.patch"));
    private static readonly string AddedSource = string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..]));

    [Fact]
    public void VariantPatchChangesOnlyNativeSourceAndCarriesTheValidatedModeToThePool()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(6, paths.Length);
        Assert.All(paths, path => Assert.Matches(@"^Source/[^/]+\.(cpp|h)$", path));
        Assert.Contains("key == \"bale_d3d11_upload\") { target = &baleD3d11Upload; minimum = 0; maximum = 5; }", AddedSource);
        Assert.Contains("uncompressed->SetUploadDevice(uploadDevice, uploadContext, baleD3d11Upload)", AddedSource);
        Assert.Contains("std::make_shared<BaleD3D11Upload>(newDevice, newContext, variant)", AddedSource);
        Assert.Contains("stagingDesc.Format, bindFlags, miscFlags, texture)", AddedSource);
        Assert.DoesNotContain("avcodec_open2", AddedSource);
        Assert.DoesNotContain("av_hwdevice_ctx_", AddedSource);
    }

    [Fact]
    public void VariantsChangeOnlyDefaultTextureFlagsAndRejectFailedAllocationsThroughTheExistingFallback()
    {
        Assert.Contains("UINT bindFlags = D3D11_BIND_SHADER_RESOURCE;", AddedSource);
        Assert.Contains("UINT miscFlags = 0;", AddedSource);
        Assert.Contains("case 2: bindFlags = D3D11_BIND_DECODER | D3D11_BIND_SHADER_RESOURCE; break;", AddedSource);
        Assert.Contains("case 3: bindFlags = D3D11_BIND_DECODER; break;", AddedSource);
        Assert.Contains("case 4: bindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE; break;", AddedSource);
        Assert.Contains("case 5: miscFlags = D3D11_RESOURCE_MISC_SHARED; break;", AddedSource);
        Assert.Contains("bindFlags != desc_shared.BindFlags || miscFlags != desc_shared.MiscFlags", AddedSource);
        Assert.Contains("desc_shared.BindFlags = bindFlags;", AddedSource);
        Assert.Contains("desc_shared.MiscFlags = miscFlags;", AddedSource);
        Assert.Contains("auto hr = device->CreateTexture2D(&desc_shared, nullptr, result.put());", AddedSource);
        Assert.Contains("return hr;", AddedSource);
        Assert.Contains("static_cast<unsigned>(winrt::to_hresult()));\n            throw;", AddedSource);
    }

    [Fact]
    public void DiagnosticsAreSafePrefixedAndBoundedAtActualMssHandoffsAndProcessedCallbacks()
    {
        var formats = Regex.Matches(AddedSource, "av_log\\(nullptr, AV_LOG_(?:INFO|WARNING), \"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.NotEmpty(formats);
        Assert.All(formats, format =>
        {
            Assert.StartsWith("Bale D3D11 upload", format);
            Assert.DoesNotContain("://", format);
            Assert.DoesNotContain("%ls", format);
        });
        foreach (var text in new[]
        {
            "device acquired: flags=0x%X feature_level=0x%X multithread_protected=%u variant=%d",
            "output declared: %dx%d subtype=%s", "pool desc: %ux%u format=%u bind=0x%X misc=0x%X",
            "sample issued index=%u position_ms=%lld texture=%dx%d", "Processed index=%u position_ms=%lld",
            "CreateTexture2D STAGING failed (hr=0x%08X)", "CreateTexture2D DEFAULT failed (hr=0x%08X)",
            "Map failed (hr=0x%08X)", "OnProcessed failed (hr=0x%08X)", "OnSampleRequested failed (hr=0x%08X)",
            "operation = \"CopyResource/GetDeviceRemovedReason\"", "operation = \"CreateFromDirect3D11Surface\"",
        }) Assert.Contains(text, AddedSource);
        Assert.Contains("if (processedSamples < 3)", AddedSource);
        Assert.Contains("baleD3d11Upload > 0 && uploadSamplesIssued < 3 && sample", AddedSource);
        Assert.True(Patch.IndexOf("args.Request().Sample(sample);", StringComparison.Ordinal)
            < Patch.IndexOf("Bale D3D11 upload sample issued", StringComparison.Ordinal));
        Assert.Contains("failure.store(hr);", AddedSource);
    }
}
