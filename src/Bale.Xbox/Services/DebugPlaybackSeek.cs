#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>Absolute source/movie seconds, independent of native, FFmpeg or reanchored HLS clocks.</summary>
internal sealed record DebugPlaybackSeek(double Seconds, bool Pause)
{
    internal static bool TryParse(string? seconds, string? pause, out DebugPlaybackSeek request)
    {
        request = null!;
        if (!double.TryParse(seconds, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0 || value >= TimeSpan.MaxValue.TotalSeconds
            || pause is not (null or "false" or "true")) return false;
        request = new(value, pause == "true");
        return true;
    }

    internal double? PlayerPosition(PlaybackTimeline timeline, double? duration) =>
        new PlaybackSeek(Seconds, Pause).PlayerPosition(timeline, duration);
}
#endif
