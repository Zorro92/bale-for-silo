using System.Text.Json;
using Bale.Core.Detail;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>
/// The per-mount start budget (silo-apple b9446d5d) through the Rust handle, and the replan its failure sends. The
/// decision's own cases are Rust unit tests (start_budget.rs); these pin the wrapper, the bit layout and the races the
/// player page depends on.
/// </summary>
public class PlaybackStartBudgetTests
{
    private static readonly PlaybackStartFacts Opening = new();

    [Fact]
    public void TheDefaultBudgetIsAetherEnginesFortyFiveSeconds()
    {
        Assert.Equal(45_000, PlaybackStartBudget.BudgetMs);
        using var budget = new PlaybackStartBudget(1_000);
        Assert.Equal(45_000, budget.RemainingMs(1_000));
        Assert.False(budget.Update(45_999, Opening));
        Assert.True(budget.Update(46_000, Opening));
        Assert.Equal(0, budget.RemainingMs(46_000));
    }

    [Fact]
    public void ItRunsOutOnceAndNeverAfterwards()
    {
        using var budget = new PlaybackStartBudget(0, budgetMs: 1_000);
        Assert.True(budget.Update(1_000, Opening));
        Assert.False(budget.Update(1_100, Opening));
        // The open that finishes late is not a second outcome.
        Assert.False(budget.Update(1_200, new() { Opened = true, Started = true }));
    }

    [Fact]
    public void AnOpenThatCompletesAtTheSameMomentWins()
    {
        using var budget = new PlaybackStartBudget(0, budgetMs: 1_000);
        Assert.False(budget.Update(1_000, new() { Opened = true, Started = true }));
        Assert.False(budget.Update(5_000, Opening));
    }

    [Theory]
    [InlineData("failure queued")]
    [InlineData("back")]
    [InlineData("suspension")]
    public void AnEndedStartNeverFires(string reason)
    {
        Assert.NotNull(reason);
        using var budget = new PlaybackStartBudget(0, budgetMs: 1_000);
        Assert.False(budget.Update(1_000, new() { Ended = true }));
        Assert.False(budget.Update(9_000, Opening));
    }

    [Fact]
    public void ASeekInFlightDefersItUntilTheSeekLetsGo()
    {
        using var budget = new PlaybackStartBudget(0, budgetMs: 1_000);
        Assert.False(budget.Update(1_000, new() { Deferred = true }));
        Assert.False(budget.Update(3_000, new() { Deferred = true }));
        Assert.True(budget.Update(3_100, Opening));
    }

    [Fact]
    public void ASeekThatBringsTheFirstFrameEndsItAsReady()
    {
        using var budget = new PlaybackStartBudget(0, budgetMs: 1_000);
        Assert.False(budget.Update(2_000, new() { Deferred = true }));
        Assert.False(budget.Update(2_100, new() { Deferred = true, Opened = true, Started = true }));
        Assert.False(budget.Update(2_200, Opening));
    }

    [Fact]
    public void OfflineAndAPausedOpenedMountHoldIt()
    {
        using var budget = new PlaybackStartBudget(0, budgetMs: 1_000);
        Assert.False(budget.Update(500, new() { Offline = true }));
        Assert.False(budget.Update(60_000, new() { Offline = true }));
        Assert.False(budget.Update(60_000, new() { Opened = true, Paused = true }));
        Assert.False(budget.Update(120_000, new() { Opened = true, Paused = true }));
        // Opening ignores the viewer's intent: a pause before the source opened does not hold it.
        Assert.False(budget.Update(120_000, new() { Opened = true }));
        Assert.Equal(500, budget.RemainingMs(120_000));
        Assert.True(budget.Update(120_500, new() { Opened = true }));
    }

    [Fact]
    public void TheFailureIsApplesClassification()
    {
        var failure = PlaybackStartBudget.Failure;
        Assert.Equal("noPlayableTrackWithinBudget", failure.Classification);
        Assert.Equal("Playback didn't start within 45 seconds.", failure.Message);
        Assert.Null(failure.DecoderName);
        Assert.True(PlaybackStartBudget.Is(failure));
        Assert.False(PlaybackStartBudget.Is(null));
        Assert.False(PlaybackStartBudget.Is(PlaybackReplan.Classify(null, false, true)));
        Assert.False(PlaybackStartBudget.Is(PlaybackEmbeddedSubtitleFailure.Failure));
    }

