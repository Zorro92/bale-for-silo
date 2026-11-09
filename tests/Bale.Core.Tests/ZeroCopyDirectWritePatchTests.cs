namespace Bale.Core.Tests;

// Source-contract checks for 0017 (zero-copy direct write), not D3D/MF execution.
public class ZeroCopyDirectWritePatchTests
{
    private static readonly string Patch = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "0017-zero-copy-direct-write.patch"));
    private static readonly string Added = string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..]));

    [Fact]
    public void PatchTouchesOnlyNativeSources()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(new[]
        {
            "Source/BaleD3D11Upload.h",
            "Source/UncompressedVideoSampleProvider.cpp",
            "Source/UncompressedVideoSampleProvider.h",
        }, paths);
    }

    [Fact]
    public void DirectWriteExportIsOptional()
    {
        Assert.Contains("GetProcAddress(module, \"bale_zc_repack_into\")", Added);
        // The required-export check is unchanged, so an older bale_gpu still loads (landing only).
        Assert.DoesNotContain("!api.repackInto", Added);
    }

    [Fact]
    public void DirectWriteNeedsSharedUavPoolTexturesAndNoGpuGrain()
    {
        Assert.Contains("return (bindFlags & D3D11_BIND_UNORDERED_ACCESS) && (miscFlags & D3D11_RESOURCE_MISC_SHARED_NTHANDLE);", Added);
        Assert.Contains("if (api.repackInto && upload->CanWriteDirect() && !(params && FilmGrain.mode == 2))", Added);
        Assert.Contains("hr = api.repackInto(handle, picture, &frame, nullptr, texture);", Added);
    }

    [Fact]
    public void DeclinedWriteReturnsTheTextureAndFallsBackToLanding()
    {
        Assert.Contains("texturePool.ReturnUploadTexture(std::move(texture));", Added);
        Assert.Contains("if (hr == S_FALSE) hr = api.repack(handle, picture, &frame, &landing);", Added);
    }

    [Fact]
    public void FramePropertiesAreReadOnlyAfterASuccessfulWrite()
    {
        var write = Added.IndexOf("hr = api.repackInto(", StringComparison.Ordinal);
        var check = Added.IndexOf("if (hr != S_OK) return false;", write, StringComparison.Ordinal);
        var read = Added.IndexOf("ReadFrameProperties(avFrame, framePts);", write, StringComparison.Ordinal);
        Assert.True(write >= 0 && check > write && read > check);
    }
}
