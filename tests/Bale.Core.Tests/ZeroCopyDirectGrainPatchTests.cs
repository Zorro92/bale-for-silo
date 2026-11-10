namespace Bale.Core.Tests;

// Source-contract checks for 0018 (GPU grain fused into the zero-copy direct write), not D3D/MF execution.
public class ZeroCopyDirectGrainPatchTests
{
    private static readonly string Patch = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "0018-zero-copy-direct-grain.patch"));
    private static readonly string Added = Lines('+');
    private static readonly string Removed = Lines('-');

    private static string Lines(char sign) => string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith(sign) && !line.StartsWith(new string(sign, 3), StringComparison.Ordinal))
        .Select(line => line[1..]));

    [Fact]
    public void PatchTouchesOnlyNativeSources()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(new[]
        {
            "Source/UncompressedVideoSampleProvider.cpp",
            "Source/UncompressedVideoSampleProvider.h",
        }, paths);
    }

    [Fact]
    public void GpuGrainFramesTakeTheDirectWriteWithTheirParams()
    {
        Assert.Contains("if (api.repackInto && upload->CanWriteDirect() && !(params && FilmGrain.mode == 2))", Removed);
        Assert.Contains("auto directGrain = params && FilmGrain.mode == 2 ? params : nullptr;", Added);
        Assert.Contains("if (api.repackInto && upload->CanWriteDirect())", Added);
        Assert.Contains("hr = api.repackInto(handle, picture, &frame, directGrain, texture);", Added);
        Assert.DoesNotContain("api.repackInto(handle, picture, &frame, params", Added);
    }

    [Fact]
    public void DeclinedGrainWriteStillLandsAndIsLoggedSeparately()
    {
        // The landing fallback (with its D3D11 grain pass) and the CPU-grain early return are untouched.
        Assert.DoesNotContain("api.repack(handle", Removed);
        Assert.DoesNotContain("CreateLandedSample", Removed);
        Assert.DoesNotContain("FilmGrain.mode == 1", Removed);
        Assert.Contains("auto& declineLogged = directGrain ? zeroCopyGrainDeclineLogged : zeroCopyDeclineLogged;", Added);
        Assert.Contains("bool zeroCopyGrainDeclineLogged = false;", Added);
    }

    [Fact]
    public void StatsCountDirectGrainFrames()
    {
        Assert.Contains("if (directGrain) zeroCopyDirectGrainFrames++;", Added);
        Assert.Contains("direct=%u direct_grain=%u.", Added);
    }
}
