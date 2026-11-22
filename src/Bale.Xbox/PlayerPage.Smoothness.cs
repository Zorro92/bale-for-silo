using System.Globalization;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    private static void ConfigureSmoothness(Attempt attempt, Mount media, PlaybackBackend player)
    {
        player.Smoothness = attempt.Smoothness;
        player.Smoothness.BeginMount(PlaybackBackend.ClockSeconds);
#if DEBUG_SERVER
        player.Smoothness.StallCompleted -= LogSmoothnessStall;
        player.Smoothness.StallCompleted += LogSmoothnessStall;
        long previousWaits = media.Stream?.DemandWaits ?? 0;
        long previousWaitTicks = media.Stream?.DemandWaitTime.Ticks ?? 0;
        player.NetworkWaiting = () =>
        {
            // Heuristic, not a decoder verdict: an empty unfinished progressive/HLS reader, or range demand
            // waits/time that grew since the previous state sample. A completion can upgrade the open stall.
            // These transport counters never take FFmpegInteropX's source lock. See docs/player-plan.md.
            if (media.Progressive is { } progressive) return !progressive.Completed && progressive.BufferedBytes == 0;
            if (media.Stream is not { } stream) return false;
            var waits = stream.DemandWaits;
            var ticks = stream.DemandWaitTime.Ticks;
            var waiting = waits > previousWaits || ticks > previousWaitTicks;
            previousWaits = waits;
            previousWaitTicks = ticks;
            return waiting;
        };
#endif
    }

#if DEBUG_SERVER
    private static void LogSmoothnessStall(PlaybackStall stall) => Log.Info(string.Create(CultureInfo.InvariantCulture,
        $"player: stall cause={(stall.Network ? "network" : "decoder")} duration={stall.DurationSeconds * 1000:0} ms"));

    private static void LogSmoothnessSummary(PlaybackSmoothnessSnapshot smoothness, bool engine, DebugDecoderSnapshot decoder, double? fps)
    {
        if (!engine) smoothness = smoothness with { MissedFrames = null, MissedPercent = null };
        Log.Info($"player: smoothness summary Missed frames: {smoothness.MissedText}; Stalls: {smoothness.StallsText}; "
            + $"Dropped (engine): {(engine ? smoothness.DroppedFrames.ToString(CultureInfo.InvariantCulture) : "unavailable")}; "
            + $"Decode rate: {decoder.DecodeRateText(fps)}; Decoder discarded: {decoder.DiscardedText}; "
            + $"Starved: {decoder.StarvedText}; Playback time: {smoothness.PlaybackTimeText}");
    }
#endif
}
