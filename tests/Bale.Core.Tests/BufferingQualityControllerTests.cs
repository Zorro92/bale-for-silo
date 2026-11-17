using Bale.Core.Playback;
using Bale.Core.Settings;
using static Bale.Core.Tests.BufferingQualityHarness;

namespace Bale.Core.Tests;

/// <summary>
/// Rules the vectors do not reach (pill timers, competing surfaces, summary figures), ported from silo-apple's
/// BufferingQualityControllerTests.swift, plus the thresholds each mode acts on.
/// </summary>
public sealed class BufferingQualityControllerTests
{
    private static BufferingQualityReplan AutoLowering(int estimate) =>
        new("quality_change", "auto") { BandwidthEstimateKbps = estimate, BandwidthEstimateSource = "observed_rebuffer" };

    /// <summary>The vectors' undo case: a long stall lowers Auto at 131000 and the undo pill shows at 134000.</summary>
    private static BufferingQualityHarness LoweredAutomatically()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Always);
        h.StartPlaying(Plan("p1"));
        h.At(125_000, c => c.Waiting());
        h.At(134_000, c =>
        {
            c.PlanAdopted(Plan("p2", delivered: 31_600));
            c.Playing();
        });
        return h;
    }

    [Fact]
    public void ALongStallLowersAutoAtSixSeconds()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Always);
        h.StartPlaying(Plan("p1"));
        h.At(125_000, c => c.Waiting());
        h.Clock.AdvanceTo(130_999);
        Assert.Empty(h.Replans);
        h.Clock.AdvanceTo(131_000);
        Assert.Equal([AutoLowering(44_100)], h.Replans);
        Assert.Equal(44_100, h.Controller.KeptEstimateKbps);
    }

    [Fact]
    public void TwoCountedStallsWithinTwoMinutesLower()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Always);
        h.StartPlaying(Plan("p1"));
        h.At(30_000, c => c.Waiting());
        h.At(31_000, c => c.Playing());
        Assert.Empty(h.Replans);
        h.At(149_000, c => c.Waiting());
        h.At(150_000, c => c.Playing());
        Assert.Equal([AutoLowering(44_100)], h.Replans);
    }

    [Fact]
    public void StallsUnderOneSecondOrInsideTheGraceNeverCount()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Always);
        h.StartPlaying(Plan("p1"));
        // The first 10 s of a plan are its startup grace.
        h.At(2_000, c => c.Waiting());
        h.At(9_500, c => c.Playing());
        h.At(30_000, c => c.Waiting());
        h.At(30_999, c => c.Playing());
        h.At(40_000, c => c.Waiting());
        h.At(40_900, c => c.Playing());
        h.At(50_000, c => c.PlanEnded());
        Assert.Empty(h.Replans);
        Assert.Equal(0, h.Summaries.Single().StallCount);
    }

    [Fact]
    public void AskFirstPromptsAndLowerQualityLowers()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Ask);
        h.StartPlaying(Plan("p1"));
        h.At(125_000, c => c.Waiting());
        h.Clock.AdvanceTo(131_000);
        Assert.Equal(BufferingPillKind.Prompt, h.Controller.Pill?.Kind);
        Assert.Empty(h.Replans);
        h.At(132_000, c => c.SelectLower());
        Assert.Null(h.Controller.Pill);
        Assert.Equal([AutoLowering(44_100)], h.Replans);
        h.At(134_000, c => c.PlanAdopted(Plan("p2", delivered: 31_600)));
        Assert.Contains(h.Shown, item => item.Output is BufferingNoticeOutput { Notice: BufferingQualityNotice.Lowered });
        Assert.Equal(BufferingAction.Accepted, h.Summaries.Single(summary => summary.PlanId == "p1").Action);
    }

    [Fact]
    public void NeverOnlyReportsTheTrigger()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Never);
        h.StartPlaying(Plan("p1"));
        h.At(125_000, c => c.Waiting());
        h.At(140_000, c => c.Playing());
        h.At(150_000, c => c.PlanEnded());
        Assert.Empty(h.Shown);
        Assert.Equal(BufferingAction.Ignored, h.Summaries.Single().Action);
    }

    [Fact]
    public void RestoreQualityReplansAutoWithLearnedLimitClear()
    {
        var h = LoweredAutomatically();
        Assert.Equal(BufferingPillKind.Undo, h.Controller.Pill?.Kind);
        h.At(140_000, c => c.SelectPrimary());
        Assert.Equal(new BufferingQualityReplan("quality_change", "auto") { LearnedLimit = "clear" }, h.Replans[^1]);
        Assert.True(h.Replans[^1].IsRestore);
        Assert.Null(h.Controller.KeptEstimateKbps);
        Assert.Null(h.Controller.Pill);
    }

    [Fact]
    public void WithoutTheFeatureRestoreSendsNoClear()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Always) { SendsBufferingQualityFields = false };
        h.StartPlaying(Plan("p1"));
        h.At(125_000, c => c.Waiting());
        h.At(134_000, c =>
        {
            c.PlanAdopted(Plan("p2", delivered: 31_600));
            c.Playing();
        });
        h.At(140_000, c => c.SelectRestore());
        Assert.Equal([new("quality_change", "auto") { BandwidthEstimateKbps = 44_100 }, new BufferingQualityReplan("quality_change", "auto")], h.Replans);
    }

    [Fact]
    public void ClientReplansCarryTheKeptEstimateWithoutASource()
    {
        var h = LoweredAutomatically();
        Assert.Equal(new BufferingQualityReplan("track_change", "auto") { BandwidthEstimateKbps = 44_100 },
            h.Controller.ClientReplan("track_change"));
        h.Controller.NewAttempt();
        Assert.Equal(new BufferingQualityReplan("seek_reanchor", "auto"), h.Controller.ClientReplan("seek_reanchor"));
    }

    [Fact]
    public void ALoweringBeforeAnythingPlayedNeedsEvidenceAndSendsIt()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Always) { MeasuredKbps = null };
        h.At(0, c => c.PlanAdopted(Plan("p1")));
        h.At(0, c => c.Waiting());
        h.Clock.AdvanceTo(30_000);
        Assert.Empty(h.Replans);

        var slow = new BufferingQualityHarness(BufferingQualityMode.Always) { MeasuredKbps = 20_000 };
        slow.At(0, c => c.PlanAdopted(Plan("p1")));
        slow.At(0, c => c.Waiting());
        slow.Clock.AdvanceTo(16_000);
        Assert.Equal(AutoLowering(44_100) with { ObservedThroughputKbps = 20_000 }, slow.Replans.Single());

        // Media arriving at or above the bitrate is no evidence: the start runs on to its own failure path.
        var fast = new BufferingQualityHarness(BufferingQualityMode.Always) { MeasuredKbps = 80_000 };
        fast.At(0, c => c.PlanAdopted(Plan("p1")));
        fast.At(0, c => c.Waiting());
        fast.Clock.AdvanceTo(30_000);
        Assert.Empty(fast.Replans);
    }

    [Fact]
    public void PauseFreezesThePillTimerAndPlayResumesIt()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Ask);
        h.StartPlaying(Plan("p1"));
        h.At(125_000, c => c.Waiting());
        h.Clock.AdvanceTo(131_000);
        Assert.Equal(146_000, h.Controller.Pill?.DeadlineMs);
        h.At(136_000, c => c.Paused());
        Assert.Null(h.Controller.Pill?.DeadlineMs);
        Assert.Equal(10_000, h.Controller.Pill?.RemainingMs);
        h.Clock.AdvanceTo(600_000);
        Assert.Equal(BufferingPillKind.Prompt, h.Controller.Pill?.Kind);
        h.At(600_000, c => c.Playing());
        Assert.Equal(610_000, h.Controller.Pill?.DeadlineMs);
        h.Clock.AdvanceTo(609_999);
        Assert.NotNull(h.Controller.Pill);
        h.Clock.AdvanceTo(610_000);
        Assert.Null(h.Controller.Pill);
    }

    [Fact]
    public void ACompetingSurfaceHidesThePillAndBringsItBackWithItsTimeLeft()
    {
        var h = LoweredAutomatically();
        h.At(139_000, c => c.SetCompetingPillVisible(true));
        Assert.Null(h.Controller.Pill);
        Assert.True(h.Controller.PillPending);
        Assert.False(h.Controller.Back(), "a hidden pill does not consume Back");
        h.At(200_000, c => c.SetCompetingPillVisible(false));
        Assert.Equal(BufferingPillKind.Undo, h.Controller.Pill?.Kind);
        Assert.Equal(210_000, h.Controller.Pill?.DeadlineMs);
        Assert.Equal(2, h.Shown.Count(item => item.Output is BufferingPillShownOutput));
    }

    [Fact]
    public void BackIsConsumedOnlyByAVisiblePill()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Ask);
        Assert.False(h.Controller.Back());
        h.StartPlaying(Plan("p1"));
        h.At(125_000, c => c.Waiting());
        h.Clock.AdvanceTo(131_000);
        Assert.True(h.Controller.Back());
        Assert.Null(h.Controller.Pill);
        Assert.False(h.Controller.Back(), "a second Back behaves normally");
    }

    [Fact]
    public void AStallTheSourceSwapEndsCountsInItsPlan()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Always);
        h.StartPlaying(Plan("p1"));
        h.At(125_000, c => c.Waiting());
        h.At(133_000, c => c.Waiting(BufferingWaitCause.Reload));
        h.At(134_000, c =>
        {
            c.PlanAdopted(Plan("p2", delivered: 31_600));
            c.Playing();
        });
        Assert.Equal([1], h.Summaries.Select(summary => summary.StallCount));
        Assert.Equal([8_000L], h.Summaries.Select(summary => summary.StallTotalMs));

        h.At(200_000, c => c.Waiting());
        h.At(202_000, c => c.Waiting(BufferingWaitCause.Reload));
        h.At(203_000, c => c.Playing());
        h.At(210_000, c => c.PlanEnded());
        Assert.Equal([1, 0], h.Summaries.Select(summary => summary.StallCount));
    }

    [Fact]
    public void TheSummaryCarriesPlayedTimeStallsAndTheAction()
    {
        var h = LoweredAutomatically();
        var first = h.Summaries.Single();
        Assert.Equal(new BufferingPlanSummary("p1", 125_000, 1, 9_000, 9_000, 63_000, BufferingQualityMode.Always,
            BufferingAction.Lowered, null), first);

        h.At(200_000, c => c.Waiting());
        h.At(203_500, c => c.Playing());
        h.At(260_000, c => c.Paused());
        h.At(300_000, c => c.PlanEnded());
        var second = h.Summaries[1];
        Assert.Equal("p2", second.PlanId);
        Assert.Equal((200_000 - 134_000) + (260_000 - 203_500), second.PlayedMs);
        Assert.Equal(1, second.StallCount);
        Assert.Equal(3_500, second.StallTotalMs);
        Assert.Equal(31_600, second.DeliveredKbps);
    }

    [Fact]
    public void RunningSummaryDoesNotEndThePlan()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Always);
        h.StartPlaying(Plan("p1"));
        h.At(50_000, c => c.Waiting());
        h.Clock.AdvanceTo(52_000);
        var running = h.Controller.RunningSummary!;
        Assert.Equal(50_000, running.PlayedMs);
        Assert.Equal(1, running.StallCount);
        Assert.Equal(2_000, running.StallTotalMs);
        Assert.Empty(h.Summaries);
        Assert.Equal("p1", h.Controller.CurrentPlanId);
    }

    [Fact]
    public void AnOutageStallNeverCountsNorLowers()
    {
        var h = new BufferingQualityHarness(BufferingQualityMode.Always);
        h.StartPlaying(Plan("p1"));
        h.At(29_000, c => c.TransportTroubleChanged(true));
        h.At(30_000, c => c.Waiting());
        h.At(60_000, c => c.TransportTroubleChanged(false));
        h.At(61_000, c => c.Playing());
        h.At(70_000, c => c.PlanEnded());
        Assert.Empty(h.Replans);
        Assert.Equal(1_000, h.Summaries.Single().StallTotalMs);
        Assert.Equal(1, h.Summaries.Single().StallCount);
    }

    [Theory]
    [InlineData(63_000, 44_100)]
    [InlineData(22_120, 15_484)]
    [InlineData(143, 100)]
    [InlineData(142, null)]
    [InlineData(0, null)]
    [InlineData(null, null)]
    public void StepDownIsTheExactIntegerSevenTenths(int? delivered, int? expected) =>
        Assert.Equal(expected, BufferingQualityController.StepDownEstimate(delivered));

    [Fact]
    public void LowerRungIsBelowTheNamedRungAndTheDeliveredBitrate()
    {
        var plan = Plan("p1", delivered: 8_000, quality: "1080p-high") with { ActiveQuality = "1080p-high" };
        Assert.Equal("1080p-medium", BufferingQualityController.LowerRung(plan)?.Label);
        Assert.Equal("1080p-high", BufferingQualityController.LowerRung(Plan("p2"))?.Label);
        Assert.Null(BufferingQualityController.LowerRung(Plan("p3", delivered: null)));
    }

    [Fact]
    public void ThePolledClockFiresDueTimersInOrderAndForgetsCancelledOnes()
    {
        long now = 0;
        var clock = new PolledBufferingQualityClock(() => now);
        var fired = new List<string>();
        clock.Schedule(200, () => fired.Add("b"));
        clock.Schedule(100, () => fired.Add("a"));
        var cancelled = clock.Schedule(150, () => fired.Add("x"));
        cancelled.Dispose();
        clock.Schedule(200, () => fired.Add("c"));
        now = 150;
        clock.Fire();
        Assert.Equal(["a"], fired);
        now = 500;
        clock.Fire();
        Assert.Equal(["a", "b", "c"], fired);
        Assert.Equal(0, clock.Pending);
        Assert.Null(clock.NextDueMs);
    }
}
