using System.Text.Json;
using Bale.Core.Detail;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>
/// The seek watchdog through the Rust handle, and the replan its failure sends. The decision's own cases are Rust unit
/// tests (seek_watchdog.rs); these pin the wrapper, the bit layout and the races the player page depends on.
/// </summary>
public class PlaybackSeekWatchdogTests
{
    private static readonly PlaybackSeekFacts Waiting = new();
    private const double From = 600, Target = 630;

    [Fact]
    public void TheDefaultIsFifteenSecondsAndTheFirstStuckSeekRemounts()
    {
        Assert.Equal(15_000, PlaybackSeekWatchdog.BudgetMs);
        using var dog = new PlaybackSeekWatchdog();
        Assert.Null(dog.RemainingMs(0));
        dog.Begin(1_000, From, Target);
        Assert.Equal(15_000, dog.RemainingMs(1_000));
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(15_999, Waiting, From));
        Assert.False(dog.RemountSpent);
        Assert.Equal(PlaybackSeekWatchdogAction.Remount, dog.Update(16_000, Waiting, From));
        Assert.True(dog.RemountSpent);
        Assert.Null(dog.RemainingMs(16_000));
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(60_000, Waiting, From));
    }

    [Fact]
    public void ASecondStuckSeekFailsAndNothingLoops()
    {
        using var dog = new PlaybackSeekWatchdog();
        dog.Begin(0, From, Target, budgetMs: 1_000);
        Assert.Equal(PlaybackSeekWatchdogAction.Remount, dog.Update(1_000, Waiting, From));
        dog.Begin(5_000, Target, Target + 30, budgetMs: 1_000);
        Assert.Equal(PlaybackSeekWatchdogAction.Fail, dog.Update(6_000, Waiting, Target));
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(9_000, Waiting, Target));
    }

    [Fact]
    public void ACompletionAtTheDeadlineWins()
    {
        using var dog = new PlaybackSeekWatchdog();
        dog.Begin(0, From, Target, budgetMs: 1_000);
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(1_000, new() { Completed = true }, From));
        Assert.False(dog.RemountSpent);
        Assert.Null(dog.RemainingMs(1_000));
    }

    [Fact]
    public void TheClockAtTheTargetLandsTheSeekWithoutTheEvent()
    {
        using var dog = new PlaybackSeekWatchdog();
        dog.Begin(0, From, Target, budgetMs: 1_000);
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(1_000, Waiting, Target + 1));
        Assert.Null(dog.RemainingMs(1_000));
        // A frozen clock never does, and an unknown one is not evidence.
        dog.Begin(2_000, From, Target, budgetMs: 1_000);
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(2_500, Waiting, double.NaN));
        Assert.Equal(PlaybackSeekWatchdogAction.Remount, dog.Update(3_000, Waiting, From));
    }

    [Fact]
    public void ANewerSeekRestartsTheWatch()
    {
        using var dog = new PlaybackSeekWatchdog();
        dog.Begin(0, From, Target, budgetMs: 1_000);
        dog.Begin(900, From, Target + 30, budgetMs: 1_000);
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(1_000, Waiting, From));
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(1_899, Waiting, From));
        Assert.Equal(PlaybackSeekWatchdogAction.Remount, dog.Update(1_900, Waiting, From));
    }

    [Theory]
    [InlineData("back")]
    [InlineData("suspension")]
    [InlineData("failure queued")]
    [InlineData("replaced by a reanchor, replan or track change")]
    public void AnEndedMountDropsTheWatchUnspent(string reason)
    {
        Assert.NotNull(reason);
        using var dog = new PlaybackSeekWatchdog();
        dog.Begin(0, From, Target, budgetMs: 1_000);
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(1_000, new() { Ended = true }, From));
        Assert.False(dog.RemountSpent);
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(10_000, Waiting, From));
    }

    [Fact]
    public void AnOutageHoldsAndAnotherChangeDefers()
    {
        using var dog = new PlaybackSeekWatchdog();
        dog.Begin(0, From, Target, budgetMs: 1_000);
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(500, new() { Offline = true }, From));
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(100_000, new() { Offline = true }, From));
        Assert.Equal(500, dog.RemainingMs(100_000));
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(100_000, Waiting, From));
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(100_500, new() { Deferred = true }, From));
        Assert.Equal(PlaybackSeekWatchdogAction.None, dog.Update(105_000, new() { Deferred = true }, From));
        Assert.Equal(PlaybackSeekWatchdogAction.Remount, dog.Update(105_001, Waiting, From));
    }

    [Fact]
    public void TheFailureIsAethersRepositionFailurePerRoute()
    {
        var software = PlaybackSeekWatchdog.Failure(ffmpeg: true);
        var native = PlaybackSeekWatchdog.Failure(ffmpeg: false);
        Assert.Equal("softwarePipelineFailed", software.Classification);
        Assert.Equal("nativeItemFailed", native.Classification);
        Assert.Equal("Playback source could not reposition.", software.Message);
        Assert.True(PlaybackSeekWatchdog.Is(software));
        Assert.True(PlaybackSeekWatchdog.Is(native));
        Assert.False(PlaybackSeekWatchdog.Is(null));
        Assert.False(PlaybackSeekWatchdog.Is(PlaybackStartBudget.Failure));
        Assert.False(PlaybackSeekWatchdog.Is(new("softwarePipelineFailed", "Some other pipeline failure.")));
        Assert.False(PlaybackStartBudget.Is(software));
    }

    [Theory]
    [MemberData(nameof(PlaybackStartBudgetTests.FailedRoutes), MemberType = typeof(PlaybackStartBudgetTests))]
    public void TheRecoveryExcludesTheRouteAndKeepsEveryClaim(string name, PlaybackRouteDecision? route)
    {
        Assert.NotNull(name);
        var start = Start(new PlaybackSelection(AudioIndex: 3)) with { QualityPreference = "auto" };
        var plan = PlaybackRoutePolicyTests.Plan("original_http", "hevc", "eac3");
        var failure = PlaybackSeekWatchdog.Failure(route?.UseFfmpeg == true);
        var request = PlaybackReplan.ForFailure(start, plan, "plan-attempt", ["prior"], 1, 630, failure, route);

        Assert.Equal("failure_recovery", request.Operation);
        Assert.Same(failure, request.Failure);
        Assert.Equal(new[] { "prior", plan.PlanAttemptKey! }, request.AttemptedPlanKeys);
        Assert.Equal(630, request.PositionSeconds);
        // A pipeline that stopped repositioning says nothing about one codec, console stage or audio track.
        Assert.Same(start.ClientFeatures, request.ClientFeatures);
        Assert.Same(start.ClientCapabilities, request.ClientCapabilities);
        Assert.Same(start.ClientPlaybackContext, request.ClientPlaybackContext);
        Assert.Equal("auto", request.QualityPreference);
    }

    [Fact]
    public void TheRequestBodyCarriesTheClassification()
    {
        var request = PlaybackReplan.ForFailure(Start(), PlaybackRoutePolicyTests.Plan("original_http", "av1", "aac"), "plan-attempt", [], 1, 7,
            PlaybackSeekWatchdog.Failure(ffmpeg: true), new PlaybackRouteDecision(true, true, "av1", "aac", 0, 1920, 1080));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, PlaybackJson.Default.PlaybackReplanRequestV3));
        var failure = json.RootElement.GetProperty("failure");
        Assert.Equal("softwarePipelineFailed", failure.GetProperty("classification").GetString());
        Assert.Equal("Playback source could not reposition.", failure.GetProperty("message").GetString());
        Assert.False(failure.TryGetProperty("decoder_name", out var decoder) && decoder.ValueKind != JsonValueKind.Null);
    }
}
