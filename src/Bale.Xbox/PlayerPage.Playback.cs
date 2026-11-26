using System.Globalization;
using Bale.Xbox.Services;
using Windows.Media.Playback;

namespace Bale.Xbox;

internal enum PlaybackControlRequest { Play, Pause, Toggle }

/// <summary>
/// Play/Pause for every route (controller and remote keys, the system media controls, the debug
/// server): one path that waits for the state, then keeps the spinner, display requests, stats and
/// the position report on the normal state path.
/// </summary>
public sealed partial class PlayerPage
{
    internal async Task<(string Status, string Body)> SetPlaybackAsync(PlaybackControlRequest request)
    {
        // Do not queue transport behind an open, replan or seek, or let it race a remount.
        if (!_lifecycle.Wait(0)) return ("409 Conflict", "player is changing its playback pipeline\n");
        try
        {
            if (_attempt is not { Attached: true, Media: { FailureQueued: false, Player: { } player } media } attempt
                || !IsCurrent(attempt, media)) return ("409 Conflict", "no active player pipeline\n");
            var session = player.PlaybackSession;
            // Paused/Playing can be transient during opening. Started also accepts an HLS
            // reanchor deliberately opened paused, without accepting buffering-from-open.
            if (!media.Opened || !media.Started || session.PlaybackState is not
                (MediaPlaybackState.Playing or MediaPlaybackState.Paused or MediaPlaybackState.Buffering))
                return ("409 Conflict", "player is still loading or buffering from open\n");

            var paused = request == PlaybackControlRequest.Pause
                || request == PlaybackControlRequest.Toggle && session.PlaybackState != MediaPlaybackState.Paused;
            var target = paused ? MediaPlaybackState.Paused : MediaPlaybackState.Playing;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStateChanged(PlaybackBackendSession sender, object? args)
            {
                // Native callbacks may run off-thread; bookkeeping stays on the UI continuation.
                try
                {
                    if (sender.PlaybackState == target) completed.TrySetResult(true);
                    else if (sender.PlaybackState is MediaPlaybackState.None or MediaPlaybackState.Opening)
                        completed.TrySetResult(false);
                }
                catch (Exception ex) { completed.TrySetException(ex); }
            }
            session.PlaybackStateChanged += OnStateChanged;
            try
            {
                if (session.PlaybackState != target)
                {
                    // The teardown-only Pause helper swallows failures, so use Play/Pause here and
                    // the shared state/report paths below.
                    if (paused) player.Pause();
                    else player.Play();
                }
                if (session.PlaybackState != target)
                {
                    var winner = await Task.WhenAny(completed.Task, Task.Delay(10000, media.Token));
                    media.Token.ThrowIfCancellationRequested();
                    if (winner != completed.Task)
                        return ("408 Request Timeout", "player did not change playback state within 10 seconds\n");
                    if (!await completed.Task) return ("409 Conflict", "player stopped during playback control\n");
                }
                if (!IsCurrent(attempt, media) || media.FailureQueued)
                    return ("409 Conflict", "player stopped during playback control\n");
                // Keep display requests, spinner and subtitle clocks on the normal state path.
                UpdatePlaybackState(attempt, media);
                var source = SourcePosition(attempt);
#if DEBUG_SERVER
                // The stats pane is debug-only (PlayerPage.Stats.cs).
                RefreshStats(attempt);
#endif
                await ReportPositionAsync(attempt, source, paused);
                if (!IsCurrent(attempt, media) || media.FailureQueued)
                    return ("409 Conflict", "player stopped during playback control\n");
                return ("200 OK", string.Create(CultureInfo.InvariantCulture,
                    $"Position: source={source:0.###}s player={session.Position.TotalSeconds:0.###}s state={session.PlaybackState}\n"));
            }
            finally { session.PlaybackStateChanged -= OnStateChanged; }
        }
        catch (OperationCanceledException) { return ("409 Conflict", "player stopped during playback control\n"); }
        catch (Exception ex)
        {
            LogFailure("playback control", ex);
            return ("500 Internal Server Error", $"playback failed hr=0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally { _lifecycle.Release(); }
    }
}
