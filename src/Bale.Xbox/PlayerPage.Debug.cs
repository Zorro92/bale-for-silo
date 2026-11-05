#if DEBUG_SERVER
using System.Globalization;
using Bale.Xbox.Services;
using Windows.Media.Playback;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    internal async Task<(string Status, string Body)> DebugSeekAsync(DebugPlaybackSeek request)
    {
        // Share teardown/replan serialization, but do not queue a debug seek behind an opening player.
        if (!_lifecycle.Wait(0)) return ("409 Conflict", "player is changing its playback pipeline\n");
        try
        {
            if (_attempt is not { Media: { Opened: true, FailureQueued: false, Player: { } player } media } attempt
                || !IsCurrent(attempt, media) || !player.PlaybackSession.CanSeek)
                return ("409 Conflict", "no active seekable player pipeline\n");
            if (request.PlayerPosition(media.Transport.Timeline, _duration) is not { } target)
                return ("400 Bad Request", "position is outside the current source timeline\n");

            var session = player.PlaybackSession;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnSeekCompleted(MediaPlaybackSession sender, object args) => completed.TrySetResult(true);
            session.SeekCompleted += OnSeekCompleted;
            try
            {
                media.FrameDelivery?.SetSeeking(true);
                if (request.Pause) player.Pause();
                if (Math.Abs(session.Position.TotalSeconds - target) > 0.000001)
                {
                    session.Position = TimeSpan.FromSeconds(target);
                    var winner = await Task.WhenAny(completed.Task, Task.Delay(10000, media.Token));
                    media.Token.ThrowIfCancellationRequested();
                    if (winner != completed.Task) return ("408 Request Timeout", "player seek did not complete within 10 seconds\n");
                }
                if (!IsCurrent(attempt, media) || media.FailureQueued)
                    return ("409 Conflict", "player changed during seek\n");
                if (request.Pause) player.Pause();
                var source = SourcePosition(attempt);
                UpdatePlaybackState(attempt, media);
                RefreshStats(attempt);
                var body = string.Create(CultureInfo.InvariantCulture,
                    $"Position: source={source:0.###}s player={session.Position.TotalSeconds:0.###}s state={session.PlaybackState}\n");
                Log.Info("player: debug seek " + body.TrimEnd());
                return ("200 OK", body);
            }
            finally
            {
                session.SeekCompleted -= OnSeekCompleted;
                media.FrameDelivery?.SetSeeking(false);
            }
        }
        catch (OperationCanceledException) { return ("409 Conflict", "player stopped during seek\n"); }
        catch (Exception ex)
        {
            LogFailure("debug seek", ex);
            return ("500 Internal Server Error", $"seek failed hr=0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally { _lifecycle.Release(); }
    }
}
#endif
