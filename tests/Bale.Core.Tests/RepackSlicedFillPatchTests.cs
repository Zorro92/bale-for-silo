namespace Bale.Core.Tests;

// Source-contract checks for 0014: the repack fill is sliced by the existing sws thread count.
public class RepackSlicedFillPatchTests
{
    private static readonly string Patch = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "0014-repack-sliced-fill.patch"));
    private static readonly string Added = string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..]));

    [Fact]
    public void PatchTouchesOnlyNativeSources()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(new[]
        {
            "Source/BaleCopyPool.h", "Source/UncompressedVideoSampleProvider.cpp", "Source/UncompressedVideoSampleProvider.h",
        }, paths);
    }

    [Fact]
    public void UsesTheSwsThreadSettingsAndAddsNoOptions()
    {
        Assert.Contains("std::make_unique<BaleCopyPool>(SwsThreads, BaleThreadBoost::ToWin32(SwsThreadPriority))", Added);
        Assert.DoesNotContain("key == \"bale_", Added);
    }

    [Fact]
    public void SlicesCoverEveryRowOfAllThreePlanes()
    {
        Assert.Contains("int begin = height * slice / slices;", Added);
        Assert.Contains("int end = height * (slice + 1) / slices;", Added);
        Assert.Contains("copyRows(data[0], avFrame->data[0], avFrame->linesize[0], outputWidth * bytes, outputHeight);", Added);
        Assert.Contains("copyRows(data[1], avFrame->data[1], avFrame->linesize[1], chromaWidth * bytes, chromaHeight);", Added);
        Assert.Contains("copyRows(vData, avFrame->data[2], avFrame->linesize[2], chromaWidth * bytes, chromaHeight);", Added);
    }

    [Fact]
    public void PoolJoinsWorkersAndShrinksWhenAThreadCannotStart()
    {
        Assert.Contains("for (auto& worker : workers) worker.join();", Added);
        Assert.Contains("threads = static_cast<int>(workers.size()) + 1;", Added);
        Assert.Contains("done.wait(lock, [this] { return pending == 0; });", Added);
    }
}
