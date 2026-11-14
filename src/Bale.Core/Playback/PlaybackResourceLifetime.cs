namespace Bale.Core.Playback;

/// <summary>Prevents an old page's idle cleanup from trimming a newer, active player.</summary>
public sealed class PlaybackResourceLifetime
{
    private readonly object _gate = new();
    private int _players;

    public IDisposable EnterPlayer()
    {
        lock (_gate) _players++;
        return new Lease(this);
    }

    /// <summary>The release is synchronous, so a start cannot race its native device operations.</summary>
    public bool ReleaseIfIdle(Action release)
    {
        lock (_gate)
        {
            if (_players != 0) return false;
            release();
            return true;
        }
    }

    private sealed class Lease(PlaybackResourceLifetime owner) : IDisposable
    {
        private PlaybackResourceLifetime? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } lifetime) return;
            lock (lifetime._gate) lifetime._players--;
        }
    }
}
