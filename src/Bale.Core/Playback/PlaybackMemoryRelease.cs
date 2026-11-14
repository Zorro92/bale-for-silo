using System.Runtime;

namespace Bale.Core.Playback;

/// <summary>
/// Collects unreachable WinRT/COM wrappers after explicit player/source/device cleanup. The small
/// managed heap may otherwise keep wrapper finalizers from running for minutes. This is a backstop:
/// native owners must be closed and driver caches trimmed explicitly; GC cannot release live devices.
/// </summary>
public sealed class PlaybackMemoryRelease
{
    public static PlaybackMemoryRelease Shared { get; } = new(CollectNow);

    /// <summary>Upper bound a start waits for a pending release; a slow pass must never block playback.</summary>
    public static readonly TimeSpan StartWaitLimit = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly Action _collect;
    private Task _pending = Task.CompletedTask;

    internal PlaybackMemoryRelease(Action collect) => _collect = collect;

    /// <summary>Queues a release pass on the thread pool, after any pass already queued.</summary>
    public Task Schedule()
    {
        lock (_gate)
        {
            return _pending = _pending.ContinueWith(_ => Run(), CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        }
    }

    /// <summary>Waits for queued passes, up to <paramref name="limit"/>. True when they finished in time.</summary>
    public async Task<bool> WaitAsync(TimeSpan limit)
    {
        Task pending;
        lock (_gate) pending = _pending;
        if (pending.IsCompleted) return true;
        return await Task.WhenAny(pending, Task.Delay(limit)).ConfigureAwait(false) == pending;
    }

    private void Run()
    {
        try { _collect(); }
        catch (Exception)
        {
            // Releasing memory early is an optimization; it must never fault teardown or the next start.
        }
    }

    private static void CollectNow()
    {
        // Twice: the first pass queues wrapper finalizers, which can release objects holding further wrappers.
        for (var pass = 0; pass < 2; pass++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
        }
    }
}
