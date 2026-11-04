using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackMessagesTests
{
    [Fact]
    public void NoticesMatchTheOfficialTvPlayer()
    {
        Assert.Equal("Playback failed. Please try again.", PlaybackMessages.PlaybackFailed);
        Assert.Equal("Can't reach server — check your connection.", PlaybackMessages.ServerUnreachable);
        Assert.Equal("Playback start returned no session.", PlaybackMessages.NoSession);
    }

    [Theory]
    [InlineData(null, "Playback is unavailable right now.")]
    [InlineData("", "Playback is unavailable right now.")]
    [InlineData(" \t\n", "Playback is unavailable right now.")]
    [InlineData("  Transcoding is disabled. \n", "Transcoding is disabled.")]
    public void TerminalMessageIsTrimmedOrFallsBack(string? message, string expected) =>
        Assert.Equal(expected, PlaybackMessages.ServerTerminal(message));

    [Theory]
    [InlineData("No playable version.", "Failed to start playback: No playable version.")]
    [InlineData(null, "Failed to start playback: ")]
    public void StartFailuresUseTheOfficialPrefix(string? message, string expected) =>
        Assert.Equal(expected, PlaybackMessages.StartFailed(message));

    [Fact]
    public void PlayableRequiresBothASessionAndAPlan()
    {
        var result = PlaybackMessages.ValidatePlayable(new PlaybackDecision
        {
            Outcome = "playable", SessionId = "session", PlaybackPlan = new PlaybackPlan(),
        });
        Assert.True(result.IsPlayable);
        Assert.Null(result.Message);
        Assert.False(result.Retryable);
    }

    [Theory]
    [InlineData("playable", null, true)]
    [InlineData("playable", "", true)]
    [InlineData("playable", " \t", true)]
    [InlineData("playable", "session", false)]
    [InlineData(null, "session", true)]
    [InlineData("unknown", "session", true)]
    public void InvalidDecisionsUseNoSession(string? outcome, string? session, bool hasPlan)
    {
        var result = PlaybackMessages.ValidatePlayable(new PlaybackDecision
        {
            Outcome = outcome, SessionId = session, PlaybackPlan = hasPlan ? new PlaybackPlan() : null,
        });
        Assert.False(result.IsPlayable);
        Assert.Equal(PlaybackMessages.NoSession, result.Message);
        Assert.False(result.Retryable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void AdaptationUnavailablePreservesTheTerminalMessageAndRetryability(bool? retryable)
    {
        var result = PlaybackMessages.ValidatePlayable(new PlaybackDecision
        {
            Outcome = "adaptation_unavailable",
            Terminal = new TerminalV3 { Message = "  No compatible stream. \n", Retryable = retryable },
        });
        Assert.False(result.IsPlayable);
        Assert.Equal("No compatible stream.", result.Message);
        Assert.Equal(retryable == true, result.Retryable);
    }

    [Fact]
    public void MissingTerminalUsesTheFriendlyFallback()
    {
        var result = PlaybackMessages.ValidatePlayable(new PlaybackDecision { Outcome = "adaptation_unavailable" });
        Assert.False(result.IsPlayable);
        Assert.Equal("Playback is unavailable right now.", result.Message);
        Assert.False(result.Retryable);
    }
}
