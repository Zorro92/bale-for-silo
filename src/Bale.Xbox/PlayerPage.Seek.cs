using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media.Playback;
using Windows.System;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    private enum SeekOutcome { Completed, Busy, Unavailable, InvalidPosition, TimedOut, Stopped, Failed }
    private sealed record SeekResult(SeekOutcome Outcome, double Source = 0, double Player = 0,
        MediaPlaybackState State = MediaPlaybackState.None, int HResult = 0);
    private bool _seeking;
    private bool _skipProcessing;
    private double? _skipTarget;
    private double? _pendingSkipTarget;
    // A chapter jump's chip names the chapter instead of a delta.
    private string? _skipLabel;

    private double SourcePosition(Attempt attempt)
    {
        try
        {
            if (attempt.Media is { Opened: true, Player: { } player } media
                && media.Transport.Timeline.SourceForPlayer(player.PlaybackSession.Position.TotalSeconds) is { } source)
                attempt.Position = source;
        }
        catch (Exception ex) { LogFailure("position snapshot", ex); }
        return _sourcePosition = attempt.Position;
    }

    private void QueueSkip(bool forward)
    {
        try
        {
            if (!_active || _leaving || _suspended || _attempt is not { Attached: true } attempt
                || !_skipProcessing && attempt.Media is not { Opened: true, Started: true, FailureQueued: false }) return;
            // Presses during a native seek/remount build on the requested SOURCE position, never
            // the old player clock. Coalesce pending absolute targets without losing any press.
            _pendingSkipTarget = _skipTarget = PlaybackSeek.SkipTarget(_skipTarget ?? SourcePosition(attempt), _duration, forward);
            _skipLabel = null;
            ShowSkipFeedback(forward ? PlaybackSeek.SkipForwardSeconds : -PlaybackSeek.SkipBackSeconds, _skipTarget.Value);
            if (!_skipProcessing) _ = ProcessSkipsAsync();
        }
        catch (Exception ex) { LogFailure("skip input", ex); }
    }

    private async Task ProcessSkipsAsync()
    {
        _skipProcessing = true;
        try
        {
            while (_active && !_leaving && !_suspended && _pendingSkipTarget is { } target)
            {
                _pendingSkipTarget = null;
                var result = await SeekAsync(new PlaybackSeek(target));
                if (result.Outcome == SeekOutcome.Completed) continue;
                Log.Info($"player: skip not completed outcome={result.Outcome}");
                HideTransport();
                break;
            }
        }
        catch (Exception ex) { LogFailure("skip", ex); HideTransport(); }
        finally
        {
            _pendingSkipTarget = _skipTarget = null;
            _skipProcessing = false;
        }
    }

    private async Task<SeekResult> SeekAsync(PlaybackSeek request)
    {
        // This is the original debug absolute-seek path, now shared with real transport input.
        // Do not queue a seek behind an opening/recovering pipeline; Back can cancel an active seek.
        if (!_lifecycle.Wait(0)) return new(SeekOutcome.Busy);
        Mount? observed = null;
        _seeking = true;
        try
        {
            if (_attempt is not { Media: { Opened: true, FailureQueued: false, Player: { } player } media } attempt
                || !IsCurrent(attempt, media)) return new(SeekOutcome.Unavailable);
            if (!double.IsFinite(request.Seconds) || request.Seconds < 0 || request.Seconds >= TimeSpan.MaxValue.TotalSeconds
                || _duration is > 0 && request.Seconds > _duration) return new(SeekOutcome.InvalidPosition);

            var paused = request.Pause || player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused;
            var session = player.PlaybackSession;
            var ranges = session.SeekableRanges();
            // Windows' growing-HLS ranges describe production, not reliable seek landings.
            // Keep them for diagnostics; only a complete server window authorizes native HLS.
            // HLS through FFmpeg is forward-only: inside the server's seek window it remounts locally at
            // the target's segment (no server round trip); outside it the server reanchors, as for
            // Windows' HLS source.
            var forwardHls = media.Transport.IsHls && media.Progressive is not null;
            var remount = forwardHls && request.CanSeekInMount(attempt.Plan!, isHls: true, canSeek: true);
            var local = !forwardHls && request.CanSeekInMount(attempt.Plan!, media.Transport.IsHls, session.CanSeek);
#if DEBUG_SERVER
            Log.Info($"player: seek target={request.Seconds:0.###} player={session.Position.TotalSeconds:0.###} offset={media.Transport.Timeline.TimelineOffsetSeconds:0.###} ranges={ranges.Count} action={(local ? "native" : remount ? "remount" : "reanchor")}");
#endif
            if (remount)
            {
                media = await RemountHlsAsync(attempt, request, paused);
                if (media is null) return new(SeekOutcome.Unavailable);
                player = media.Player!;
                session = player.PlaybackSession;
            }
            else if (!local)
            {
                if (!attempt.ServerFeatures.Contains(PlaybackSeekReanchor.Feature)) return new(SeekOutcome.Unavailable);
                media = await ReanchorSeekAsync(attempt, media, request.Seconds, paused);
                if (media is null) return new(SeekOutcome.Unavailable);
                player = media.Player!;
                session = player.PlaybackSession;
            }
            if (request.PlayerPosition(media.Transport.Timeline, _duration) is not { } target)
                return new(SeekOutcome.InvalidPosition);

            observed = media;
#if DEBUG_SERVER
            media.FrameDelivery?.SetSeeking(true);
#endif
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnSeekCompleted(PlaybackBackendSession sender, object? args) => completed.TrySetResult(true);
            session.SeekCompleted += OnSeekCompleted;
            try
            {
                if (paused) player.Pause();
                // The replacement already mounted at its player_start_seconds. A second native
                // seek after opening would reintroduce EVENT/live-edge jumps as its clock advances.
                if (local && Math.Abs(session.Position.TotalSeconds - target) > 0.000001)
                {
                    // The seek watchdog (PlayerPage.SeekWatchdog.cs) follows this seek until it lands, past this wait
                    // too, and recovers a seek that never does. Only its debug hang keeps the seek from the source.
                    if (BeginWatchedSeek(attempt, media, target, request.Seconds, paused))
                        session.Position = TimeSpan.FromSeconds(target);
                    var winner = await Task.WhenAny(completed.Task, Task.Delay(10000, media.Token));
                    media.Token.ThrowIfCancellationRequested();
                    if (winner != completed.Task) return new(SeekOutcome.TimedOut);
                }
                if (!IsCurrent(attempt, media) || media.FailureQueued) return new(SeekOutcome.Stopped);
                if (paused) player.Pause();
                var source = SourcePosition(attempt);
                UpdatePlaybackState(attempt, media);
#if DEBUG_SERVER
                media.FrameDelivery?.SetSeeking(false);
                RefreshStats(attempt);
                Log.Info($"player: seek completed source={source:0.###} player={session.Position.TotalSeconds:0.###}");
#endif
                // Flush the actual landed SOURCE position, even for a clamped skip smaller than
                // the journal's normal 10-content-second threshold. A failed HTTP flush stays durable.
                await ReportPositionAsync(attempt, source, paused);
                return new(SeekOutcome.Completed, source, session.Position.TotalSeconds, session.PlaybackState);
            }
            finally { session.SeekCompleted -= OnSeekCompleted; }
        }
        catch (OperationCanceledException) { return new(SeekOutcome.Stopped); }
        catch (Exception ex)
        {
            LogFailure("seek", ex);
            return new(SeekOutcome.Failed, HResult: ex.HResult);
        }
        finally
        {
#if DEBUG_SERVER
            observed?.FrameDelivery?.SetSeeking(false);
#endif
            _seeking = false;
            // Every outcome, including a reanchor remount: the overlay's extrapolated clock is stale now.
            PushSubtitleClock(force: true);
            _lifecycle.Release();
        }
    }

    private async Task<Mount?> ReanchorSeekAsync(Attempt attempt, Mount media, double source, bool paused)
    {
        // PlaybackSessionManager.kt reanchorActiveVideoSession: without a reliable local window,
        // refresh the SAME route/session at source time instead of clamping to the manifest's head.
        // A progressive remux seeks and reconnects this way too (protocol §6.2): a new URL at source.
        // The Rust remux's connection closes with the old mount; warm a spare during the replan.
        if (media.Progressive is RustProgressiveStream) RustTransport.WarmConnection(attempt.Client.Server);
        var decision = await RequestReanchorAsync(attempt, source);
        if (!IsCurrent(attempt, media) || decision is null)
        {
            Log.Info("player: seek reanchor rejected");
            return null;
        }
        Pause(attempt);
        DisposeMount(attempt);
        var kept = ClientBufferingReplan("seek_reanchor")?.BandwidthEstimateKbps;
        attempt.Plan = decision.PlaybackPlan!;
        attempt.ServerFeatures = decision.ServerFeatures ?? [];
        // Every adopted replan is a new plan for buffering quality, with its own plan_summary.
        AdoptBufferingPlan(attempt, controllerInitiated: false, kept);
        // A forward-only remux opens at its keyframe origin, at most one GOP before the target.
        attempt.Position = _sourcePosition = PlaybackProgressive.MountSourceStart(attempt.Plan, source);
        _state = "loading";
        SetLoading(true);
        var timeline = PlaybackTimeline.From(attempt.Plan.Timeline);
        await MountPlanAsync(attempt, PlaybackProgressive.MountPlayerStart(attempt.Plan, timeline.PlayerStartSeconds), paused);
        return await OpenedReplacementAsync(attempt);
    }

    /// <summary>
    /// HLS through FFmpeg: the same plan remounted at the segment holding the target (the Rust reader
    /// starts there), with no server request. Playback resumes at that segment's start, at most one
    /// segment before the target.
    /// </summary>
    private async Task<Mount?> RemountHlsAsync(Attempt attempt, PlaybackSeek request, bool paused)
    {
        if (request.PlayerPosition(PlaybackTimeline.From(attempt.Plan!.Timeline), _duration) is not { } playerTarget) return null;
        Pause(attempt);
        DisposeMount(attempt);
        attempt.Position = _sourcePosition = request.Seconds;
        _state = "loading";
        SetLoading(true);
        await MountPlanAsync(attempt, playerTarget, paused);
        return await OpenedReplacementAsync(attempt);
    }

    private async Task<Mount?> OpenedReplacementAsync(Attempt attempt)
    {
        if (attempt.Media is not { } replacement) return null;
        var ready = await Task.WhenAny(replacement.Opening.Task, Task.Delay(15000, replacement.Token));
        replacement.Token.ThrowIfCancellationRequested();
        return ready == replacement.Opening.Task && await replacement.Opening.Task && IsCurrent(attempt, replacement)
            && !replacement.FailureQueued ? replacement : null;
    }

    /// <summary>A same-route seek_reanchor decision at <paramref name="source"/>, or null when the server changed the route.</summary>
    private async Task<PlaybackDecision?> RequestReanchorAsync(Attempt attempt, double source)
    {
#if DEBUG_SERVER
        if (attempt.DebugUrl is not null) return null;
#endif
        // After a lowering, a reanchor keeps asking for it (the kept step-down estimate, no source).
        var request = PlaybackReplan.WithClientReplan(PlaybackReplan.ForSeek(attempt.Start!, attempt.Plan!, attempt.PlanAttemptId,
            attempt.AttemptedPlanKeys, attempt.Replanned ? 2 : 1, source), ClientBufferingReplan("seek_reanchor"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var decision = await attempt.Client.Playback.ReplanAsync(attempt.SessionId!, request, attempt.Services!.Installation, deadline.Token);
        return PlaybackSeekReanchor.Matches(attempt.Plan!, decision, attempt.SessionId!, attempt.Start!.FileId) ? decision : null;
    }

    private static async Task ReportPositionAsync(Attempt attempt, double source, bool paused)
    {
#if DEBUG_SERVER
        if (attempt.DebugUrl is not null) return;
#endif
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await attempt.Services!.Reporter.ReportProgressAsync(attempt.SessionId!, source, paused, deadline.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogFailure("progress flush", ex); }
    }
}
