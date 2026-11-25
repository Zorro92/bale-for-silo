using Bale.Core.Detail;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>
/// A direct play whose range stream gave up mid-title (PlaybackTransportStall): before this, FFmpeg read the failed read as
/// the end of the file and the title ended as if finished. The page compiles only on Windows, so its wiring is pinned by source.
/// </summary>
public class PlaybackTransportStallTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    [Fact]
    public void TheFailureIsAndroidsTransportStall()
    {
        var failure = PlaybackTransportStall.Failure;
        Assert.Equal("transport_stall", failure.Classification);
        Assert.True(PlaybackTransportStall.Is(failure));
        Assert.False(PlaybackTransportStall.Is(null));
        Assert.False(PlaybackTransportStall.Is(PlaybackReplan.Classify(null, false, true)));
        Assert.False(PlaybackTransportStall.Is(PlaybackStartBudget.Failure));
    }

    [Fact]
    public void OneSameRouteRemountRenewedAfterRecoveredPlayback()
    {
        Assert.True(PlaybackTransportStall.RemountsSamePlan(0, 0, 1_000));
        // A second stall soon after the remount goes to the replan.
        Assert.False(PlaybackTransportStall.RemountsSamePlan(1, 1_000, 1_000 + PlaybackTransportStall.RenewAfterMs));
        // One long after it is a new outage: a fresh same-route remount.
        Assert.True(PlaybackTransportStall.RemountsSamePlan(1, 1_000, 1_001 + PlaybackTransportStall.RenewAfterMs));
        Assert.Equal(0, PlaybackTransportStall.RemountsAfterRenewal(1, 1_000, 1_001 + PlaybackTransportStall.RenewAfterMs));
        Assert.Equal(1, PlaybackTransportStall.RemountsAfterRenewal(1, 1_000, 2_000));
    }

    [Theory]
    [MemberData(nameof(PlaybackStartBudgetTests.FailedRoutes), MemberType = typeof(PlaybackStartBudgetTests))]
    public void TheReplanExcludesTheRouteAndKeepsEveryClaim(string name, PlaybackRouteDecision? route)
    {
        Assert.NotNull(name);
        var start = Start(new PlaybackSelection(AudioIndex: 3)) with { QualityPreference = "auto" };
        var plan = PlaybackRoutePolicyTests.Plan("original_http", "hevc", "eac3");
        var request = PlaybackReplan.ForFailure(start, plan, "plan-attempt", ["prior"], 1, 42.5, PlaybackTransportStall.Failure, route);

        Assert.Equal("failure_recovery", request.Operation);
        Assert.Same(PlaybackTransportStall.Failure, request.Failure);
        Assert.Equal(new[] { "prior", plan.PlanAttemptKey! }, request.AttemptedPlanKeys);
        Assert.Equal(42.5, request.PositionSeconds);
        // An outage says nothing about a decoder, a console stage or an audio track.
        Assert.Same(start.ClientCapabilities, request.ClientCapabilities);
        Assert.Same(start.ClientPlaybackContext, request.ClientPlaybackContext);
    }

    [Fact]
    public void MediaEndedAfterAFailedRangeReadIsAStallNotTheEnd()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        var ended = media[media.IndexOf("private void OnMediaEnded(", StringComparison.Ordinal)..];
        ended = ended[..ended.IndexOf("private void EndAtEmptyTail(", StringComparison.Ordinal)];
        var stalled = ended.IndexOf("else if (media.Stream is { LastReadFailed: true })", StringComparison.Ordinal);
        var queued = ended.IndexOf("QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: true, PlaybackTransportStall.Failure);", StringComparison.Ordinal);
        var finished = ended.IndexOf("BufferingMediaEnded();", StringComparison.Ordinal);
        Assert.True(stalled > 0 && queued > stalled && finished > queued, "the stall check comes before the title's end");
    }

    [Fact]
    public void TheRecoveryRemountsOnceThenReplansKeepingTheClassification()
    {
        var player = Read("PlayerPage.cs.txt");
        var recover = player[player.IndexOf("private async Task RecoverCoreAsync(", StringComparison.Ordinal)..];
        var remount = recover.IndexOf("await RemountSamePlanAsync(attempt, position, \"player: range stream gave up; remounting the same plan\", failure);", StringComparison.Ordinal);
        var downscale = recover.IndexOf("if (attempt.Media is { Route.ConsoleDownscale: true } downscaled", StringComparison.Ordinal);
        Assert.True(remount > 0 && downscale > remount, "the stall remount runs before the claim-changing remounts");
        var same = player[player.IndexOf("private async Task RemountSamePlanAsync(", StringComparison.Ordinal)..];
        Assert.Contains("await RecoverCoreAsync(attempt, ex.HResult, isDecoder: false, IsNetworkFailure(ex), failure: failure);", same);
    }

    [Fact]
    public void ThePositionStaysWhereTheStreamGaveUp()
    {
        // Console 0.1.774: the engine's clock ran on to the title's end, so the remount and Retry started there and ended.
        var seek = Read("PlayerPage.Seek.cs.txt");
        Assert.Contains("private static bool StreamGaveUp(Mount media) => media.Stream is { LastReadFailed: true };", seek);
        Assert.Contains("if (attempt.Media is { Opened: true, Player: { } player } media && !StreamGaveUp(media)", seek);
        var present = Read("PlayerPage.Present.cs.txt");
        Assert.Contains("if (_attempt is { } current && !_engineReanchoring && !(current.Media is { } mounted && StreamGaveUp(mounted)))", present);
    }
}
