using Bale.Core.Playback;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

/// <summary>
/// The intro-skip prompt's conformance suite: silo-android IntroAutoSkipControllerTest.kt case for case (the server's
/// docs/design/2026-08-16-intro-skip-mode.md "Prompt behaviour" tables), on Bale's polled clock, plus the stall grace
/// the Android test leaves to settlingFalseEdges.
/// </summary>
public class IntroSkipPromptTests
{
    private static readonly MarkerRange Intro = new(30, 90);

    /// <summary>A player around the prompt: it seeks where an always skip asks, as the real players do.</summary>
    private sealed class Harness(IntroSkipMode mode, long promptMs = 5_000)
    {
        public IntroSkipPrompt Prompt { get; } = new(promptMs);
        public double Position { get; set; }
        public MarkerRange? Range { get; set; } = Intro;
        public IntroSkipMode Mode { get; set; } = mode;
        public string? Key { get; set; } = "file-1";
        public PromptActivity Activity { get; set; } = PromptActivity.Playing;
        public long Now { get; set; }
        public List<double> Seeks { get; } = [];

        public IntroPromptPill? Tick()
        {
            if (Prompt.Update(Position, Range, Key, Mode, Activity, Now) is { } target)
            {
                Seeks.Add(target);
                Position = target;
                Prompt.Update(Position, Range, Key, Mode, Activity, Now);
            }
            return Prompt.Pill;
        }

        public IntroPromptPill? At(double position)
        {
            Position = position;
            return Tick();
        }

        public IntroPromptPill? After(long ms)
        {
            Now += ms;
            return Tick();
        }
    }

    private static void Asking(IntroPromptPill? pill, long remainingMs)
    {
        Assert.NotNull(pill);
        Assert.Equal(IntroPromptKind.Skip, pill.Kind);
        Assert.Equal(remainingMs, pill.RemainingMs);
    }

    private static void Skipped(IntroPromptPill? pill, long remainingMs)
    {
        Assert.NotNull(pill);
        Assert.Equal(IntroPromptKind.Undo, pill.Kind);
        Assert.Equal(remainingMs, pill.RemainingMs);
    }

    [Fact]
    public void NeverEnteringAnIntroDoesNothingAtAll()
    {
        var player = new Harness(IntroSkipMode.Never, 3_000);
        Assert.Null(player.At(35));
        Assert.Null(player.After(10_000));
        Assert.Empty(player.Seeks);
    }

    [Fact]
    public void AskEnteringAnIntroOffersThePillAndItTicksDown()
    {
        var player = new Harness(IntroSkipMode.Ask, 3_000);
        Assert.Null(player.Tick());
        Asking(player.At(35), 3_000);
        Asking(player.After(1_000), 2_000);
        Asking(player.After(1_000), 1_000);
        Assert.Empty(player.Seeks);
    }

    [Fact]
    public void AskTheTimerRunningOutHidesThePillWithoutResolvingTheIntro()
    {
        var player = new Harness(IntroSkipMode.Ask, 3_000);
        player.At(35);
        Assert.Null(player.After(3_000));
        Assert.Empty(player.Seeks);
        // Still inside the same intro: the offer withdrew itself and must not come straight back.
        Assert.Null(player.At(40));
        // Scrubbing out and back in re-offers, with a full timer.
        Assert.Null(player.At(95));
        Asking(player.At(32), 3_000);
    }

    [Fact]
    public void AskSelectSeeksToTheEndResolvesAndDoesNotReOfferOnScrubBack()
    {
        var player = new Harness(IntroSkipMode.Ask);
        Asking(player.At(35), 5_000);
        Assert.Equal(Intro.End, player.Prompt.Select());
        Assert.Null(player.Prompt.Pill);
        // The caller performs the seek it was handed.
        Assert.Null(player.At(Intro.End));
        Assert.Null(player.At(40));
        Assert.Null(player.After(10_000));
        Assert.Empty(player.Seeks);
    }

    [Fact]
    public void AskBackDismissesThePillResolvesTheIntroAndReportsThePressConsumed()
    {
        var player = new Harness(IntroSkipMode.Ask);
        Asking(player.At(35), 5_000);
        Assert.True(player.Prompt.Dismiss());
        Assert.Null(player.Prompt.Pill);
        Assert.False(player.Prompt.Dismiss());
        Assert.Null(player.At(40));
        Assert.Null(player.After(10_000));
        Assert.Empty(player.Seeks);
    }

    [Fact]
    public void AskPauseFreezesTheTimerAndPlayResumesItFromTheSameValue()
    {
        var player = new Harness(IntroSkipMode.Ask);
        player.At(35);
        Asking(player.After(2_000), 3_000);
        player.Activity = PromptActivity.Paused;
        var paused = player.Tick();
        Asking(paused, 3_000);
        Assert.False(paused!.Running);
        Asking(player.After(10_000), 3_000);
        player.Activity = PromptActivity.Playing;
        var resumed = player.Tick();
        Asking(resumed, 3_000);
        Assert.True(resumed!.Running);
        Asking(player.After(1_000), 2_000);
        Assert.Null(player.After(2_000));
    }

