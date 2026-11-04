namespace Bale.Core.Playback;

/// <summary>
/// Samples cumulative network counters once a second, keeping the last five samples. Idle playback
/// from cache expires the estimate instead of misreporting zero as the network's capacity.
/// </summary>
public sealed class PlaybackThroughputMeter
{
    private const int WindowSamples = 5;
    private readonly Queue<(long Bytes, TimeSpan Time)> _window = new();
    private long _lastBytes;
    private TimeSpan _lastTime;

    public long? Sample(long downloadedBytes, TimeSpan downloadTime)
    {
        if (downloadedBytes < _lastBytes || downloadTime < _lastTime)
        {
            // A replacement transport starts fresh counters; never mix its rate with the old mount.
            _window.Clear();
            _lastBytes = 0;
            _lastTime = TimeSpan.Zero;
        }
        _window.Enqueue((downloadedBytes - _lastBytes, downloadTime - _lastTime));
        _lastBytes = downloadedBytes;
        _lastTime = downloadTime;
        if (_window.Count > WindowSamples) _window.Dequeue();
        var bytes = _window.Sum(sample => (double)sample.Bytes);
        var seconds = _window.Sum(sample => sample.Time.TotalSeconds);
        if (bytes <= 0 || seconds <= 0) return null;
        var bps = Math.Round(bytes * 8 / seconds);
        return bps >= long.MaxValue ? long.MaxValue : (long)bps;
    }
}
