using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>
/// A seek kept across a quality or track change (silo-server <c>4a73f48</c>, <c>d22e55f</c>; web
/// <c>VideoPlayer.tsx</c> <c>replanSeekRef</c>): a seek the viewer makes while the change's replan is in flight, or one
/// still in flight when it starts, is where the replacement stream heads, not the position the change was asked at. The
/// replacement takes the seek when it starts there: the plan was already anchored within the web's settle tolerance of
/// it, or the plan can seek there itself. Otherwise it starts where the change was asked and the seek follows it once it
/// plays (a <c>seek_reanchor</c> where the stream cannot seek anywhere, as the web asks for one).
/// </summary>
public static class PlaybackReplanSeek
{
    /// <summary>web <c>pendingSeek.ts</c> <c>DEFAULT_SEEK_SETTLE_TOLERANCE_SECONDS</c>: a start this close settles a seek.</summary>
    public const double SettleToleranceSeconds = 1;

    /// <summary>Whether a replacement mounting <paramref name="plan"/> can start at the source time <paramref name="seek"/>.</summary>
    public static bool StartsAt(PlaybackPlan plan, double seek)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!double.IsFinite(seek) || seek < 0) return false;
        if (Math.Abs(PlaybackTimeline.From(plan.Timeline).SourceStartSeconds - seek) <= SettleToleranceSeconds) return true;
        var hls = string.Equals(plan.Stream?.Protocol, "hls", StringComparison.OrdinalIgnoreCase);
        return new PlaybackSeek(seek).CanSeekInMount(plan, hls, canSeek: true);
    }
}