    [Fact]
    public void AskTheTimerDoesNotStartUntilPlaybackIsActuallyRunning()
    {
        var player = new Harness(IntroSkipMode.Ask, 3_000) { Activity = PromptActivity.Paused };
        Assert.Null(player.At(35));
        Assert.Null(player.After(10_000));
        // A stall (a seek, a reload) is no start either.
        player.Activity = PromptActivity.Stalled;
        Assert.Null(player.After(500));
        player.Activity = PromptActivity.Playing;
        Asking(player.Tick(), 3_000);
    }

    [Fact]
    public void AskSeekingOutOfTheIntroHidesThePillWithoutResolvingIt()
    {
        var player = new Harness(IntroSkipMode.Ask);
        player.At(35);
        Asking(player.After(2_000), 3_000);
        Assert.Null(player.At(120));
        Assert.Null(player.After(10_000));
        Assert.Empty(player.Seeks);
        Asking(player.At(35), 5_000);
    }

    [Fact]
    public void AskADifferentIntroGetsItsOwnOffer()
    {
        var player = new Harness(IntroSkipMode.Ask, 3_000);
        player.At(35);
        player.Prompt.Dismiss();
        Assert.Null(player.Tick());
        player.Key = "file-2";
        Asking(player.Tick(), 3_000);
    }

    [Fact]
    public void AskALaterIntroUnderTheSameKeyGetsItsOwnOffer()
    {
        var player = new Harness(IntroSkipMode.Ask, 3_000);
        player.At(35);
        player.Prompt.Dismiss();
        player.Range = new MarkerRange(600, 660);
        Asking(player.At(605), 3_000);
        Assert.Equal(660, player.Prompt.Select());
    }

    [Fact]
    public void AlwaysTheUndoForALaterIntroSurvivesItsOwnSkip()
    {
        var player = new Harness(IntroSkipMode.Always, 3_000) { Range = new MarkerRange(600, 660) };
        Skipped(player.At(605), 3_000);
        Assert.Equal([660.0], player.Seeks);
        Assert.Equal(600, player.Prompt.Select());
    }

    [Fact]
    public void NoIntroKeyNothingIsEverOffered()
    {
        var player = new Harness(IntroSkipMode.Ask, 3_000) { Key = null };
        Assert.Null(player.At(35));
        Assert.Null(player.After(10_000));
        Assert.Empty(player.Seeks);
    }

    [Fact]
    public void AlwaysEnteringAnIntroSkipsItImmediatelyAndOffersTheUndo()
    {
        var player = new Harness(IntroSkipMode.Always, 3_000);
        Assert.Null(player.Tick());
        Skipped(player.At(35), 3_000);
        Assert.Equal([Intro.End], player.Seeks);
        Skipped(player.After(1_000), 2_000);
    }

    [Fact]
    public void AlwaysTheUndoPillIsAnchoredToTheIntroNotToThePosition()
    {
        var player = new Harness(IntroSkipMode.Always);
        Skipped(player.At(35), 5_000);
        Skipped(player.At(95), 5_000);
        Skipped(player.At(140), 5_000);
        Skipped(player.After(1_000), 4_000);
    }

    [Fact]
    public void AlwaysTheTimerRunningOutResolvesTheIntro()
    {
        var player = new Harness(IntroSkipMode.Always, 3_000);
        Skipped(player.At(35), 3_000);
        Assert.Null(player.After(3_000));
        Assert.Equal([Intro.End], player.Seeks);
        // Resolved: scrubbing back into it does not skip again.
        Assert.Null(player.At(35));
        Assert.Null(player.After(10_000));
        Assert.Equal([Intro.End], player.Seeks);
    }

    [Fact]
    public void AlwaysSelectSeeksBackToTheStartResolvesAndDoesNotSkipAgain()
    {
        var player = new Harness(IntroSkipMode.Always);
        Skipped(player.At(35), 5_000);
        Assert.Equal(Intro.Start, player.Prompt.Select());
        Assert.Null(player.Prompt.Pill);
        Assert.Null(player.At(Intro.Start));
        Assert.Null(player.At(45));
        Assert.Null(player.After(10_000));
        Assert.Equal([Intro.End], player.Seeks);
    }

    [Fact]
    public void AlwaysBackResolvesTheIntroAndPlaybackContinuesPastIt()
    {
        var player = new Harness(IntroSkipMode.Always);
        Skipped(player.At(35), 5_000);
        Assert.True(player.Prompt.Dismiss());
        Assert.Null(player.Prompt.Pill);
        Assert.False(player.Prompt.Dismiss());
        Assert.Null(player.At(35));
        Assert.Null(player.After(10_000));
        Assert.Equal([Intro.End], player.Seeks);
    }