    public static TheoryData<string, PlaybackRouteDecision?> FailedRoutes => new()
    {
        { "no route", null },
        { "system decoder", new PlaybackRouteDecision(true, false, "hevc", "eac3", 3, 1920, 1080) },
        { "software decode", new PlaybackRouteDecision(true, true, "av1", "eac3", 3, 1920, 1080) },
        { "console tone map", new PlaybackRouteDecision(true, false, "hevc", "eac3", 3, 3840, 2160, ConsoleToneMap: true) },
        { "console downscale", new PlaybackRouteDecision(true, false, "hevc", "eac3", 3, 3840, 2160, ConsoleDownscale: true) },
        { "windows reader", new PlaybackRouteDecision(false, false, "h264", "aac", 0, 1920, 1080) },
    };

    [Theory]
    [MemberData(nameof(FailedRoutes))]
    public void TheRecoveryExcludesTheRouteAndKeepsEveryClaim(string name, PlaybackRouteDecision? route)
    {
        Assert.NotNull(name);
        var start = Start(new PlaybackSelection(AudioIndex: 3)) with { QualityPreference = "auto" };
        var plan = PlaybackRoutePolicyTests.Plan("original_http", "hevc", "eac3");
        var request = PlaybackReplan.ForFailure(start, plan, "plan-attempt", ["prior"], 1, 42.5, PlaybackStartBudget.Failure, route);

        Assert.Equal("failure_recovery", request.Operation);
        Assert.Same(PlaybackStartBudget.Failure, request.Failure);
        Assert.Equal(new[] { "prior", plan.PlanAttemptKey! }, request.AttemptedPlanKeys);
        Assert.Equal(42.5, request.PositionSeconds);
        // A start that ran out of time is no evidence about a decoder, a console stage or an audio track.
        Assert.Same(start.ClientFeatures, request.ClientFeatures);
        Assert.Same(start.ClientCapabilities, request.ClientCapabilities);
        Assert.Same(start.ClientPlaybackContext, request.ClientPlaybackContext);
        Assert.Equal("auto", request.QualityPreference);
    }

    [Fact]
    public void AProgressiveMountThatRanOutIsNotOfferedAgain()
    {
        var start = Start();
        var plan = PlaybackRoutePolicyTests.Plan(PlaybackProgressive.Delivery, "hevc", "aac");
        var request = PlaybackReplan.ForFailure(start, plan, "plan-attempt", [], 1, 0, PlaybackStartBudget.Failure,
            new PlaybackRouteDecision(true, false, "hevc", "aac", 0, 1920, 1080));
        Assert.Same(PlaybackStartBudget.Failure, request.Failure);
        Assert.False(request.ClientPlaybackContext.Deliveries["progressive"].Enabled);
        Assert.Equal(start.ClientPlaybackContext.Deliveries["original_http"], request.ClientPlaybackContext.Deliveries["original_http"]);
        Assert.Same(start.ClientCapabilities, request.ClientCapabilities);
    }

    [Fact]
    public void TheRequestBodyCarriesTheClassification()
    {
        var request = PlaybackReplan.ForFailure(Start(), PlaybackRoutePolicyTests.Plan("hls", "h264", "aac"), "plan-attempt", [], 1, 7,
            PlaybackStartBudget.Failure, new PlaybackRouteDecision(true, false, "h264", "aac", 0, 1920, 1080));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, PlaybackJson.Default.PlaybackReplanRequestV3));
        var failure = json.RootElement.GetProperty("failure");
        Assert.Equal("noPlayableTrackWithinBudget", failure.GetProperty("classification").GetString());
        Assert.Equal("Playback didn't start within 45 seconds.", failure.GetProperty("message").GetString());
        Assert.False(failure.TryGetProperty("decoder_name", out var decoder) && decoder.ValueKind != JsonValueKind.Null);
    }
}
