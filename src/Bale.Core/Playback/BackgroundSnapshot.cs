namespace Bale.Core.Playback;

/// <summary>
/// A value read off the caller's thread: <see cref="RequestRefresh"/> schedules at most one read at a time and
/// never waits for it; a request while a read runs makes that read run once more when it finishes. The UI thread
/// reads <see cref="Value"/>, the last completed read (or the initial value), and never blocks.
/// </summary>
/// <remarks>
/// For FFmpegInteropX's stream info: its getters take the source lock, which the sample thread can hold while it
/// waits on a decoder (the fork's bale/README.md, "rav1g prewarm and threading notes"). A read that blocks holds one
/// thread-pool thread, never the UI thread, and later requests only mark it dirty.
/// </remarks>
public sealed class BackgroundSnapshot<T> where T : class
{
    private const int Idle = 0;
    private const int Running = 1;
    private const int RunningDirty = 2;

    private readonly Func<T?> _read;
    private readonly Action<Action> _schedule;
    private readonly Action<Exception>? _failed;
    private T? _value;
    private int _state;
    private volatile bool _closed;

    /// <param name="read">Reads the value; null or a throw keeps the previous value.</param>
    /// <param name="initial">The value until the first read completes (taken at open, before playback).</param>
    /// <param name="schedule">Runs a read off the caller's thread; the thread pool by default (tests run it inline).</param>
    /// <param name="failed">Told about a read that threw; called on the reading thread.</param>
    public BackgroundSnapshot(Func<T?> read, T? initial, Action<Action>? schedule = null, Action<Exception>? failed = null)
    {
        _read = read;
        _value = initial;
        _schedule = schedule ?? (work => ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state!)(), work));
        _failed = failed;
    }

    /// <summary>The last completed read, or the initial value.</summary>
    public T? Value => Volatile.Read(ref _value);

    /// <summary>A read is scheduled or running (it may be blocked inside the source).</summary>
    public bool Refreshing => Volatile.Read(ref _state) != Idle;

    /// <summary>Asks for a fresh read without waiting for it. Does nothing after <see cref="Close"/>.</summary>
    public void RequestRefresh()
    {
        if (_closed) return;
        while (true)
        {
            switch (Volatile.Read(ref _state))
            {
                case Idle:
                    if (Interlocked.CompareExchange(ref _state, Running, Idle) != Idle) continue;
                    try { _schedule(Run); }
                    catch (Exception ex)
                    {
                        Volatile.Write(ref _state, Idle);
                        _failed?.Invoke(ex);
                    }
                    return;
                case Running:
                    if (Interlocked.CompareExchange(ref _state, RunningDirty, Running) != Running) continue;
                    return;
                default:
                    return;
            }
        }
    }

    /// <summary>Stops further reads; a read already running finishes but no longer replaces the value.</summary>
    public void Close() => _closed = true;

    private void Run()
    {
        while (true)
        {
            if (!_closed)
            {
                try
                {
                    if (_read() is { } value && !_closed) Volatile.Write(ref _value, value);
                }
                catch (Exception ex)
                {
                    try { _failed?.Invoke(ex); } catch { }
                }
            }
            // Running → Idle ends; RunningDirty → Running reads once more for the request that came in meanwhile.
            if (Interlocked.CompareExchange(ref _state, Idle, Running) == Running) return;
            Volatile.Write(ref _state, Running);
        }
    }
}
