using System.Runtime.CompilerServices;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class UiResidencyTests
{
    [Theory]
    [InlineData(-1000, 0)]
    [InlineData(100, 0)]
    [InlineData(750, 500)]
    [InlineData(10000, 3000)]
    public void CollectionWaitRespectsTheRemainingSuspendDeadline(int deadlineMs, int expectedMs)
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), UiResidencyState.CollectionWait(
            now.AddMilliseconds(deadlineMs), now, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void EnteredBackgroundWithoutAnOsDeadlineKeepsTheExistingBound()
        => Assert.Equal(TimeSpan.FromSeconds(3), UiResidencyState.CollectionWait(null,
            DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(3)));

    private sealed class Resource(UiResourceCounts counts) : IUiResource
    {
        private readonly UiResourceCounts _counts = counts;
        public UiResourceCounts Resident { get; private set; } = counts;
        public int Restores { get; private set; }
        public UiResourceCounts Release()
        {
            var released = Resident;
            Resident = default;
            return released;
        }
        public UiResourceCounts Restore()
        {
            if (Resident != default) return default;
            Restores++;
            return Resident = _counts;
        }
    }

    [Fact]
    public void BackgroundAndSuspendAreIdempotentAndResumeDoesNotDecodeHiddenPages()
    {
        var state = new UiResidencyState();
        var visible = new Resource(new(Images: 12, Surfaces: 2, Shadows: 3));
        var cached = new Resource(new(Images: 200, Surfaces: 2));
        state.Register(visible);
        state.Register(cached);
        Assert.Equal(new(Images: 212, Surfaces: 4, Shadows: 3), state.Release());
        var generation = state.Generation;
        Assert.Equal(state.LastReleased, state.Release());
        Assert.Equal(generation, state.Generation);
        Assert.Equal(1, state.Releases);
        Assert.Equal(default, state.Resident);
        Assert.Equal(default, state.Restore(visible));

        Assert.True(state.Resume());
        Assert.False(state.Resume());
        Assert.Equal(default, state.Resident); // no eager replay of the entire Frame cache
        Assert.Equal(new(Images: 12, Surfaces: 2, Shadows: 3), state.Restore(visible));
        Assert.Equal(default, cached.Resident);
        Assert.Equal(0, cached.Restores);
        Assert.Equal(default, state.Restore(visible));
        Assert.Equal(1, visible.Restores);
        state.Restore(cached); // rail scroll / Back reveals it later
        Assert.Equal(new(Images: 212, Surfaces: 4, Shadows: 3), state.Resident);
        GC.KeepAlive(visible);
        GC.KeepAlive(cached);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlayerAndBackgroundGatesMustBothOpenBeforeArtReturns(bool backgroundFirst)
    {
        var first = backgroundFirst ? UiResidencyReason.Background : UiResidencyReason.Playback;
        var second = backgroundFirst ? UiResidencyReason.Playback : UiResidencyReason.Background;
        var state = new UiResidencyState();
        var art = new Resource(new(Images: 36, Surfaces: 2));
        state.Register(art);
        state.Release(first);
        state.Release(second);
        Assert.Equal(new(Images: 36, Surfaces: 2), state.LastReleased);
        Assert.True(state.Resume(first));
        Assert.Equal(default, state.Restore(art));
        Assert.Equal(default, state.Resident);
        Assert.True(state.Resume(second));
        state.Restore(art);
        Assert.False(state.IsSuppressed);
        Assert.Equal(1, art.Restores);
    }

    [Fact]
    public void LateRegistrationWhileSuppressedIsReleasedAndOnlyRestoredOnDemand()
    {
        var state = new UiResidencyState();
        state.Release(UiResidencyReason.Playback);
        var late = new Resource(new(Images: 1));
        state.Register(late);
        Assert.Equal(default, late.Resident);
        Assert.Equal(new(Images: 1), state.LastReleased);
        Assert.Equal(default, state.Restore(late));
        state.Resume(UiResidencyReason.Playback);
        Assert.Equal(0, late.Restores);
        state.Restore(late);
        Assert.Equal(1, late.Restores);
    }

    [Fact]
    public void AQuickReturnInvalidatesCleanupAndEachNewBackgroundHasItsOwnGeneration()
    {
        var state = new UiResidencyState();
        state.Release();
        var shutdownGeneration = state.Generation;
        state.Resume();
        Assert.NotEqual(shutdownGeneration, state.Generation);
        state.Release();
        Assert.True(state.IsBackground);
        Assert.NotEqual(shutdownGeneration, state.Generation);
        Assert.Equal(2, state.Releases);
    }

    [Fact]
    public void TrackerDoesNotRootPagesOrBitmaps()
    {
        var state = new UiResidencyState();
        var weak = RegisterUnowned(state);
        for (var pass = 0; pass < 2; pass++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.TryGetTarget(out _));
        Assert.Equal(default, state.Release());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<IUiResource> RegisterUnowned(UiResidencyState state)
    {
        var resource = new Resource(new(Images: 48));
        state.Register(resource);
        return new(resource);
    }

    private sealed class Broken : IUiResource
    {
        public UiResourceCounts Resident => default;
        public UiResourceCounts Release() => throw new InvalidOperationException();
        public UiResourceCounts Restore() => throw new InvalidOperationException();
    }

    [Fact]
    public void OneFailedOwnerDoesNotPreventOtherCleanupOrRestore()
    {
        var failures = 0;
        var state = new UiResidencyState(_ => failures++);
        var broken = new Broken();
        var good = new Resource(new(Surfaces: 3));
        state.Register(broken);
        state.Register(good);
        Assert.Equal(new(Surfaces: 3), state.Release());
        state.Resume();
        state.Restore(broken);
        state.Restore(good);
        Assert.Equal(2, failures);
        Assert.Equal(new(Surfaces: 3), state.Resident);
        GC.KeepAlive(broken);
    }
}
