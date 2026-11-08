using System.Runtime;

namespace Bale.Core.Playback;

/// <summary>Keeps low-latency GC active until every overlapping player has finished shutting down.</summary>
public sealed class PlaybackGcLatency
{
    public static PlaybackGcLatency Shared { get; } = new(() => GCSettings.LatencyMode,
        mode => GCSettings.LatencyMode = mode);

    private readonly object _gate = new();
    private readonly Func<GCLatencyMode> _get;
    private readonly Action<GCLatencyMode> _set;
    private GCLatencyMode _originalMode;
    private int _leases;

    internal PlaybackGcLatency(Func<GCLatencyMode> get, Action<GCLatencyMode> set)
    {
        _get = get;
        _set = set;
        _originalMode = get();
    }

    /// <summary>The mode saved before the current (or most recent) group of playback leases.</summary>
    public GCLatencyMode OriginalMode { get { lock (_gate) return _originalMode; } }
    public bool IsHeld { get { lock (_gate) return _leases != 0; } }

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_leases == 0)
            {
                _originalMode = _get();
                TrySet(GCLatencyMode.SustainedLowLatency);
            }
            _leases++;
            return new Lease(this);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_leases == 0) TrySet(_originalMode);
        }
    }

    private void TrySet(GCLatencyMode mode)
    {
        try { _set(mode); }
        catch (Exception)
        {
            // Runtime GC restrictions must not interrupt playback or its teardown.
        }
    }

    private sealed class Lease(PlaybackGcLatency owner) : IDisposable
    {
        private PlaybackGcLatency? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
