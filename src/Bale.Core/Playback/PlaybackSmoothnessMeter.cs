using System.Globalization;

namespace Bale.Core.Playback;

/// <summary>
/// Playback smoothness (owner, 2026-11-22): renderer drops plus frames lost while the clock waits for data.
/// Times are monotonic seconds. Feed real engine events, not an expected-fps/position polling heuristic.
/// No sample/frame observer is needed; only state changes and the backend's existing state poll touch this meter.
/// </summary>
public sealed class PlaybackSmoothnessMeter
{
    private readonly object _gate = new();
    private readonly Queue<PlaybackStall> _recent = new();
    private bool _started, _paused, _seeking, _ready, _waiting, _ended;
    private double _time, _active, _stalled, _longest, _stallStart, _networkSeconds;
    private long _stalls, _networkStalls;
    private bool _network;
    private ulong? _lastDropped;
    private ulong _dropped;
    private double? _position;
    private double _media;
    private double? _wallStart;

    /// <summary>Debug consumers can tag each completed stall without recording per-frame history.</summary>
    public event Action<PlaybackStall>? StallCompleted;

    /// <summary>A replacement source keeps this playback's totals, with new preroll and counter baselines.</summary>
    public void BeginMount(double seconds)
    {
        lock (_gate)
        {
            Advance(seconds);
            CloseStall();
            _started = _paused = _seeking = _ready = _ended = false;
            _position = null;
            _lastDropped = null;
        }
    }

    public void Observe(PlaybackSmoothnessEvent kind, double seconds, bool network = false)
    {
        lock (_gate)
        {
            if (_ended) return;
            Advance(seconds);
            switch (kind)
            {
                case PlaybackSmoothnessEvent.Play:
                    _paused = false;
                    break;
                case PlaybackSmoothnessEvent.Playing:
                    if (_paused || _seeking) break;
                    CloseStall();
                    _wallStart ??= _time;
                    _started = _ready = true;
                    break;
                case PlaybackSmoothnessEvent.Waiting:
                case PlaybackSmoothnessEvent.BufferingStarted:
                    if (!_started || !_ready || _paused || _seeking) break;
                    if (!_waiting)
                    {
                        _waiting = true;
                        _stallStart = _time;
                        _network = network;
                        _stalls++;
                    }
                    else _network |= network;
                    break;
                case PlaybackSmoothnessEvent.BufferingEnded:
                    _network |= _waiting && network;
                    CloseStall();
                    break;
                case PlaybackSmoothnessEvent.Pause:
                    CloseStall();
                    _paused = true;
                    _ready = false;
                    break;
                case PlaybackSmoothnessEvent.Seeking:
                    CloseStall();
                    _seeking = true;
                    _ready = false;
                    _position = null;
                    break;
                case PlaybackSmoothnessEvent.Seeked:
                    _seeking = false;
                    _position = null;
                    // A subsequent Playing is the end of seek preroll, not Seeked alone.
                    break;
                case PlaybackSmoothnessEvent.Ended:
                    CloseStall();
                    _ended = true;
                    break;
            }
        }
    }

    /// <summary>Read the engine counter before pause/seek commands too: MF restarts it at these boundaries.</summary>
    public void ObserveDropped(ulong dropped)
    {
        lock (_gate)
        {
            if (_ended) return;
            if (_started && !_paused && !_seeking)
                _dropped += _lastDropped is { } previous && dropped >= previous ? dropped - previous : dropped;
            _lastDropped = dropped;
        }
    }

    /// <summary>Accumulates media advanced; seek targets are new baselines, never watched media.</summary>
    public void ObservePosition(double position)
    {
        lock (_gate)
        {
            if (_ended || !double.IsFinite(position)) return;
            if (_started && !_paused && !_seeking && _position is { } previous)
                _media += Math.Max(0, position - previous);
            _position = _seeking ? null : position;
        }
    }

    public void NoteNetworkWait(bool network)
    {
        lock (_gate) _network |= _waiting && network;
    }

