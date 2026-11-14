using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackResourceLifetimeTests
{
    [Fact]
    public void PlaybackProtectsResourcesUntilEveryPlayerHasStopped()
    {
        var lifetime = new PlaybackResourceLifetime();
        var oldPlayer = lifetime.EnterPlayer();
        var nextPlayer = lifetime.EnterPlayer();
        var releases = 0;
        Assert.False(lifetime.ReleaseIfIdle(() => releases++));
        oldPlayer.Dispose();
        oldPlayer.Dispose();
        Assert.False(lifetime.ReleaseIfIdle(() => releases++));
        Assert.Equal(0, releases);
        nextPlayer.Dispose();
        Assert.True(lifetime.ReleaseIfIdle(() => releases++));
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task ANewStartCannotOverlapAnIdleDeviceRelease()
    {
        var lifetime = new PlaybackResourceLifetime();
        using var releaseEntered = new ManualResetEventSlim();
        using var finishRelease = new ManualResetEventSlim();
        var release = Task.Run(() => lifetime.ReleaseIfIdle(() =>
        {
            releaseEntered.Set();
            Assert.True(finishRelease.Wait(TimeSpan.FromSeconds(10)));
        }));
        Assert.True(releaseEntered.Wait(TimeSpan.FromSeconds(10)));
        var start = Task.Run(lifetime.EnterPlayer);
        try { Assert.False(start.IsCompleted); }
        finally { finishRelease.Set(); }
        Assert.True(await release);
        using var player = await start;
        Assert.False(lifetime.ReleaseIfIdle(() => throw new InvalidOperationException()));
    }

    [Fact]
    public void FailedCleanupDoesNotLockOutPlayback()
    {
        var lifetime = new PlaybackResourceLifetime();
        Assert.Throws<IOException>(() => lifetime.ReleaseIfIdle(() => throw new IOException()));
        using (lifetime.EnterPlayer()) Assert.False(lifetime.ReleaseIfIdle(() => { }));
        Assert.True(lifetime.ReleaseIfIdle(() => { }));
    }
}
