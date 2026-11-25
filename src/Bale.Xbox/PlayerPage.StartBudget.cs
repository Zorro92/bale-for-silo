using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Xbox;

/// <summary>
/// The per-mount start budget (silo-apple <c>b9446d5d</c>: "fail a direct-play load that never opens so the server can
/// replan"). Every mount, the first plan's and each replacement's, on every route (engine and MediaPlayer, FFmpeg and
/// Windows' reader, HLS through the Rust reader, progressive remux), must show its first frame within
/// <see cref="PlaybackStartBudget.BudgetMs"/>. Apple exempts server HLS only because AetherEngine bounds it itself;
/// Bale's HLS has no such deadline, so it takes the budget too, and Bale has no downloads. When the budget runs out
/// the buffering-quality controller is asked first (<see cref="BufferingQualityController.StartDeadlinePassed"/>):
/// its lowering then owns the recovery, holding the failure as Apple's <c>heldStartDeadlineFailure</c> does. Otherwise
/// the mount fails with <see cref="PlaybackStartBudget.Failure"/> on the single recovery replan.
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>OnTick: spend or hold the current mount's budget, and act once when it runs out.</summary>
    private void UpdateStartBudget()
    {
        try
        {
            if (_attempt is not { Media: { StartBudget: { } budget } media } attempt || !IsCurrent(attempt, media)) return;
            // An opened mount whose HLS segments to the playlist's end held no media never starts (silo-server 4bee8f8).
            if (!media.Started && !media.Mounting && !media.FailureQueued && media.Progressive is { EndedWithoutMedia: true })
            {
                EndAtEmptyTail(attempt, media);
                return;
            }
            var now = Environment.TickCount64;
            var facts = new PlaybackStartFacts
            {
                Started = media.Started,
                Ended = media.FailureQueued,
                Opened = media.Opened,
                // Play/Pause are refused until a mount started (SetPlaybackAsync), and a remount opened paused counts as
                // started at open, so no viewer pause can meet a mount still starting. The backend's own transient
                // Paused while it opens must not hold the budget.
                Paused = false,
                Offline = ConnectionSpeedService.DeviceOffline,
                // Another pipeline change (a seek, track change or replan) holds the lifecycle and decides what
                // replaces this mount; the mount's own open holding it does not defer anything.
                Deferred = _lifecycle.CurrentCount == 0 && !media.Mounting,
            };
            if (budget.Update(now, facts)) StartBudgetRanOut(attempt, media);
        }
        catch (Exception ex) { LogFailure("start budget", ex); }
    }

    /// <summary>
    /// The mount showed no first frame within its budget. The controller may lower (Auto, always mode, media that
    /// measurably arrived below the plan's bitrate); otherwise, or when its lowering is refused or fails, the mount
    /// fails through the ordinary single recovery replan with Apple's noPlayableTrackWithinBudget.
    /// </summary>
    private void StartBudgetRanOut(Attempt attempt, Mount media)
    {
        media.StartBudgetRanOut = true;
        Log.Info($"player: start budget ran out route={RouteText(media)} opened={media.Opened} mounting={media.Mounting}");
        // An open still inside MountPlanAsync holds the lifecycle: stop it so whoever recovers can take over. Its
        // await then fails (stream opens are abandoned, FFmpeg's reads end, the engine's open fails without falling
        // back to MediaPlayer), and the caller's own failure path or the queued failure below recovers once.
        if (media.Mounting) InterruptOpening(media);
        // Held before asking: the controller's lowering starts at once and may be refused right away.
        attempt.HeldStartDeadline = media;
        var lowering = false;
        // The budget fires once: whatever the controller does, the start must not be left without an outcome.
        try { lowering = _bufferingController?.StartDeadlinePassed() == true; }
        catch (Exception ex) { LogFailure("start budget lowering", ex); }
        if (lowering)
        {
            Log.Info("player: start budget: lowering instead of failure recovery");
            return;
        }
        if (ReferenceEquals(attempt.HeldStartDeadline, media)) attempt.HeldStartDeadline = null;
        QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: false, PlaybackStartBudget.Failure);
    }

    /// <summary>Ends the steps of an open still in progress: its awaits fail, and nothing it built survives the mount.</summary>
    private static void InterruptOpening(Mount media)
    {
        Cleanup("cancel opening", media.Cancellation.Cancel);
        // Closing the transports wakes FFmpeg's and Media Foundation's blocked reads with an end of data, so
        // FFmpeg's create and the engine's open return instead of waiting out their own timeouts.
        Cleanup("close opening stream", () => media.Stream?.Dispose());
        Cleanup("close opening forward stream", () => media.Progressive?.Dispose());
    }

    /// <summary>
    /// Awaits one transport open of a mount, abandoning it when the mount is cancelled (start budget, Back, suspension):
    /// the blocking native open cannot be interrupted, so a stream it returns after that is closed, never leaked.
    /// </summary>
    private static async Task<T> OpenStepAsync<T>(Task<T> step, CancellationToken token) where T : class, IDisposable
    {
        try { return await step.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = step.ContinueWith(static late =>
            {
                if (late.IsCompletedSuccessfully) Cleanup("close abandoned stream", late.Result.Dispose);
                else _ = late.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>
    /// The held start-deadline failure after the controller's lowering was refused, failed or never adopted: the mount
    /// that ran out takes the usual recovery while it is still installed, has not started since, and nothing else
    /// failed it (silo-apple takeHeldStartDeadlineRecovery). Call with the lifecycle held.
    /// </summary>
    private async Task RecoverHeldStartDeadlineAsync(Attempt? attempt)
    {
        if (attempt is not { HeldStartDeadline: { } held }) return;
        attempt.HeldStartDeadline = null;
        if (!IsCurrent(attempt, held) || held.FailureQueued || held.Started) return;
        Log.Info("player: start budget lowering not adopted; failure recovery");
        held.FailureQueued = true;
        held.Opening.TrySetResult(false);
        await RecoverCoreAsync(attempt, 0, isDecoder: false, isNetwork: false, failure: PlaybackStartBudget.Failure);
    }

    /// <summary>The controller's lowering owns the recovery of this mount: a caller's own failure path stands aside.</summary>
    private static bool StartDeadlineLoweringOwns(Attempt attempt) =>
        attempt.HeldStartDeadline is { } held && ReferenceEquals(held, attempt.Media);

    /// <summary>A new mount's budget: the default, or the debug override captured when the mount begins.</summary>
    private static PlaybackStartBudget NewStartBudget()
    {
#if DEBUG_SERVER
        return new(Environment.TickCount64, DebugStartBudget.BudgetMs);
#else
        return new(Environment.TickCount64);
#endif
    }

#if DEBUG_SERVER
    /// <summary>The current mount's budget for <c>bale-debug.sh start-budget</c>.</summary>
    internal string DebugStartBudgetState => _attempt?.Media is { StartBudget: { } budget } media
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"mount: remaining={budget.RemainingMs(Environment.TickCount64)} ms started={media.Started} ran_out={media.StartBudgetRanOut} mounting={media.Mounting}\n")
        : "mount: none\n";
#endif
}
