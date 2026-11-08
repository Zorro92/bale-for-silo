using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugSampleMemoryTests
{
    [Theory]
    [InlineData(0L, 0L, null)]
    [InlineData(1L, 0L, "GPU texture (1/1 checked)")]
    [InlineData(42L, 0L, "GPU texture (42/42 checked)")]
    [InlineData(0L, 1L, "CPU buffer (1/1 checked)")]
    [InlineData(0L, 42L, "CPU buffer (42/42 checked)")]
    [InlineData(10L, 32L, "mixed: texture 10 · buffer 32")]
    [InlineData(42L, 1L, "mixed: texture 42 · buffer 1")]
    [InlineData(3_000_000_000L, 0L, "GPU texture (3000000000/3000000000 checked)")]
    public void DescribesCheckedCountsWithoutClaimingUncheckedFrames(long textures, long buffers, string? expected)
    {
        Assert.Equal(expected, DebugSampleMemory.Describe(textures, buffers));
    }

    [Fact]
    public void ChecksOnlyTheFirstAndEvery24thVideoSample()
    {
        Assert.False(DebugSampleMemory.ShouldCheck(0));
        var checkedSamples = Enumerable.Range(1, 73).Select(sample => (long)sample).Where(DebugSampleMemory.ShouldCheck);
        Assert.Equal([1L, 24L, 48L, 72L], checkedSamples);
    }

    [Fact]
    public void ManagedObserverResetsAtomicCountersOnStartingAndDisposesOnlySampledVideoSurfaces()
    {
        var observer = Read("DebugFrameDelivery.cs.txt");
        var starting = observer[observer.IndexOf("private void OnStarting(", StringComparison.Ordinal)
            ..observer.IndexOf("private void OnSampleRequested(", StringComparison.Ordinal)];
        Assert.Contains("lock (_timelineGate)", starting);
        foreach (var counter in new[] { "_memorySamples", "_textureSamples", "_bufferSamples" })
            Assert.Contains($"Interlocked.Exchange(ref {counter}, 0)", starting);
        Assert.Contains("Volatile.Read(ref _textureSamples), Volatile.Read(ref _bufferSamples)", observer);

        var videoCheck = observer.IndexOf("!IsVideo(args.Request.StreamDescriptor)", StringComparison.Ordinal);
        var classificationStart = observer.IndexOf("if (DebugSampleMemory.ShouldCheck(Interlocked.Increment(ref _memorySamples)))", StringComparison.Ordinal);
        Assert.True(videoCheck >= 0 && videoCheck < classificationStart);
        var classification = observer[classificationStart..observer.IndexOf("var nowMilliseconds", classificationStart, StringComparison.Ordinal)];
        Assert.Contains("try", classification);
        Assert.Contains("using var surface = sample.Direct3D11Surface;", classification);
        Assert.Contains("if (surface is not null) Interlocked.Increment(ref _textureSamples);", classification);
        Assert.Contains("else Interlocked.Increment(ref _bufferSamples);", classification);
        Assert.Contains("catch { }", classification); // Optional metadata failure must not disable timing or escape into FFmpeg.
    }

    [Fact]
    public void StatsUseTheManagedReadoutOrExplicitUnavailableTextOnlyInDebugBuilds()
    {
        Assert.Contains("string? SampleMemory { get; }", Read("IDebugFrameDelivery.cs.txt"));
        Assert.Contains("public string? SampleMemory => null;", Read("NativeFrameDelivery.cs.txt"));
        var stats = Read("PlayerPage.Stats.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", stats);
        Assert.Contains("(\"Sample memory\", media?.FrameDelivery?.SampleMemory ?? DebugSampleMemory.Unavailable)", stats);
        Assert.Equal("unavailable (use frame-delivery managed)", DebugSampleMemory.Unavailable);
        Assert.Contains("StatsRowFilter.DescribeStats(player.DebugStatsRows)", Read("DebugServer.Stats.cs.txt"));
    }

    [Fact]
    public void StatsCommandIncludesTheRowEvenWhenHiddenAndRecognizesItBeforePlayback()
    {
        var filter = new DebugStatsRowFilter();
        Assert.True(filter.TryApply("hide", "Sample memory", [], out var unknown));
        Assert.Empty(unknown);
        Assert.Equal("Sample memory: GPU texture (42/42 checked) [hidden]\n",
            filter.DescribeStats([("Sample memory", DebugSampleMemory.Describe(42, 0)!)]));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
