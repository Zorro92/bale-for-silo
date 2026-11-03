namespace Bale.Core.Playback;

// Wire samples from shared/src/commonMain/kotlin/org/siloserver/silo/network/apiv2/PlaybackV2Api.kt.
public sealed record PlaybackProgressV2(string InstallationId, long Sequence, double Position, bool IsPaused);
public sealed record PlaybackStopV2(string InstallationId, string StopId, long? Sequence = null, double? Position = null, bool? IsPaused = null);
public sealed record PlaybackPosition(double Position, bool IsPaused);

/// <summary>
/// No credentials. LoginId is the platform's persisted saved-login identity, not an access token.
/// repository/SequencedPlayback.kt never replays under another login, account, profile, origin or installation.
/// </summary>
public sealed record PlaybackJournalOwner(string Origin, string LoginId, string AccountId, string ProfileId, string InstallationId);

/// <summary>
/// Immutable journal state for a known session. The pending sample/stop must be saved before dispatch.
/// LastPosition survives a successful progress receipt so crash cleanup can stop at the last recorded position.
/// </summary>
public sealed record PlaybackJournalEntry(PlaybackJournalOwner Owner, string SessionId)
{
    public long Sequence { get; init; }
    public PlaybackPosition? LastPosition { get; init; }
    public PlaybackProgressV2? Progress { get; init; }
    public PlaybackStopV2? Stop { get; init; }
    public bool Terminal { get; init; }
}

/// <summary>
/// repository/SequencedPlayback.kt's PlaybackJournalStore: atomic durable writes complete before returning;
/// a failed write prevents dispatch. Give a journal one coordinator at a time. Disk persistence comes later.
/// </summary>
public interface IPlaybackJournal
{
    Task<IReadOnlyList<PlaybackJournalEntry>> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(IReadOnlyList<PlaybackJournalEntry> entries, CancellationToken cancellationToken = default);
}

/// <summary>Tests and process-local use only; this implementation cannot survive process death.</summary>
public sealed class InMemoryPlaybackJournal : IPlaybackJournal
{
    private readonly object _sync = new();
    private PlaybackJournalEntry[] _entries = [];

    public Task<IReadOnlyList<PlaybackJournalEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) return Task.FromResult<IReadOnlyList<PlaybackJournalEntry>>(_entries.ToArray());
    }

    public Task WriteAsync(IReadOnlyList<PlaybackJournalEntry> entries, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) _entries = entries.ToArray();
        return Task.CompletedTask;
    }
}
