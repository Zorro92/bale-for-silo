namespace Bale.Core.Tests;

// Source-contract checks for 0016 (debug upload variant 6), not D3D/MF execution.
public class SharedUavUploadVariantPatchTests
{
    private static readonly string Patch = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "0016-upload-variant-shared-uav.patch"));
    private static readonly string Added = string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..]));

    [Fact]
    public void PatchTouchesOnlyNativeSources()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(new[] { "Source/BaleD3D11Upload.h", "Source/BaleFilmGrain.h", "Source/FFmpegMediaSource.cpp" }, paths);
    }

    [Fact]
    public void VariantSixIsVariantOnePlusUavAndAnNtSharedHandle()
    {
        Assert.Contains("else if (key == \"bale_d3d11_upload\") { target = &baleD3d11Upload; minimum = 0; maximum = 6; }", Added);
        Assert.Contains("bindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;", Added);
        Assert.Contains("miscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED;", Added);
    }

    [Fact]
    public void GpuGrainAcceptsBothRenderTargetVariants()
    {
        Assert.Contains("if (selected == 2 && uploadVariant != 1 && uploadVariant != 6)", Added);
    }

    [Fact]
    public void ProductionStillUsesVariantOne()
    {
        Assert.Equal(1, Bale.Core.Playback.SoftwareDecodeTuning.DefaultD3d11Upload);
    }
}
