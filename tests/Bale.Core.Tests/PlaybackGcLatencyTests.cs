using System.Runtime;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackGcLatencyTests
{
    [Theory]
    [InlineData(GCLatencyMode.Batch)]
    [InlineData(GCLatencyMode.Interactive)]
    [InlineData(GCLatencyMode.SustainedLowLatency)]
    public void FirstAcquireCapturesTheCurrentModeAndSetsSustained(GCLatencyMode original)
    {
        var mode = GCLatencyMode.Interactive;
        var changes = new List<GCLatencyMode>();
        var latency = new PlaybackGcLatency(() => mode, value => { mode = value; changes.Add(value); });
        Assert.False(latency.IsHeld);
        Assert.Equal(GCLatencyMode.Interactive, latency.OriginalMode);
        Assert.Empty(changes);
        mode = original;

        using var lease = latency.Acquire();
        Assert.True(latency.IsHeld);
        Assert.Equal(original, latency.OriginalMode);
        Assert.Equal(GCLatencyMode.SustainedLowLatency, mode);
        Assert.Equal([GCLatencyMode.SustainedLowLatency], changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingPagesKeepSustainedUntilTheLastLeaseReleases(bool releaseOldPageFirst)
    {
        var mode = GCLatencyMode.Interactive;
        var changes = new List<GCLatencyMode>();
        var latency = new PlaybackGcLatency(() => mode, value => { mode = value; changes.Add(value); });
        var oldPage = latency.Acquire();
        var newPage = latency.Acquire();
        Assert.Equal([GCLatencyMode.SustainedLowLatency], changes);
        Assert.Equal(GCLatencyMode.Interactive, latency.OriginalMode);

        (releaseOldPageFirst ? oldPage : newPage).Dispose();
        Assert.True(latency.IsHeld);
        Assert.Equal(GCLatencyMode.SustainedLowLatency, mode);
        Assert.Single(changes);

        (releaseOldPageFirst ? newPage : oldPage).Dispose();
        Assert.False(latency.IsHeld);
        Assert.Equal(GCLatencyMode.Interactive, mode);
        Assert.Equal([GCLatencyMode.SustainedLowLatency, GCLatencyMode.Interactive], changes);
    }

    [Fact]
    public void LastReleaseRestoresTheCapturedModeEvenAfterADebugOverride()
    {
        var mode = GCLatencyMode.Batch;
        var latency = new PlaybackGcLatency(() => mode, value => mode = value);
        var lease = latency.Acquire();
        mode = GCLatencyMode.Interactive;
        Assert.Equal(GCLatencyMode.Batch, latency.OriginalMode);

        lease.Dispose();
        Assert.Equal(GCLatencyMode.Batch, mode);
        Assert.Equal(GCLatencyMode.Batch, latency.OriginalMode);
        Assert.False(latency.IsHeld);
    }

    [Fact]
    public void DoubleDisposeCannotReleaseAnotherPagesLease()
    {
        var mode = GCLatencyMode.Interactive;
        var changes = new List<GCLatencyMode>();
        var latency = new PlaybackGcLatency(() => mode, value => { mode = value; changes.Add(value); });
        var oldPage = latency.Acquire();
        var newPage = latency.Acquire();

        oldPage.Dispose();
        oldPage.Dispose();
        Assert.True(latency.IsHeld);
        Assert.Equal(GCLatencyMode.SustainedLowLatency, mode);
        Assert.Single(changes);

        newPage.Dispose();
        oldPage.Dispose();
        newPage.Dispose();
        Assert.False(latency.IsHeld);
        Assert.Equal(GCLatencyMode.Interactive, mode);
        Assert.Equal([GCLatencyMode.SustainedLowLatency, GCLatencyMode.Interactive], changes);
    }

    [Fact]
    public void EachNewGroupOfLeasesCapturesTheCurrentModeAgain()
    {
        var mode = GCLatencyMode.Interactive;
        var latency = new PlaybackGcLatency(() => mode, value => mode = value);
        latency.Acquire().Dispose();
        mode = GCLatencyMode.Batch;

        var lease = latency.Acquire();
        Assert.Equal(GCLatencyMode.Batch, latency.OriginalMode);
        Assert.Equal(GCLatencyMode.SustainedLowLatency, mode);
        lease.Dispose();
        Assert.Equal(GCLatencyMode.Batch, mode);
        Assert.False(latency.IsHeld);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SetFailuresNeverEscapeAcquireOrDispose(bool failAcquire, bool failRestore)
    {
        var mode = GCLatencyMode.Interactive;
        var changes = new List<GCLatencyMode>();
        var latency = new PlaybackGcLatency(() => mode, value =>
        {
            changes.Add(value);
            if (value == GCLatencyMode.SustainedLowLatency ? failAcquire : failRestore)
                throw new InvalidOperationException("GC mode unavailable.");
            mode = value;
        });

        var lease = latency.Acquire();
        Assert.True(latency.IsHeld);
        Assert.Equal(GCLatencyMode.Interactive, latency.OriginalMode);
        lease.Dispose();
        lease.Dispose();
        Assert.False(latency.IsHeld);
        Assert.Equal([GCLatencyMode.SustainedLowLatency, GCLatencyMode.Interactive], changes);
    }

    [Fact]
    public void ConcurrentAcquiresAndRepeatedDisposalsOnlyChangeModeAtTheBoundaries()
    {
        var mode = GCLatencyMode.Interactive;
        var changes = new List<GCLatencyMode>();
        var latency = new PlaybackGcLatency(() => mode, value => { mode = value; changes.Add(value); });
        var leases = new IDisposable[64];
        Parallel.For(0, leases.Length, i => leases[i] = latency.Acquire());
        Assert.True(latency.IsHeld);
        Assert.Equal(GCLatencyMode.Interactive, latency.OriginalMode);
        Assert.Equal([GCLatencyMode.SustainedLowLatency], changes);

        Parallel.For(0, leases.Length * 4, i => leases[i % leases.Length].Dispose());
        Assert.False(latency.IsHeld);
        Assert.Equal(GCLatencyMode.Interactive, mode);
        Assert.Equal([GCLatencyMode.SustainedLowLatency, GCLatencyMode.Interactive], changes);
    }

    [Fact]
    public void PlayerOwnsOneLeaseOutsideDebugGuardsAndReleasesItInShutdownFinally()
    {
        var player = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlayerPage.cs.txt"));
        Assert.Contains("private IDisposable? _gcLatencyLease;", player[..player.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal)]);
        Assert.Equal(1, player.Split("PlaybackGcLatency.Shared.Acquire()", StringSplitOptions.None).Length - 1);
        var start = player[player.IndexOf("private async Task StartAttemptAsync()", StringComparison.Ordinal)
            ..player.IndexOf("private async Task AttachSessionAsync", StringComparison.Ordinal)];
        Assert.Contains("_gcLatencyLease ??= PlaybackGcLatency.Shared.Acquire();",
            start[..start.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal)]);
        Assert.True(start.IndexOf("_gcLatencyLease ??=", StringComparison.Ordinal)
            < start.IndexOf("await StopAttemptAsync()", StringComparison.Ordinal));
        var shutdown = player[player.IndexOf("private async Task ShutdownCoreAsync()", StringComparison.Ordinal)
            ..player.IndexOf("private async void OnSuspending", StringComparison.Ordinal)];
        Assert.DoesNotContain("#if", shutdown);
        var cleanup = shutdown[shutdown.IndexOf("finally", StringComparison.Ordinal)..];
        Assert.Contains("_gcLatencyLease?.Dispose();", cleanup);
        Assert.Contains("_gcLatencyLease = null;", cleanup);
    }
}
