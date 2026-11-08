using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackMemoryReleaseTests
{
    [Fact]
    public async Task NothingQueuedCompletesImmediately()
    {
        var release = new PlaybackMemoryRelease(() => throw new InvalidOperationException("not expected"));
        Assert.True(await release.WaitAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task QueuedPassesRunInOrderOffTheCallingThread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var runs = new List<int>();
        var threads = new List<int>();
        var release = new PlaybackMemoryRelease(() =>
        {
            lock (runs)
            {
                runs.Add(runs.Count);
                threads.Add(Environment.CurrentManagedThreadId);
            }
            Thread.Sleep(20);
        });

        _ = release.Schedule();
        _ = release.Schedule();
        await release.Schedule();
        Assert.True(await release.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([0, 1, 2], runs);
        Assert.DoesNotContain(caller, threads);
    }

    [Fact]
    public async Task StartStopsWaitingAtTheLimitButThePassStillFinishes()
    {
        using var gate = new ManualResetEventSlim();
        var finished = false;
        var release = new PlaybackMemoryRelease(() => { gate.Wait(); finished = true; });
        var pass = release.Schedule();

        Assert.False(await release.WaitAsync(TimeSpan.FromMilliseconds(50)));
        Assert.False(finished);
        gate.Set();
        await pass;
        Assert.True(finished);
        Assert.True(await release.WaitAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task AFailingPassNeverFaultsTheQueue()
    {
        var calls = 0;
        var release = new PlaybackMemoryRelease(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new OutOfMemoryException("collect failed");
        });

        await release.Schedule();
        await release.Schedule();
        Assert.Equal(2, calls);
        Assert.True(await release.WaitAsync(TimeSpan.Zero));
    }

    [Fact]
    public void TheStartWaitIsBounded()
    {
        Assert.InRange(PlaybackMemoryRelease.StartWaitLimit, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void PlayerSchedulesAReleaseAfterEveryTeardownAndStartsWaitForIt()
    {
        var player = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlayerPage.cs.txt"));
        var stop = player[player.IndexOf("private async Task StopAttemptAsync()", StringComparison.Ordinal)
            ..player.IndexOf("public bool OnBack()", StringComparison.Ordinal)];
        var stopCleanup = stop[stop.LastIndexOf("finally", StringComparison.Ordinal)..];
        Assert.True(stopCleanup.IndexOf("DisposeMount(attempt);", StringComparison.Ordinal)
            < stopCleanup.IndexOf("PlaybackMemoryRelease.Shared.Schedule()", StringComparison.Ordinal));
        Assert.Equal(1, player.Split("PlaybackMemoryRelease.Shared.Schedule()", StringSplitOptions.None).Length - 1);

        var start = player[player.IndexOf("private async Task StartAttemptAsync()", StringComparison.Ordinal)
            ..player.IndexOf("private async Task AttachSessionAsync", StringComparison.Ordinal)];
        var stopCall = start.IndexOf("await StopAttemptAsync()", StringComparison.Ordinal);
        var wait = start.IndexOf("PlaybackMemoryRelease.Shared.WaitAsync(PlaybackMemoryRelease.StartWaitLimit)",
            StringComparison.Ordinal);
        Assert.True(stopCall >= 0 && stopCall < wait);
        Assert.True(wait < start.IndexOf("new Attempt(request, client)", StringComparison.Ordinal));
        Assert.True(wait < start.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal));
    }
}
