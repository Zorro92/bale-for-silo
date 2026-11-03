using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Playback;

/// <summary>
/// One coordinator per journal/login. Mirrors the serialized writer and receipt rules in
/// shared/src/commonMain/kotlin/org/siloserver/silo/repository/SequencedPlayback.kt for known sessions.
/// The platform must provide a durable journal before claiming crash recovery across process death.
/// </summary>
public sealed class PlaybackProgressReporter
{
    // androidTvApp/src/androidMain/kotlin/org/siloserver/silo/tv/ui/screens/player/TvPlayerViewModel.kt:
    // POSITION_RECORD_INTERVAL_SEC measures content movement, not elapsed wall time.
    public const double PositionRecordIntervalSeconds = 10;

    // android-shared/src/androidMain/kotlin/org/siloserver/silo/common/player/PlaybackSessionLifecycle.kt:
    // PROGRESS_REPORT_INTERVAL_MS sends every 10 wall-clock seconds, even while paused.
    public static TimeSpan ProgressReportInterval { get; } = TimeSpan.FromSeconds(10);

    private readonly PlaybackClient _client;
    private readonly IPlaybackJournal _journal;
    private readonly PlaybackJournalOwner _owner;
    private readonly PlaybackRequestScope _scope;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly HashSet<string> _live = new();
    private readonly Dictionary<string, PlaybackPosition> _positions = new();
    private readonly Dictionary<string, long> _lastReports = new();
    private IReadOnlyList<PlaybackJournalEntry>? _entries;

    internal PlaybackProgressReporter(PlaybackClient client, IPlaybackJournal journal, PlaybackJournalOwner owner,
        PlaybackRequestScope scope, TimeProvider time)
    {
        _client = client;
        _journal = journal;
        _owner = owner;
        _scope = scope;
        _time = time;
    }

    /// <summary>A background tick failed; its exact pending sample remains journaled for the next tick.</summary>
    public Exception? LastProgressError { get; private set; }

