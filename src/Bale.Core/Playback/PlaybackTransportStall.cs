namespace Bale.Core.Playback;

/// <summary>
/// A direct play whose range stream gave up mid-title: its retry window (retry_window.rs) was spent, or the server
/// answered a final status. FFmpeg reads the failed read as the end of the file, so without this the title ended as
/// if finished. silo-android's TvPlayerViewModel treats a network failure the same way: one retry of the same route
/// (renewed once playback progresses), then the recovery ladder with <c>transport_stall</c>. An outage says nothing
/// about a decoder, a console stage or a track, so the replan keeps every claim, as for the start budget.
/// </summary>
public static class PlaybackTransportStall
{
    /// <summary>silo-android's classification for a network connection failure or timeout.</summary>
    public const string Classification = "transport_stall";

    /// <summary>English, as the server stores it; never transport text.</summary>
    public const string Message = "The media connection failed after its retries.";

    /// <summary>silo-android MAX_TRANSIENT_NETWORK_RETRIES: same-route remounts before the replan.</summary>
    public const int MaxSameRouteRemounts = 1;

    /// <summary>
    /// A remount this long ago counts as recovered playback, so a later outage gets a fresh same-route retry
    /// (Android resets on progress; Bale's progressive reconnects use the same two-minute window).
    /// </summary>
    public const long RenewAfterMs = 120_000;

    public static PlaybackFailureV3 Failure { get; } = new(Classification, Message);

    public static bool Is(PlaybackFailureV3? failure) => failure?.Classification == Classification;

    /// <summary>The attempt's same-route remount count after the renewal rule, for a stall at <paramref name="nowMs"/>.</summary>
    public static int RemountsAfterRenewal(int remounts, long lastRemountAtMs, long nowMs) =>
        remounts > 0 && nowMs - lastRemountAtMs > RenewAfterMs ? 0 : remounts;

    /// <summary>True when a stall remounts the same plan instead of replanning.</summary>
    public static bool RemountsSamePlan(int remounts, long lastRemountAtMs, long nowMs) =>
        RemountsAfterRenewal(remounts, lastRemountAtMs, nowMs) < MaxSameRouteRemounts;
}
