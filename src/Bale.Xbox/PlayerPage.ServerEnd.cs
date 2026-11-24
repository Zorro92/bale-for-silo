using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Xbox;

/// <summary>
/// The server ended this session for good (TvPlayerViewModel.kt leaveServerEndedSession, Android 56e23561a; Apple 80ae9437;
/// Roku 6389fcc): an administrator stopped it (410 playback_session_ended), or it stayed paused past the server's limit
/// (410 playback_session_paused_timeout, which the start's paused_timeout_v1 asks for). Progress, every replan and the seek
/// reanchor answer it. The player leaves as Back does, with the official notice over the page it returns to, and starts
/// nothing: no replan, renewal, error screen or Up Next. The exit's stop settles the journal against the server's replayed
/// receipt.
/// </summary>
public sealed partial class PlayerPage
{
    private void LeaveServerEnded(Attempt attempt, PlaybackServerEnd end)
    {
        if (!_active || _leaving || !ReferenceEquals(_attempt, attempt)) return;
        Log.Info($"player: server ended the session reason={PlaybackServerEnds.LogName(end)}; leaving");
        ExitNotice.Show(PlaybackServerEnds.Notice(end));
        _ = StopAndGoBackAsync();
    }

    /// <summary>A failure that says the server ended the session: leave (above) and return true, so the caller starts nothing.</summary>
    private bool LeftForServerEnd(Attempt attempt, Exception error)
    {
        if (PlaybackServerEnds.From(error) is not { } end) return false;
        LeaveServerEnded(attempt, end);
        return true;
    }
}