    /// <summary>Call once after a successful start decision, before giving the session to a player.</summary>
    public async Task AttachSessionAsync(string sessionId, double startPosition = 0, bool isPaused = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ValidatePosition(startPosition);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _scope.Request.CheckCurrent();
            if ((await LoadAsync(cancellationToken).ConfigureAwait(false)).Any(entry => entry.Owner == _owner && entry.SessionId == sessionId))
                throw new InvalidOperationException("This playback session is already journaled.");
            var position = new PlaybackPosition(startPosition, isPaused);
            await SaveAsync(new(_owner, sessionId) { LastPosition = position }, cancellationToken).ConfigureAwait(false);
            _live.Add(sessionId);
            _positions[sessionId] = position;
            _lastReports[sessionId] = _time.GetTimestamp();
        }
        finally { _mutex.Release(); }
    }

    /// <summary>Feed player callbacks here. Pause changes are forced; ordinary positions are recorded every 10 content seconds.</summary>
    public async Task RecordPositionAsync(string sessionId, double position, bool isPaused, CancellationToken cancellationToken = default)
    {
        ValidatePosition(position);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await ActiveEntryAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var latest = new PlaybackPosition(position, isPaused);
            _positions[sessionId] = latest;
            if (entry.LastPosition is not { } saved || saved.IsPaused != isPaused
                || Math.Abs(position - saved.Position) >= PositionRecordIntervalSeconds)
                await SaveAsync(entry with { LastPosition = latest }, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutex.Release(); }
    }

    /// <summary>Immediate progress, for a forced flush or a pause. A lost reply is retried verbatim first.</summary>
    public async Task ReportProgressAsync(string sessionId, double position, bool isPaused, CancellationToken cancellationToken = default)
    {
        ValidatePosition(position);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await ActiveEntryAsync(sessionId, cancellationToken).ConfigureAwait(false);
            _positions[sessionId] = new(position, isPaused);
            _lastReports[sessionId] = _time.GetTimestamp();
            await ProgressAsync(entry, _positions[sessionId], cancellationToken).ConfigureAwait(false);
        }
        finally { _mutex.Release(); }
    }

    /// <summary>Deterministic heartbeat for a platform timer; it still sends paused/stationary samples.</summary>
    public async Task<bool> ReportIfDueAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _scope.Request.CheckCurrent();
            if (!_live.Contains(sessionId)) return false;
            var now = _time.GetTimestamp();
            if (_time.GetElapsedTime(_lastReports[sessionId], now) < ProgressReportInterval) return false;
            var entry = await ActiveEntryAsync(sessionId, cancellationToken).ConfigureAwait(false);
            _lastReports[sessionId] = now;
            await ProgressAsync(entry, _positions[sessionId], cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _mutex.Release(); }
    }

    /// <summary>Optional Core-owned timer. Cancel and await it on player teardown; transient failures do not end the loop.</summary>
    public async Task RunAsync(string sessionId, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(ProgressReportInterval, _time, cancellationToken).ConfigureAwait(false);
            try { await ReportIfDueAsync(sessionId, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is HttpRequestException or ApiException or InvalidDataException)
            {
                LastProgressError = error;
            }
        }
    }

    /// <summary>
    /// Freeze and send the exact final position. Further progress is fenced even if stop fails.
    /// Retrying a pending stop never changes its stop_id, sequence or final sample.
    /// </summary>
    public async Task StopAsync(string sessionId, double finalPosition, bool isPaused, CancellationToken cancellationToken = default)
    {
        ValidatePosition(finalPosition);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await EntryAsync(sessionId, cancellationToken).ConfigureAwait(false);
            await StopAsync(entry, new(finalPosition, isPaused), 3, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutex.Release(); }
    }

    /// <summary>
    /// On launch after restoring the same saved login and loading its account, stop orphaned sessions with
    /// the journal's last position. Live sessions are untouched. One attempt per stop bounds launch work.
    /// </summary>
    public async Task<int> ReplayAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _scope.Request.CheckCurrent();
            var pending = (await LoadAsync(cancellationToken).ConfigureAwait(false))
                .Where(entry => entry.Owner == _owner && !entry.Terminal && (!_live.Contains(entry.SessionId) || entry.Stop is not null)).ToArray();
            if (pending.Length == 0) return 0;
            // SequencedPlayback.kt checks the live installation before dispatching anything from an earlier process.
            var capabilities = await _client.GetCapabilitiesAsync(_scope, cancellationToken).ConfigureAwait(false);
            if (capabilities.InstallationId != _owner.InstallationId) return 0;
            var settled = 0;
            foreach (var entry in pending)
            {
                await StopAsync(entry, entry.LastPosition, 1, cancellationToken).ConfigureAwait(false);
                settled++;
            }
            return settled;
        }
        finally { _mutex.Release(); }
    }

    private async Task ProgressAsync(PlaybackJournalEntry entry, PlaybackPosition position, CancellationToken cancellationToken)
    {
        // Retain the latest crash position even if retrying an older pending progress request fails again.
        if (entry.LastPosition != position)
        {
            entry = entry with { LastPosition = position };
            await SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        if (entry.Progress is not null) entry = await SendProgressAsync(entry, cancellationToken).ConfigureAwait(false);
        var sample = new PlaybackProgressV2(_owner.InstallationId, NextSequence(entry.Sequence), position.Position, position.IsPaused);
        entry = entry with { Sequence = sample.Sequence, Progress = sample };
        await SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        await SendProgressAsync(entry, cancellationToken).ConfigureAwait(false);
        LastProgressError = null;
    }

    private async Task<PlaybackJournalEntry> SendProgressAsync(PlaybackJournalEntry entry, CancellationToken cancellationToken)
    {
        var sent = entry.Progress!;
        var receipt = await _client.ProgressAsync(entry.SessionId, sent, _scope, cancellationToken).ConfigureAwait(false);
        var accepted = receipt.Accepted;
        if (receipt.Outcome is not ("applied" or "replayed" or "stale_sample")
            || accepted?.Sequence is not { } sequence || sequence < sent.Sequence
            || accepted.Position is not { } position || !double.IsFinite(position) || position < 0 || accepted.IsPaused is null
            || sequence == sent.Sequence && (position != sent.Position || accepted.IsPaused != sent.IsPaused))
            throw new InvalidDataException("Playback returned an inconsistent progress receipt.");
        entry = entry with { Sequence = Math.Max(entry.Sequence, sequence), Progress = null };
        await SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    private async Task StopAsync(PlaybackJournalEntry entry, PlaybackPosition? position, int attempts, CancellationToken cancellationToken)
    {
        _scope.Request.CheckCurrent();
        if (entry.Terminal) return;
        if (entry.Stop is null)
        {
            if (position is not null) ValidatePosition(position.Position);
            var sequence = position is null ? (long?)null : NextSequence(entry.Sequence);
            var stop = new PlaybackStopV2(_owner.InstallationId, Guid.NewGuid().ToString(), sequence, position?.Position, position?.IsPaused);
            entry = entry with { Stop = stop, Sequence = sequence ?? entry.Sequence, LastPosition = position };
            await SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        _live.Remove(entry.SessionId);
        _positions.Remove(entry.SessionId);
        _lastReports.Remove(entry.SessionId);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var receipt = await _client.StopAsync(entry.SessionId, entry.Stop, _scope, cancellationToken).ConfigureAwait(false);
                // PlaybackV2Api.kt: replayed is the winning stop's receipt, possibly a different stop_id.
                if (receipt.Outcome != "replayed" && !(receipt.Outcome == "stopped" && receipt.StopId == entry.Stop.StopId))
                    throw new InvalidDataException("The playback stop receipt did not match the request.");
                await SaveAsync(entry with { Terminal = true, Progress = null }, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ApiException error) when (error.ResponseStatusCode == 404)
            {
                await SaveAsync(entry with { Terminal = true, Progress = null }, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (attempt + 1 < attempts
                && (error is HttpRequestException || error is ApiException { ResponseStatusCode: 503 }))
            {
                // SequencedPlayback.kt: at most three sends, with 250/500ms backoff; recovery gets one.
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<PlaybackJournalEntry> EntryAsync(string sessionId, CancellationToken cancellationToken)
    {
        _scope.Request.CheckCurrent();
        return (await LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(entry => entry.Owner == _owner && entry.SessionId == sessionId)
            ?? throw new InvalidOperationException("This playback session has no journal authority.");
    }

    private async Task<PlaybackJournalEntry> ActiveEntryAsync(string sessionId, CancellationToken cancellationToken)
    {
        var entry = await EntryAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!_live.Contains(sessionId) || entry.Terminal || entry.Stop is not null)
            throw new InvalidOperationException("Playback is stopping or no longer active.");
        return entry;
    }

    private async Task<IReadOnlyList<PlaybackJournalEntry>> LoadAsync(CancellationToken cancellationToken) =>
        _entries ??= (await _journal.ReadAsync(cancellationToken).ConfigureAwait(false)).ToArray();

    private async Task SaveAsync(PlaybackJournalEntry entry, CancellationToken cancellationToken)
    {
        _scope.Request.CheckCurrent();
        var next = (await LoadAsync(cancellationToken).ConfigureAwait(false))
            .Where(old => old.Owner != entry.Owner || old.SessionId != entry.SessionId).Append(entry).ToArray();
        // SequencedPlayback.kt bounds settled tombstones, never discarding a pending mutation.
        var oldTombstones = next.Where(old => old.Owner == _owner && old.Terminal).Reverse().Skip(8).ToHashSet();
        next = next.Where(old => !oldTombstones.Contains(old)).ToArray();
        await _journal.WriteAsync(next, cancellationToken).ConfigureAwait(false);
        _entries = next;
        _scope.Request.CheckCurrent();
    }

    private static long NextSequence(long sequence) => sequence is >= 0 and < long.MaxValue
        ? sequence + 1 : throw new InvalidOperationException("Playback sample sequence exhausted.");

    private static void ValidatePosition(double position)
    {
        if (!double.IsFinite(position) || position < 0) throw new ArgumentOutOfRangeException(nameof(position));
    }
}
