#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// Lines of the debug frame log (bale-debug.sh frame-log; docs/player-plan.md, "Frame log"), to classify each missed
/// frame by joining them with the FFmpegInteropX fork's per-frame lines
/// (<c>Bale rav1g stage u=&lt;unit&gt; pts=&lt;100 ns as handed to MF&gt; take= submit= release= gpudone=</c>) on pts.
/// <para>
/// The clock: every <c>t=</c> is QueryPerformanceCounter milliseconds (counter × 1000 / frequency, 3 decimals), the
/// clock of the native observer's records, of Stopwatch and of the fork's lines, so the lines also join on time.
/// <c>pts</c>, <c>dur</c> and <c>pos</c> are integer 100 ns ticks, <c>pts</c> exactly the MediaStreamSample Timestamp;
/// <c>lead</c> is pts − pos in milliseconds with one decimal. <c>-</c> marks a value that does not exist.
/// </para>
/// </summary>
internal static class DebugFrameLog
{
    /// <summary>Record flag: the observer had no player clock (engine preroll before the clock was attached).</summary>
    internal const uint NoClock = 4;

    /// <summary>
    /// <c>frame: t= pts= dur= pos= lead= kind= flags=</c> for one observer record. Kind 1 is a video sample at delivery
    /// (after FFmpeg's sample handler filled it), with the player clock read then; 2 the MSS Starting event and 3/4
    /// seeking on/off have no pts, dur, pos or lead. Flags: bit 0 playing, bit 1 discontinuous, bit 2 no player clock
    /// (pos and lead <c>-</c>).
    /// </summary>
    internal static string Frame(in PlaybackFrameRecord record)
    {
        var sample = record.Kind == 1;
        var clocked = sample && (record.Flags & NoClock) == 0;
        return "frame: t=" + Time(record.NowMilliseconds)
            + " pts=" + (sample ? Ticks(record.Timestamp) : "-")
            + " dur=" + (sample ? Ticks(record.Duration) : "-")
            + " pos=" + (clocked ? Ticks(record.Position) : "-")
            + " lead=" + (clocked ? Milliseconds(unchecked(record.Timestamp - record.Position)) : "-")
            + " kind=" + record.Kind.ToString(CultureInfo.InvariantCulture)
            + " flags=" + record.Flags.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>frame-event: t= kind= pos=</c> for one IMFMediaEngine playback event from bale_gpu's event ring: its QPC time and
    /// the engine position read in the engine's callback without waiting (NaN, so <c>-</c>, before the session's frame log
    /// was on, or rarely when a sample callback held the engine clock then).
    /// </summary>
    internal static string Event(double qpcMilliseconds, uint kind, double positionSeconds) =>
        "frame-event: t=" + Time(qpcMilliseconds) + " kind=" + EventName(kind) + " pos=" + Position(positionSeconds);

    /// <summary>MF_MEDIA_ENGINE_EVENT names, as bale_gpu's engine report spells them.</summary>
    internal static string EventName(uint kind) => kind switch
    {
        8 => "play",
        9 => "pause",
        12 => "waiting",
        13 => "playing",
        16 => "seeking",
        17 => "seeked",
        19 => "ended",
        1005 => "bufferingstarted",
        1006 => "bufferingended",
        _ => kind.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>100 ns ticks as milliseconds with one decimal, rounded half away from zero in integer arithmetic.</summary>
    internal static string Milliseconds(long ticks)
    {
        // long.MinValue's magnitude still fits in ulong.
        var magnitude = ticks < 0 ? (ulong)(-(ticks + 1)) + 1 : (ulong)ticks;
        var tenths = magnitude / 1000 + (magnitude % 1000 >= 500 ? 1UL : 0UL);
        var text = (tenths / 10).ToString(CultureInfo.InvariantCulture) + "." + (tenths % 10).ToString(CultureInfo.InvariantCulture);
        return ticks < 0 && tenths != 0 ? "-" + text : text;
    }

    private static string Time(double milliseconds) =>
        double.IsFinite(milliseconds) ? milliseconds.ToString("F3", CultureInfo.InvariantCulture) : "-";

    private static string Ticks(long ticks) => ticks.ToString(CultureInfo.InvariantCulture);

    /// <summary>Engine seconds as 100 ns ticks, as bale_gpu rounds them for sample records.</summary>
    private static string Position(double seconds)
    {
        var ticks = Math.Round(seconds * 10_000_000, MidpointRounding.AwayFromZero);
        return double.IsFinite(ticks) && Math.Abs(ticks) < 9e18 ? Ticks((long)ticks) : "-";
    }
}
#endif