    public PlaybackSmoothnessSnapshot Read(double seconds, double? framesPerSecond)
    {
        lock (_gate)
        {
            var now = _ended ? _time : Math.Max(_time, double.IsFinite(seconds) ? seconds : _time);
            Prune(now);
            var elapsed = now - _time;
            var active = _active + (Counting ? elapsed : 0);
            var currentStall = _waiting ? now - _stallStart : 0;
            var stalled = _stalled + (_waiting ? elapsed : 0);
            long recentCount = 0;
            double recentSeconds = 0;
            foreach (var stall in _recent)
            {
                if (stall.StartSeconds >= now - 10) recentCount++;
                recentSeconds += Math.Max(0, stall.EndSeconds - Math.Max(stall.StartSeconds, now - 10));
            }
            if (_waiting)
            {
                if (_stallStart >= now - 10) recentCount++;
                recentSeconds += Math.Min(10, currentStall);
            }
            var fps = framesPerSecond is > 0 and { } value && double.IsFinite(value) ? value : (double?)null;
            var missed = fps is { } rate ? _dropped + stalled * rate : (double?)null;
            var expected = fps * active;
            return new(_dropped, missed, expected > 0 ? Math.Clamp(missed!.Value / expected.Value * 100, 0, 100) : fps is null ? null : 0,
                _stalls, stalled, Math.Max(_longest, currentStall), recentCount, recentSeconds,
                _networkStalls + (_waiting && _network ? 1 : 0), _networkSeconds + (_waiting && _network ? currentStall : 0),
                _wallStart is { } start ? now - start : 0, _media, active);
        }
    }

    private bool Counting => _started && _ready && !_paused && !_seeking && !_ended;

    private void Advance(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        var now = Math.Max(_time, seconds);
        var elapsed = now - _time;
        if (Counting) _active += elapsed;
        if (_waiting) _stalled += elapsed;
        _time = now;
        Prune(now);
    }

    private void CloseStall()
    {
        if (!_waiting) return;
        var stall = new PlaybackStall(_stallStart, _time, _network);
        _waiting = false;
        _longest = Math.Max(_longest, stall.DurationSeconds);
        if (_network) { _networkStalls++; _networkSeconds += stall.DurationSeconds; }
        _recent.Enqueue(stall);
        StallCompleted?.Invoke(stall);
    }

    private void Prune(double now)
    {
        while (_recent.TryPeek(out var stall) && stall.EndSeconds <= now - 10) _recent.Dequeue();
    }
}

public enum PlaybackSmoothnessEvent { Play, Playing, Waiting, BufferingStarted, BufferingEnded, Pause, Seeking, Seeked, Ended }

public readonly record struct PlaybackStall(double StartSeconds, double EndSeconds, bool Network)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

public readonly record struct PlaybackSmoothnessSnapshot(ulong DroppedFrames, double? MissedFrames, double? MissedPercent,
    long Stalls, double StalledSeconds, double LongestStallSeconds, long RecentStalls, double RecentStalledSeconds,
    long NetworkStalls, double NetworkStalledSeconds, double WallSeconds, double MediaSeconds, double ActiveSeconds = 0)
{
    public string MissedText => MissedFrames is { } missed && MissedPercent is { } percent
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(missed, MidpointRounding.AwayFromZero):0} ({percent:0.0}%)") : "unavailable";

    public string StallsText => string.Create(CultureInfo.InvariantCulture,
        $"{Stalls} · {StalledSeconds * 1000:0} ms · longest {LongestStallSeconds * 1000:0} ms · last 10 s {RecentStalls}/{RecentStalledSeconds * 1000:0} ms · network {NetworkStalls}/{NetworkStalledSeconds * 1000:0} ms · decoder {Stalls - NetworkStalls}/{Math.Max(0, StalledSeconds - NetworkStalledSeconds) * 1000:0} ms");

    public string PlaybackTimeText => string.Create(CultureInfo.InvariantCulture, $"{WallSeconds:0.0} s for {MediaSeconds:0.0} s of media");
}
