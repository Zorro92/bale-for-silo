using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media.Playback;

namespace Bale.Xbox;

/// <summary>
/// The seek watchdog (<see cref="PlaybackSeekWatchdog"/>, Rust <c>seek_watchdog.rs</c>): a local seek (native or FFmpeg)
/// that has not landed within <see cref="PlaybackSeekWatchdog.BudgetMs"/> is not waited for any longer. AetherEngine's
/// native host re-anchors its producer at the target and re-seeks; its software host fails the item as
/// softwarePipelineFailed. Here the attempt's first stuck seek remounts the same plan, session and tracks at the target
/// (bounded by the new mount's start budget), and a later stuck seek fails the mount through the single recovery replan.
/// A seek_reanchor or forward-HLS remount is a new mount under its own start budget and is never watched.
/// <para>
/// The check runs on the UI tick and reads only the backend's clock, the device's connectivity and fields of the
/// mount: never an FFmpegInteropX member that takes the source lock, so a source wedged on its sample thread cannot
/// stop the watchdog that recovers from it.
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>The seek being watched: its mount, its target, and whether the backend reported it complete.</summary>
    private sealed class WatchedSeek(Attempt attempt, Mount media, PlaybackBackendSession session, double sourceTarget,
        bool paused)
    {
        private int _completed;
        public Attempt Attempt { get; } = attempt;
        public Mount Media { get; } = media;
        public PlaybackBackendSession Session { get; } = session;
        public double SourceTarget { get; } = sourceTarget;
        public bool Paused { get; } = paused;
        public bool Completed => Volatile.Read(ref _completed) != 0;
        /// <summary>The backend raises this on its own thread.</summary>
        public void OnSeekCompleted(PlaybackBackendSession sender, object? args) => Volatile.Write(ref _completed, 1);
    }

    private WatchedSeek? _watchedSeek;

    /// <summary>
    /// SeekAsync is about to move the player's clock to <paramref name="playerTarget"/>: watch it until it lands. A seek
    /// still watched is superseded and the newer one gets a full budget. Returns false only when the debug hang
    /// (<c>bale-debug.sh seek-watchdog hang=once</c>) swallows this seek, so it never reaches the source.
    /// </summary>
    private bool BeginWatchedSeek(Attempt attempt, Mount media, double playerTarget, double sourceTarget, bool paused)
    {
        try
        {
            StopWatchingSeek();
            var session = media.Player!.PlaybackSession;
            var watched = new WatchedSeek(attempt, media, session, sourceTarget, paused);
            // Subscribed before the clock moves: a backend may report the landing from inside the setter.
            session.SeekCompleted += watched.OnSeekCompleted;
            _watchedSeek = watched;
            attempt.SeekWatchdog ??= new PlaybackSeekWatchdog();
            attempt.SeekWatchdog.Begin(Environment.TickCount64, session.Position.TotalSeconds, playerTarget, SeekWatchdogBudgetMs);
#if DEBUG_SERVER
            if (DebugSeekWatchdog.TakeHang())
            {
                Log.Info("player: debug seek-watchdog hang: this seek never reaches the source");
                return false;
            }
#endif
        }
        catch (Exception ex) { LogFailure("seek watchdog", ex); }
        return true;
    }

    /// <summary>OnTick: spend or hold the watched seek's budget, and act once when it runs out.</summary>
    private void UpdateSeekWatchdog()
    {
        try
        {
            if (_watchedSeek is not { Attempt.SeekWatchdog: { } dog } watched) return;
            var attempt = watched.Attempt;
            var media = watched.Media;
            var now = Environment.TickCount64;
            // Back, suspension, a queued failure, or a reanchor, replan or track change that replaced the mount.
            var ended = !IsCurrent(attempt, media) || media.FailureQueued;
            var clock = double.NaN;
            if (!ended)
            {
                try { clock = watched.Session.Position.TotalSeconds; }
                catch (Exception ex) { LogFailure("seek watchdog clock", ex); }
            }
            var action = dog.Update(now, new PlaybackSeekFacts
            {
                Completed = watched.Completed,
                Ended = ended,
                Offline = ConnectionSpeedService.DeviceOffline,
                // A seek, track change or replan holds the player: let it finish (SeekAsync's own wait ends at 10 s).
                Deferred = _lifecycle.CurrentCount == 0,
            }, clock);
            if (dog.RemainingMs(now) is null) StopWatchingSeek();
            switch (action)
            {
                case PlaybackSeekWatchdogAction.Remount:
                    Log.Info($"player: seek did not land; remounting the same plan at the target route={RouteText(media)}");
                    _ = RemountStuckSeekAsync(attempt, media, watched.SourceTarget,
                        watched.Paused || watched.Session.PlaybackState == MediaPlaybackState.Paused);
                    break;
                case PlaybackSeekWatchdogAction.Fail:
                    Log.Info($"player: seek did not land after the remount; failure recovery route={RouteText(media)}");
                    attempt.StuckSeekTarget = watched.SourceTarget;
                    QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: false, PlaybackSeekWatchdog.Failure(media.Route.UseFfmpeg));
                    break;
            }
        }
        catch (Exception ex) { LogFailure("seek watchdog", ex); }
    }

    /// <summary>Drops the watched seek's completion handler. The decision itself forgets the seek on its own.</summary>
    private void StopWatchingSeek()
    {
        if (_watchedSeek is not { } watched) return;
        _watchedSeek = null;
        Cleanup("detach seek watchdog", () => watched.Session.SeekCompleted -= watched.OnSeekCompleted);
    }

    /// <summary>
    /// The attempt's first stuck seek: the same plan, session and tracks remounted at the seek's source target, as the
    /// forward-HLS seek remount does (MountPlanAsync keeps the HDMI mode and reloads the subtitle track). The old source
    /// closes on the thread pool (DisposeMount); the new mount has its own start budget, and an open that fails or runs
    /// out takes the ordinary single replan. Nothing is done when the mount changed while this waited for the player.
    /// </summary>
    private async Task RemountStuckSeekAsync(Attempt attempt, Mount media, double source, bool paused)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!IsCurrent(attempt, media) || media.FailureQueued) return;
            HideTransport();
            Pause(attempt);
            DisposeMount(attempt);
            attempt.Position = _sourcePosition = source;
            _state = "loading";
            SetLoading(true);
            var timeline = PlaybackTimeline.From(attempt.Plan!.Timeline);
            try
            {
                await MountPlanAsync(attempt, PlaybackProgressive.MountPlayerStart(attempt.Plan,
                    timeline.PlayerForSource(source) ?? timeline.PlayerStartSeconds), paused);
            }
            catch (Exception ex) when (!attempt.Cancellation.IsCancellationRequested)
            {
                LogFailure("open", ex);
                await RecoverCoreAsync(attempt, ex.HResult, isDecoder: false, IsNetworkFailure(ex));
            }
        }
        catch (OperationCanceledException) when (attempt.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogFailure("seek watchdog remount", ex);
            if (_active && !_leaving && !_suspended) await ShowErrorAsync(PlaybackMessages.PlaybackFailed);
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>A watched seek's budget: the default, or the debug override captured when the seek begins.</summary>
#if DEBUG_SERVER
    private static long SeekWatchdogBudgetMs => DebugSeekWatchdog.BudgetMs;
#else
    private static long SeekWatchdogBudgetMs => PlaybackSeekWatchdog.BudgetMs;
#endif

#if DEBUG_SERVER
    /// <summary>The watched seek for <c>bale-debug.sh seek-watchdog</c>.</summary>
    internal string DebugSeekWatchdogState
    {
        get
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            if (_attempt?.SeekWatchdog is not { } dog) return "seek: none remount_spent=False\n";
            var seek = dog.RemainingMs(Environment.TickCount64) is { } left && _watchedSeek is { } watched
                ? string.Create(invariant, $"remaining={left} ms target={watched.SourceTarget:0.###}")
                : "none";
            return string.Create(invariant, $"seek: {seek} remount_spent={dog.RemountSpent}\n");
        }
    }
#endif
}
