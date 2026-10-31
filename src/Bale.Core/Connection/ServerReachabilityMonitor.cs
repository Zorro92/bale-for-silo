namespace Bale.Core.Connection;

public enum ServerReachabilityStatus { Unknown, Reachable, Unreachable }

public sealed record ServerReachabilityState(ServerReachabilityStatus Status = ServerReachabilityStatus.Unknown,
    DateTimeOffset? LastCheckedAt = null)
{
    public bool CanUseServer => Status != ServerReachabilityStatus.Unreachable;
}

/// <summary>
/// ServerReachabilityMonitor.kt: foreground-only fresh discovery, with a faster offline retry cadence.
/// Each instance belongs to one configured server; disposing it invalidates every pending result.
/// </summary>
public sealed class ServerReachabilityMonitor : IDisposable
{
    public static readonly TimeSpan ReachableProbeInterval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan UnreachableProbeInterval = TimeSpan.FromSeconds(8);

    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;
    private readonly IDisposable? _ownedProbe;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _foreground;
    private ServerReachabilityState _state = new();
    private long _epoch;
    private long _run;
    private bool _disposed;

    public ServerReachabilityMonitor(Uri server, HttpMessageHandler? transport = null, TimeProvider? time = null)
        : this(new ServerDiscoveryProbe(server, transport), time) { }

    private ServerReachabilityMonitor(ServerDiscoveryProbe probe, TimeProvider? time)
        : this(probe.ProbeAsync, time) => _ownedProbe = probe;

    internal ServerReachabilityMonitor(Func<CancellationToken, Task<bool>> probe, TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _probe = probe;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((interval, token) => Task.Delay(interval, _time, token));
    }

    public ServerReachabilityState State { get { lock (_sync) return _state; } }

    /// <summary>May be raised off-thread. Read State after dispatching to the UI thread.</summary>
    public event EventHandler? StateChanged;

    public void StartForeground()
    {
        CancellationToken token;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_foreground is not null) return;
            _epoch++;
            _foreground = new CancellationTokenSource();
            token = _foreground.Token;
        }
        _ = PollAsync(token);
    }

    public void StopForeground()
    {
        CancellationTokenSource? foreground;
        lock (_sync)
        {
            _epoch++;
            _run++;
            foreground = _foreground;
            _foreground = null;
            _state = new();
        }
        foreground?.Cancel();
        foreground?.Dispose();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Newest retry wins; a late response after Stop or server replacement cannot restore old state.</summary>
    public async Task<ServerReachabilityState> RetryNowAsync(CancellationToken cancellationToken = default)
    {
        long epoch, run;
        CancellationToken lifetime;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            epoch = _epoch;
            run = ++_run;
            lifetime = _lifetime.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        await _probeLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (epoch != _epoch || run != _run) return _state;
            }
            var reachable = await _probe(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            ServerReachabilityState next;
            lock (_sync)
            {
                if (epoch != _epoch || run != _run) return _state;
                next = _state = new(reachable ? ServerReachabilityStatus.Reachable : ServerReachabilityStatus.Unreachable,
                    _time.GetUtcNow());
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            return next;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = await RetryNowAsync(cancellationToken).ConfigureAwait(false);
                await _delay(state.Status == ServerReachabilityStatus.Unreachable ? UnreachableProbeInterval : ReachableProbeInterval,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            // Stop/disposal is not an offline verdict.
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Disposal may win the race between the delay and the next retry.
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        StopForeground();
        _lifetime.Cancel();
        _ownedProbe?.Dispose();
        // Pending probes still release the semaphore. Neither lock needs a native wait handle.
    }
}
