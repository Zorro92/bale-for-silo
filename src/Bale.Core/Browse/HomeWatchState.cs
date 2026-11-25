using System.Threading.Channels;

namespace Bale.Core.Browse;

/// <summary>
/// Home follows watch state from other devices while it is on screen: the server's v2 events socket on the
/// <c>user_state</c> channel, as silo-apple's <c>HomeWatchStateRealtime.swift</c> (<c>0414ff74</c>) and silo-roku's
/// <c>HomeWatchState.bs</c> (<c>c1841ae</c>) hold it. <see cref="HomeEventsMachine"/> (Rust) decides; this performs its
/// actions (mint, connect, subscribe, close), feeds it the answers, frames and timer ticks one at a time, and calls
/// <c>refresh</c> whenever Home should be read again: on each (re)subscription, on a change for the profile, and every
/// 120 s while no subscription is live. Start it when Home shows; dispose it when Home hides, the app suspends or the
/// profile changes, which ends the socket at once. <c>refresh</c> and <c>log</c> run on the thread pool; the notes are
/// fixed diagnostics, never ids, URLs or tickets.
/// </summary>
public sealed class HomeWatchState : IDisposable
{
    private readonly IHomeEventsTransport _transport;
    private readonly string _profileId;
    private readonly Action _refresh;
    private readonly Action<string>? _log;
    private readonly HomeEventsPolicy? _policy;
    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly HomeEventsMachine _machine = new();
    private readonly Channel<Func<long, HomeEventsOutcome>> _inputs =
        Channel.CreateUnbounded<Func<long, HomeEventsOutcome>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private Attempt? _attempt;
    private ITimer? _timer;
    private int _started;
    private int _disposed;

    /// <summary>Home's subscription as <paramref name="profileId"/> on <paramref name="client"/>'s server.</summary>
    public HomeWatchState(SiloClient client, string profileId, Action refresh, Action<string>? log = null)
        : this(new SiloHomeEventsTransport(client, profileId), profileId, refresh, log, null, TimeProvider.System)
    {
    }

    internal HomeWatchState(IHomeEventsTransport transport, string profileId, Action refresh, Action<string>? log,
        HomeEventsPolicy? policy, TimeProvider time)
    {
        _transport = transport;
        _profileId = profileId;
        _refresh = refresh;
        _log = log;
        _policy = policy;
        _time = time;
        _origin = time.GetTimestamp();
    }

