using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

public sealed record PlaybackValidationResult(bool IsPlayable, string? Message = null, bool Retryable = false);

/// <summary>TV player notices from TvPlayerViewModel.kt, TvVideoPlaybackStarter.kt and VideoPlaybackStartResult.kt.</summary>
public static class PlaybackMessages
{
    public const string PlaybackFailed = "Playback failed. Please try again.";
    public const string ServerUnreachable = "Can't reach server — check your connection.";
    public const string NoSession = "Playback start returned no session.";

    public static string ServerTerminal(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "Playback is unavailable right now." : message.Trim();

    public static string StartFailed(string? message) => $"Failed to start playback: {message}";

    public static PlaybackValidationResult ValidatePlayable(PlaybackDecision d) => d.Outcome switch
    {
        "playable" when !string.IsNullOrWhiteSpace(d.SessionId) && d.PlaybackPlan is not null => new(true),
        "adaptation_unavailable" => new(false, ServerTerminal(d.Terminal?.Message), d.Terminal?.Retryable == true),
        _ => new(false, NoSession),
    };
}
