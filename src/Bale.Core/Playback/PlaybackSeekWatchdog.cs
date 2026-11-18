using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>What the player page knows about the watched seek at one moment (<see cref="PlaybackSeekWatchdog.Update"/>).</summary>
public readonly record struct PlaybackSeekFacts
{
    /// <summary>The backend reported the seek complete.</summary>
    public bool Completed { get; init; }
    /// <summary>The seek's mount is gone or stopped: Back, suspension, a queued failure, or a replacement mount.</summary>
    public bool Ended { get; init; }
    /// <summary>The device has no network connection.</summary>
    public bool Offline { get; init; }
    /// <summary>Another pipeline change holds the player; an expiry waits for it.</summary>
    public bool Deferred { get; init; }
}

/// <summary>What the page does after <see cref="PlaybackSeekWatchdog.Update"/>.</summary>
public enum PlaybackSeekWatchdogAction : byte
{
    None = 0,
    /// <summary>The attempt's first stuck seek: remount the same plan, session and tracks at the target.</summary>
    Remount = 1,
    /// <summary>A stuck seek after the remount was spent: fail the mount through the single recovery replan.</summary>
    Fail = 2,
}

/// <summary>
/// One playback attempt's seek watchdog: a local seek (native or FFmpeg; never a seek_reanchor or forward-HLS remount,
/// which are new mounts under their start budget) must land within <see cref="BudgetMs"/>. AetherEngine bounds both of
/// its seek paths: the native host waits 8 s (extended only while the target's buffer grows), re-anchors its producer
/// at the target and re-seeks; the software host gives a reposition 30 s and then fails as
/// <c>softwarePipelineFailed</c>, which silo-apple sends as an ordinary failure_recovery. Bale takes one 15 s window
/// and the same two moves: the attempt's first stuck seek remounts the same plan at the target, a later one fails
/// with <see cref="Failure"/>. A newer seek restarts the watch; an ended mount drops it unspent; an outage holds it.
/// <para>The decision lives in Rust (native/bale-core/src/seek_watchdog.rs); the page updates it every UI tick.</para>
/// </summary>
public sealed class PlaybackSeekWatchdog : IDisposable
{
    /// <summary>One window per seek (seek_watchdog.rs BUDGET_MS).</summary>
    public const long BudgetMs = 15_000;

    /// <summary>
    /// AetherEngine's <c>PlaybackErrorKind.softwarePipelineFailed</c>, the failure its software host publishes when a
    /// reposition does not land in its budget; silo-apple sends it as <c>failure.classification</c>.
    /// </summary>
    public const string SoftwareClassification = "softwarePipelineFailed";

    /// <summary>
    /// AetherEngine's <c>PlaybackErrorKind.nativeItemFailed</c>: the same failure on Windows' own source and decoders,
    /// the analogue of Aether's native (AVPlayer) host. Both are in silo-apple's server-can-adapt list.
    /// </summary>
    public const string NativeClassification = "nativeItemFailed";

    /// <summary>Aether's message for a reposition that never landed; English, as the server stores it.</summary>
    public const string Message = "Playback source could not reposition.";

    /// <summary>The failure for a mount whose seek got stuck after the attempt's remount was spent.</summary>
    public static PlaybackFailureV3 Failure(bool ffmpeg) => new(ffmpeg ? SoftwareClassification : NativeClassification, Message);

    public static bool Is(PlaybackFailureV3? failure) =>
        failure?.Message == Message && failure.Classification is SoftwareClassification or NativeClassification;

    private readonly SeekWatchdogHandle _handle;

    /// <summary>One attempt's watchdog (its one remount spans every seek of the attempt).</summary>
    public PlaybackSeekWatchdog()
    {
        BaleCoreNative.EnsureLoaded();
        _handle = BaleCoreSeekWatchdog.bale_core_seek_watchdog_create();
    }

    /// <summary>
    /// A local seek from <paramref name="fromSeconds"/> to <paramref name="targetSeconds"/> (player clock) was issued, with
    /// <paramref name="budgetMs"/> to land: the default unless a debug override asks. A watched seek is superseded.
    /// </summary>
    public void Begin(long nowMs, double fromSeconds, double targetSeconds, long budgetMs = BudgetMs) =>
        BaleCoreSeekWatchdog.bale_core_seek_watchdog_begin(_handle, nowMs, budgetMs, fromSeconds, targetSeconds);

    /// <summary>
    /// Records <paramref name="facts"/> and the player clock (<paramref name="clockSeconds"/>, NaN when unknown) at
    /// <paramref name="nowMs"/>. Returns an action only on the update that runs the watched seek out; a landing (the
    /// event, or the clock near the target) or an end in the same update wins.
    /// </summary>
    public PlaybackSeekWatchdogAction Update(long nowMs, PlaybackSeekFacts facts, double clockSeconds)
    {
        var bits = (facts.Completed ? 1u : 0) | (facts.Ended ? 2u : 0) | (facts.Offline ? 4u : 0) | (facts.Deferred ? 8u : 0);
        return (PlaybackSeekWatchdogAction)BaleCoreSeekWatchdog.bale_core_seek_watchdog_update(_handle, nowMs, bits, clockSeconds);
    }

    /// <summary>What is left of the watched seek's budget, or null when no seek is watched.</summary>
    public long? RemainingMs(long nowMs) =>
        BaleCoreSeekWatchdog.bale_core_seek_watchdog_remaining(_handle, nowMs) is var left && left == long.MinValue ? null : Math.Max(0, left);

    /// <summary>The attempt's one remount was used.</summary>
    public bool RemountSpent => BaleCoreSeekWatchdog.bale_core_seek_watchdog_remount_spent(_handle) != 0;

    public void Dispose() => _handle.Dispose();
}
