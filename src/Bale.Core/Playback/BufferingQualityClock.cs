namespace Bale.Core.Playback;

/// <summary>
/// The clock and timers of <see cref="BufferingQualityController"/> (silo-apple PlayerPromptClock): elapsed
/// milliseconds on a monotonic clock, and one-shot timers. Injected so tests drive the clock by hand.
/// </summary>
public interface IBufferingQualityClock
{
    /// <summary>Elapsed real time in milliseconds; only differences are meaningful.</summary>
    long ElapsedMs { get; }

    /// <summary>Runs <paramref name="action"/> once after <paramref name="delayMs"/> unless the handle is disposed first.</summary>
    IDisposable Schedule(long delayMs, Action action);
}

/// <summary>
/// A clock whose timers fire when the owner polls <see cref="Fire"/> (the player's 100 ms UI tick), on the thread
/// that polls. No thread-pool timer ever calls into the controller, so it needs no locking.
/// </summary>
public sealed class PolledBufferingQualityClock(Func<long> now) : IBufferingQualityClock
{
    private readonly List<Timer> _timers = [];
    private long _order;

    public long ElapsedMs => now();

    public IDisposable Schedule(long delayMs, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var timer = new Timer(now() + Math.Max(0, delayMs), _order++, action);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Fires every due timer in deadline order, first scheduled first on a tie, including ones that firing schedules.</summary>
    public void Fire()
    {
        var current = now();
        while (Next(current) is { } next)
        {
            next.Dispose();
            next.Action();
        }
        _timers.RemoveAll(timer => timer.Cancelled);
    }

    /// <summary>Timers still waiting (tests check that the controller cancels what it no longer needs).</summary>
    public int Pending => _timers.Count(timer => !timer.Cancelled);

    /// <summary>When the earliest waiting timer is due, or null (a manual test clock steps to it).</summary>
    public long? NextDueMs => _timers.Where(timer => !timer.Cancelled).Select(timer => (long?)timer.DueMs).Min();

    private Timer? Next(long current)
    {
        Timer? next = null;
        foreach (var timer in _timers)
            if (!timer.Cancelled && timer.DueMs <= current
                && (next is null || timer.DueMs < next.DueMs || timer.DueMs == next.DueMs && timer.Order < next.Order))
                next = timer;
        return next;
    }

    private sealed class Timer(long dueMs, long order, Action action) : IDisposable
    {
        public long DueMs { get; } = dueMs;
        public long Order { get; } = order;
        public Action Action { get; } = action;
        public bool Cancelled { get; private set; }
        public void Dispose() => Cancelled = true;
    }
}