    /// <summary>Mints the first ticket now. Once only; nothing after <see cref="Dispose"/>.</summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(RunAsync);
    }

    /// <summary>Ends the subscription: no further refresh, and the socket closes (a going-away close frame, best effort).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _inputs.Writer.TryComplete();
        _stop.Cancel();
        // Started, the loop releases everything on its way out.
        if (Interlocked.Exchange(ref _started, 1) == 0) _machine.Dispose();
    }

    private long Now => (long)_time.GetElapsedTime(_origin).TotalMilliseconds;

    private async Task RunAsync()
    {
        try
        {
            if (_stop.IsCancellationRequested) return;
            Apply(_machine.Start(Now, _profileId, _policy));
            while (await _inputs.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (!_stop.IsCancellationRequested && _inputs.Reader.TryRead(out var input)) Apply(input(Now));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            // A fault here must not take Home with it: Home keeps its own reads.
            Log($"stopped after an error ({error.GetType().Name})");
        }
        finally
        {
            _inputs.Writer.TryComplete();
            _timer?.Dispose();
            _attempt?.End();
            _attempt = null;
            _machine.Dispose();
        }
    }

    private void Post(Func<long, HomeEventsOutcome> input) => _inputs.Writer.TryWrite(input);

    private void Apply(HomeEventsOutcome outcome)
    {
        foreach (var note in outcome.Notes) Log(note);
        foreach (var action in outcome.Actions)
        {
            switch (action.Kind)
            {
                case HomeEventsActionKind.Mint:
                    // One attempt at a time: a new one ends whatever the last one still holds.
                    _attempt?.End();
                    var attempt = _attempt = new Attempt(action.Generation);
                    _ = MintAsync(attempt);
                    break;
                case HomeEventsActionKind.Connect when _attempt?.Generation == action.Generation:
                    _ = OpenAsync(_attempt, action.Path!, action.Protocols!);
                    break;
                case HomeEventsActionKind.Send when _attempt?.Generation == action.Generation:
                    _ = SendAsync(_attempt, action.Text!);
                    break;
                case HomeEventsActionKind.Close when _attempt?.Generation == action.Generation:
                    _attempt.End();
                    _attempt = null;
                    break;
                case HomeEventsActionKind.Refresh:
                    Refresh();
                    break;
            }
        }
        Arm(outcome.NextDeadlineMs);
    }

    private async Task MintAsync(Attempt attempt)
    {
        var generation = attempt.Generation;
        HomeEventsMint answer;
        try
        {
            answer = await _transport.MintAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (attempt.Ended) return;
            answer = new(null);
        }
        if (attempt.Ended) return;
        if (answer.Ticket is { } ticket)
        {
            Post(now => _machine.MintSucceeded(now, generation, ticket.Ticket, ticket.ExpiresIn, ticket.MaxConnectionSeconds,
                ticket.Protocol));
        }
        else
        {
            Post(now => _machine.MintFailed(now, generation, answer.Status, answer.RetryAfterMs));
        }
    }

    private async Task OpenAsync(Attempt attempt, string path, IReadOnlyList<string> protocols)
    {
        var generation = attempt.Generation;
        IHomeEventsConnection connection;
        try
        {
            connection = await _transport.ConnectAsync(path, protocols, attempt.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (attempt.Ended) return;
            var status = (error as HomeEventsConnectException)?.Status;
            Post(now => _machine.ConnectFailed(now, generation, status));
            return;
        }
        if (!attempt.Adopt(connection))
        {
            await connection.CloseAsync().ConfigureAwait(false);
            return;
        }
        var protocol = connection.SubProtocol;
        Post(now => _machine.Opened(now, generation, protocol));
        try
        {
            while (true)
            {
                var text = await connection.ReceiveAsync(attempt.Token).ConfigureAwait(false);
                if (attempt.Ended) return;
                if (text is null)
                {
                    var code = connection.CloseCode;
                    Post(now => _machine.Closed(now, generation, code));
                    return;
                }
                Post(now => _machine.Frame(now, generation, text));
            }
        }
        catch (Exception)
        {
            if (!attempt.Ended) Post(now => _machine.Closed(now, generation, null));
        }
    }

    private static async Task SendAsync(Attempt attempt, string text)
    {
        try
        {
            if (attempt.Connection is { } connection) await connection.SendAsync(text, attempt.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The receive loop sees the socket fail and reports the end.
        }
    }

    private void Arm(long? deadline)
    {
        if (deadline is not { } due)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        _timer ??= _time.CreateTimer(static state =>
        {
            var watch = (HomeWatchState)state!;
            watch.Post(now => watch._machine.Tick(now));
        }, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(TimeSpan.FromMilliseconds(Math.Max(0, due - Now)), Timeout.InfiniteTimeSpan);
    }

    private void Refresh()
    {
        try
        {
            _refresh();
        }
        catch (Exception error)
        {
            Log($"refresh failed ({error.GetType().Name})");
        }
    }

    private void Log(string message)
    {
        try
        {
            _log?.Invoke(message);
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
    }

    /// <summary>One attempt: its ticket request, then its socket. Ending it cancels the one and closes the other.</summary>
    private sealed class Attempt(uint generation)
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly Lock _gate = new();
        private IHomeEventsConnection? _connection;
        private bool _ended;

        public uint Generation { get; } = generation;

        public CancellationToken Token => _cancel.Token;

        public bool Ended
        {
            get { lock (_gate) return _ended; }
        }

        public IHomeEventsConnection? Connection
        {
            get { lock (_gate) return _connection; }
        }

        /// <summary>Takes the socket that opened; false once the attempt has ended (the caller then closes it).</summary>
        public bool Adopt(IHomeEventsConnection connection)
        {
            lock (_gate)
            {
                if (_ended) return false;
                _connection = connection;
                return true;
            }
        }

        public void End()
        {
            IHomeEventsConnection? connection;
            lock (_gate)
            {
                if (_ended) return;
                _ended = true;
                connection = _connection;
            }
            if (connection is null)
            {
                _cancel.Cancel();
                return;
            }
            // The close frame goes first: cancelling a pending receive would abort the socket without one.
            _ = connection.CloseAsync().ContinueWith(_ => _cancel.Cancel(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
