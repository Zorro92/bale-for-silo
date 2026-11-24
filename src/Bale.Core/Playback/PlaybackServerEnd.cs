using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>
/// Why the server ended a playback session for good (PlaybackSessionLifecycle.kt PlaybackServerEnd, Android 56e23561a;
/// Apple 80ae9437; Roku 6389fcc). Progress and replans answer it with a 410 problem once the session is over, and only
/// for these two ends: any other stop answers 404 (silo-server playback_service.go stoppedAttemptError). Renewing or
/// replanning would undo the stop, so the player leaves with the notice and starts nothing.
/// </summary>
public enum PlaybackServerEnd
{
    /// <summary><c>playback_session_ended</c>: an administrator stopped a player that could not be told directly.</summary>
    Administrator,

    /// <summary>
    /// <c>playback_session_paused_timeout</c>: the session stayed paused past the server's limit
    /// (<c>playback.paused_session_timeout_minutes</c>, 15 by default, server dde5ed2). Only an attempt that advertised
    /// <see cref="PlaybackProtocol.PausedTimeoutFeature"/> gets this answer; others get <see cref="Administrator"/>'s.
    /// </summary>
    PausedTimeout,
}

public static class PlaybackServerEnds
{
    public const string EndedCode = "playback_session_ended";
    public const string PausedTimeoutCode = "playback_session_paused_timeout";

    /// <summary>The official wording: PlaybackCommandDispatch.kt ADMIN_ENDED_PLAYBACK_NOTICE (player.notice.adminEnded).</summary>
    public const string AdministratorNotice = "An administrator ended this playback session.";

    /// <summary>The official wording: player.notice.pausedTimeout (Android, Apple, Roku and the web share the key).</summary>
    public const string PausedTimeoutNotice = "Playback stopped because it was paused for too long.";

    // silo-server apiv2/problem.go ProblemTypeOrigin; the type's final segment is the problem code.
    private const string ProblemTypeOrigin = "https://siloserver.org/docs/api/v2/problems/";

    /// <summary>
    /// Why the server ended the session, or null when this failure does not say it did (ApiResult.playbackServerEnd):
    /// an HTTP 410 problem whose type is one of the two ends.
    /// </summary>
    public static PlaybackServerEnd? From(Exception? error) =>
        error is Problem { ResponseStatusCode: 410 } problem ? FromType(problem.Type) : null;

    internal static PlaybackServerEnd? FromType(string? type)
    {
        if (type?.StartsWith(ProblemTypeOrigin, StringComparison.Ordinal) != true) return null;
        return type[ProblemTypeOrigin.Length..] switch
        {
            PausedTimeoutCode => PlaybackServerEnd.PausedTimeout,
            EndedCode => PlaybackServerEnd.Administrator,
            _ => null,
        };
    }

    /// <summary>The notice the player leaves with (PlaybackServerEnd.exitNotice).</summary>
    public static string Notice(PlaybackServerEnd end) =>
        end == PlaybackServerEnd.PausedTimeout ? PausedTimeoutNotice : AdministratorNotice;

    /// <summary>A fixed log token for the end.</summary>
    public static string LogName(PlaybackServerEnd end) =>
        end == PlaybackServerEnd.PausedTimeout ? "paused_timeout" : "administrator";
}
