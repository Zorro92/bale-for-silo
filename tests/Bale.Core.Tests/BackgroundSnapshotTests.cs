using Bale.Core.Playback;

namespace Bale.Core.Tests;

public sealed class BackgroundSnapshotTests
{
    private sealed class Box(int value)
    {
        public int Value { get; } = value;
    }

    [Fact]
    public void ValueIsTheInitialSnapshotUntilAReadCompletes()
    {
        var queued = new List<Action>();
        var reads = 0;
        var snapshot = new BackgroundSnapshot<Box>(() => new Box(++reads), new Box(0), queued.Add);
        snapshot.RequestRefresh();
        // The caller never runs the read itself: it only schedules it.
        Assert.Equal(0, snapshot.Value!.Value);
        Assert.Equal(0, reads);
        Assert.True(snapshot.Refreshing);
        Assert.Single(queued)();
        Assert.Equal(1, snapshot.Value!.Value);
        Assert.False(snapshot.Refreshing);
    }

    [Fact]
    public void AtMostOneReadIsInFlightAndRequestsMeanwhileReadOnceMore()
    {
        var queued = new List<Action>();
        var reads = 0;
        BackgroundSnapshot<Box>? snapshot = null;
        snapshot = new BackgroundSnapshot<Box>(() =>
        {
            reads++;
            // A blocked source: the UI tick keeps asking while this read is inside the wrapper.
            if (reads == 1)
                for (var i = 0; i < 10; i++) snapshot!.RequestRefresh();
            return new Box(reads);
        }, null, queued.Add);
        snapshot.RequestRefresh();
        snapshot.RequestRefresh();
        Assert.Single(queued);
        queued[0]();
        // Ten requests during the first read coalesce into one more read, on the same scheduled work item.
        Assert.Equal(2, reads);
        Assert.Single(queued);
        Assert.Equal(2, snapshot.Value!.Value);
        Assert.False(snapshot.Refreshing);
        snapshot.RequestRefresh();
        Assert.Equal(2, queued.Count);
    }

    [Fact]
    public void AFailedOrEmptyReadKeepsTheLastValue()
    {
        var queued = new List<Action>();
        var failures = new List<Exception>();
        var step = 0;
        var snapshot = new BackgroundSnapshot<Box>(() => ++step switch
        {
            1 => throw new ObjectDisposedException("source"),
            2 => null,
            _ => new Box(step),
        }, new Box(7), queued.Add, failures.Add);
        for (var i = 0; i < 3; i++)
        {
            snapshot.RequestRefresh();
            queued[^1]();
            if (i < 2) Assert.Equal(7, snapshot.Value!.Value);
        }
        Assert.IsType<ObjectDisposedException>(Assert.Single(failures));
        Assert.Equal(3, snapshot.Value!.Value);
    }

    [Fact]
    public void CloseStopsReadsAndDropsOneAlreadyRunning()
    {
        var queued = new List<Action>();
        BackgroundSnapshot<Box>? snapshot = null;
        snapshot = new BackgroundSnapshot<Box>(() =>
        {
            snapshot!.Close();
            return new Box(1);
        }, new Box(0), queued.Add);
        snapshot.RequestRefresh();
        queued[0]();
        Assert.Equal(0, snapshot.Value!.Value);
        snapshot.RequestRefresh();
        Assert.Single(queued);
    }

    [Fact]
    public void AReadBlockedOnThePoolNeverBlocksTheCaller()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var snapshot = new BackgroundSnapshot<Box>(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new Box(1);
        }, new Box(0));
        snapshot.RequestRefresh();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        // The wrapper holds its lock: the UI tick asks again and reads the old value immediately.
        snapshot.RequestRefresh();
        Assert.Equal(0, snapshot.Value!.Value);
        Assert.True(snapshot.Refreshing);
        release.Set();
        SpinWait.SpinUntil(() => !snapshot.Refreshing, TimeSpan.FromSeconds(10));
        Assert.Equal(1, snapshot.Value!.Value);
    }
}