    [Fact]
    public void AlwaysPauseFreezesTheUndoTimer()
    {
        var player = new Harness(IntroSkipMode.Always);
        player.At(35);
        Skipped(player.After(2_000), 3_000);
        player.Activity = PromptActivity.Paused;
        Skipped(player.Tick(), 3_000);
        Skipped(player.After(10_000), 3_000);
        player.Activity = PromptActivity.Playing;
        player.Tick();
        Skipped(player.After(1_000), 2_000);
    }

    [Fact]
    public void AskToNeverMidIntroTakesThePillDown()
    {
        var player = new Harness(IntroSkipMode.Ask);
        Asking(player.At(35), 5_000);
        player.Mode = IntroSkipMode.Never;
        Assert.Null(player.Tick());
        Assert.Null(player.After(10_000));
        Assert.Empty(player.Seeks);
    }

    [Fact]
    public void NeverToAskMidIntroOffersThePill()
    {
        var player = new Harness(IntroSkipMode.Never);
        Assert.Null(player.At(35));
        player.Mode = IntroSkipMode.Ask;
        Asking(player.Tick(), 5_000);
    }

    [Fact]
    public void AskToAlwaysMidIntroSkipsItThereAndThen()
    {
        var player = new Harness(IntroSkipMode.Ask);
        Asking(player.At(35), 5_000);
        player.Mode = IntroSkipMode.Always;
        Skipped(player.Tick(), 5_000);
        Assert.Equal([Intro.End], player.Seeks);
    }

    [Fact]
    public void ResetClearsResolvedIntrosSoNewContentStartsFresh()
    {
        var player = new Harness(IntroSkipMode.Ask, 3_000);
        player.At(35);
        player.Prompt.Dismiss();
        player.Prompt.Reset();
        Assert.Null(player.Prompt.Pill);
        Assert.Null(player.At(5));
        Asking(player.At(40), 3_000);
    }

    [Fact]
    public void SelectAndDismissAreNoOpsWhenNoPillIsShowing()
    {
        var player = new Harness(IntroSkipMode.Ask, 3_000);
        player.Tick();
        Assert.Null(player.Prompt.Select());
        Assert.False(player.Prompt.Dismiss());
        Assert.Null(player.Prompt.Pill);
    }

    [Fact]
    public void EachFreshOfferIsANewRunButATickIsNot()
    {
        // IntroAutoSkipController.countdownRun: the page focuses a new pill once, never on a tick.
        var player = new Harness(IntroSkipMode.Ask);
        var first = player.At(35)!.Run;
        Assert.Equal(first, player.After(1_000)!.Run);
        player.Activity = PromptActivity.Paused;
        Assert.Equal(first, player.After(1_000)!.Run);
        Assert.Null(player.At(120));
        player.Activity = PromptActivity.Playing;
        Assert.True(player.At(35)!.Run > first);
    }

    [Fact]
    public void AStallShorterThanTheGraceWindowIsIgnoredAndALongerOneFreezesTheTimer()
    {
        // TvPlayerViewModel.kt PLAYBACK_PAUSE_GRACE_MS: a rebuffer under 1.5 s does not touch the timer.
        var player = new Harness(IntroSkipMode.Ask, 10_000);
        player.At(35);
        Asking(player.After(1_000), 9_000);
        player.Activity = PromptActivity.Stalled;
        Asking(player.After(1_000), 8_000);
        player.Activity = PromptActivity.Playing;
        Asking(player.After(400), 7_600);
        // A long stall: the timer runs through the grace window, then holds what it had left as the window closed.
        player.Activity = PromptActivity.Stalled;
        Asking(player.After(1_000), 6_600);
        var stalled = player.After(1_000);
        Asking(stalled, 5_600);
        Assert.True(stalled!.Running);
        var frozen = player.After(1_000);
        Asking(frozen, 5_100);
        Assert.False(frozen!.Running);
        Asking(player.After(5_000), 5_100);
        player.Activity = PromptActivity.Playing;
        Asking(player.Tick(), 5_100);
        Asking(player.After(4_500), 600);
        Assert.Null(player.After(600));
    }

    [Fact]
    public void TheFillStartsEmptyAndLandsFullAsTheTimerRunsOut()
    {
        var player = new Harness(IntroSkipMode.Ask, 4_000);
        Assert.Equal(0, player.At(35)!.Progress);
        Assert.Equal(0.25, player.After(1_000)!.Progress, 6);
        Assert.Equal(0.75, player.After(2_000)!.Progress, 6);
        Assert.Null(player.After(1_000));
    }
}
