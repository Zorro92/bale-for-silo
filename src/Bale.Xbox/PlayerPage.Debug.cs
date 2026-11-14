#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media.Playback;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    internal async Task<(string Status, string Body)> DebugSetPlaybackAsync(DebugPlaybackControl request)
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

            var paused = request == DebugPlaybackControl.Pause
                || request == DebugPlaybackControl.Toggle && session.PlaybackState != MediaPlaybackState.Paused;
            var target = paused ? MediaPlaybackState.Paused : MediaPlaybackState.Playing;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStateChanged(MediaPlaybackSession sender, object args)
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
                    // There is no user transport handler yet. The teardown-only Pause helper
                    // swallows failures, so use Play/Pause here and the shared state/report paths below.
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
                RefreshStats(attempt);
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
            LogFailure("debug playback", ex);
            return ("500 Internal Server Error", $"playback failed hr=0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally { _lifecycle.Release(); }
    }

    internal async Task<(string Status, string Body)> DebugSeekAsync(DebugPlaybackSeek request)
    {
        var result = await SeekAsync(new PlaybackSeek(request.Seconds, request.Pause));
        return result.Outcome switch
        {
            SeekOutcome.Completed => ("200 OK", string.Create(CultureInfo.InvariantCulture,
                $"Position: source={result.Source:0.###}s player={result.Player:0.###}s state={result.State}\n")),
            SeekOutcome.Busy => ("409 Conflict", "player is changing its playback pipeline\n"),
            SeekOutcome.Unavailable => ("409 Conflict", "no active seekable player pipeline or supported reanchor\n"),
            SeekOutcome.InvalidPosition => ("400 Bad Request", "position is outside the current source timeline\n"),
            SeekOutcome.TimedOut => ("408 Request Timeout", "player seek did not complete within 10 seconds\n"),
            SeekOutcome.Stopped => ("409 Conflict", "player stopped during seek\n"),
            _ => ("500 Internal Server Error", $"seek failed hr=0x{unchecked((uint)result.HResult):X8}\n"),
        };
    }
}
#endif
