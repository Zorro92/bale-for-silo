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

    internal bool HandleSkipKey(VirtualKey key, bool firstDown)
    {
        var direction = key switch
        {
            VirtualKey.GamepadLeftTrigger or VirtualKey.J => -1,
            VirtualKey.GamepadRightTrigger or VirtualKey.L => 1,
            _ => 0,
        };
        if (direction == 0) return false;
        // TvPlayerRemoteKeyAction.kt: act on the FIRST DOWN, consume repeats and UP halves.
        if (firstDown) QueueSkip(direction > 0);
        return true;
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
            var ranges = session.GetSeekableRanges().Select(range => (range.Start.TotalSeconds, range.End.TotalSeconds)).ToArray();
            if (ranges.Length == 0 && session.CanSeek && session.NaturalDuration > TimeSpan.Zero)
                ranges = [(0, session.NaturalDuration.TotalSeconds)];
            if (!request.CanSeekInMount(attempt.Plan!, media.Transport.IsHls, session.CanSeek, ranges))
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
            void OnSeekCompleted(MediaPlaybackSession sender, object args) => completed.TrySetResult(true);
            session.SeekCompleted += OnSeekCompleted;
            try
            {
                if (paused) player.Pause();
                if (Math.Abs(session.Position.TotalSeconds - target) > 0.000001)
                {
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
#endif
                // Flush the actual landed SOURCE position, even for a clamped skip smaller than
                // the journal's normal 10-content-second threshold. A failed HTTP flush stays durable.
                await ReportSeekPositionAsync(attempt, source, paused);
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
        // PlaybackSessionManager.kt reanchorActiveVideoSession: outside a produced HLS window,
        // refresh the SAME route/session at source time instead of clamping to the manifest's head.
        var request = PlaybackReplan.ForSeek(attempt.Start!, attempt.Plan!, attempt.PlanAttemptId,
            attempt.AttemptedPlanKeys, attempt.Replanned ? 1 : 0, source);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var decision = await attempt.Client.Playback.ReplanAsync(attempt.SessionId!, request, attempt.Services!.Installation, deadline.Token);
        if (!IsCurrent(attempt, media) || !PlaybackSeekReanchor.Matches(attempt.Plan!, decision, attempt.SessionId!, attempt.Start!.FileId))
        {
            Log.Info("player: seek reanchor rejected");
            return null;
        }
        Pause(attempt);
        DisposeMount(attempt);
        attempt.Plan = decision.PlaybackPlan!;
        attempt.ServerFeatures = decision.ServerFeatures ?? [];
        attempt.Position = _sourcePosition = source;
        _state = "loading";
        SetLoading(true);
        var timeline = PlaybackTimeline.From(attempt.Plan.Timeline);
        await MountPlanAsync(attempt, timeline.PlayerStartSeconds, paused);
        if (attempt.Media is not { } replacement) return null;
        var ready = await Task.WhenAny(replacement.Opening.Task, Task.Delay(15000, replacement.Token));
        replacement.Token.ThrowIfCancellationRequested();
        return ready == replacement.Opening.Task && await replacement.Opening.Task && IsCurrent(attempt, replacement)
            && !replacement.FailureQueued ? replacement : null;
    }

    private static async Task ReportSeekPositionAsync(Attempt attempt, double source, bool paused)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await attempt.Services!.Reporter.ReportProgressAsync(attempt.SessionId!, source, paused, deadline.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogFailure("seek progress", ex); }
    }
}
