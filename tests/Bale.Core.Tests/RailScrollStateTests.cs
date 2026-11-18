using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class RailScrollStateTests
{
    [Fact]
    public void HeldHorizontalKeysAreDiscardedUntilTheAnimationSettles()
    {
        var state = new RailScrollState();
        Assert.False(state.ShouldDrop(1, wasKeyDown: true));

        Assert.Equal(108, state.Begin(0, 108, 800));
        // ChangeView is asynchronous: repeats must be blocked even before the first ViewChanging.
        Assert.True(state.ShouldDrop(1, wasKeyDown: true));
        Assert.True(state.ShouldDrop(-1, wasKeyDown: true));
        state.ViewChanged(isIntermediate: true);
        Assert.True(state.ShouldDrop(1, wasKeyDown: true));
        state.ViewChanged(isIntermediate: false);
        Assert.False(state.ShouldDrop(1, wasKeyDown: true));
    }

    [Fact]
    public void HeldStepsKeepTheAnimationPaceWhereTheRowCannotScroll()
    {
        var state = new RailScrollState();
        // Mid-row: the first animation measures the pace.
        state.Stepped(0);
        Assert.Equal(108, state.Begin(0, 108, 800, nowMs: 0));
        Assert.True(state.ShouldDrop(1, wasKeyDown: true, nowMs: 100));
        state.ViewChanged(isIntermediate: false, nowMs: 220);
        Assert.Equal(220, state.PaceMs);
        Assert.False(state.ShouldDrop(1, wasKeyDown: true, nowMs: 230));
        state.Stepped(230);
        // Near the end the pin is clamped: nothing animates, but a held key still waits for the pace.
        Assert.Null(state.Begin(800, 908, 800, nowMs: 230));
        Assert.True(state.ShouldDrop(1, wasKeyDown: true, nowMs: 300));
        Assert.False(state.ShouldDrop(1, wasKeyDown: true, nowMs: 450));
        // A new press is never consumed, whatever the pace.
        Assert.False(state.ShouldDrop(1, wasKeyDown: false, nowMs: 240));
        Assert.False(state.ShouldDrop(0, wasKeyDown: true, nowMs: 240));
    }

    [Fact]
    public void ThePaceStaysWithinItsBounds()
    {
        var state = new RailScrollState();
        Assert.Equal(RailScrollState.DefaultPaceMs, state.PaceMs);
        state.Begin(0, 108, 800, nowMs: 0);
        state.ViewChanged(isIntermediate: false, nowMs: 5);
        Assert.Equal(RailScrollState.MinPaceMs, state.PaceMs);
        state.Begin(0, 108, 800, nowMs: 0);
        state.ViewChanged(isIntermediate: false, nowMs: 5000);
        Assert.Equal(RailScrollState.MaxPaceMs, state.PaceMs);
        // An animation without a start time (an older caller) leaves the pace alone.
        state.Begin(0, 108, 800);
        state.ViewChanged(isIntermediate: false, nowMs: 100);
        Assert.Equal(RailScrollState.MaxPaceMs, state.PaceMs);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    public void NewPressesAndVerticalMovesAreNeverConsumed(int horizontalStep, bool wasKeyDown)
    {
        var state = new RailScrollState();
        state.Begin(0, 108, 800);
        Assert.False(state.ShouldDrop(horizontalStep, wasKeyDown));
    }

    [Theory]
    [InlineData(0, -108, 800)]
    [InlineData(800, 908, 800)]
    [InlineData(0, 108, 0)]
    [InlineData(108, 108.5, 800)]
    public void AClampedOrSubpixelPinDoesNotLockTheRow(double current, double requested, double maximum)
    {
        var state = new RailScrollState();
        Assert.Null(state.Begin(current, requested, maximum));
        Assert.False(state.IsAnimating);
    }

    [Theory]
    [InlineData(108, -108, 800, 0)]
    [InlineData(700, 908, 800, 800)]
    public void PinTargetsStayWithinTheScrollableExtent(double current, double requested, double maximum, double expected)
    {
        var state = new RailScrollState();
        Assert.Equal(expected, state.Begin(current, requested, maximum));
        Assert.True(state.IsAnimating);
    }

    [Fact]
    public void AutomaticHorizontalScrollAlsoGatesHeldKeysAndUnloadResetsIt()
    {
        var state = new RailScrollState();
        state.ViewChanging(0, 0);
        Assert.False(state.IsAnimating);
        state.ViewChanging(0, 108);
        Assert.True(state.ShouldDrop(1, wasKeyDown: true));
        state.Reset();
        Assert.False(state.ShouldDrop(1, wasKeyDown: true));
    }

    [Fact]
    public void NoOpFocusDoesNotPrematurelyEndAnExistingAnimation()
    {
        var state = new RailScrollState();
        state.Begin(0, 108, 800);
        Assert.Null(state.Begin(0, 0, 800));
        Assert.True(state.IsAnimating);
    }

    [Fact]
    public void TheRowsEndKeepsTheWholeHeldCycleNotJustTheAnimation()
    {
        var state = new RailScrollState();
        // A press, then held repeats: focus work (100 ms) + animation (200 ms) + the wait for the next repeat.
        state.Stepped(0, held: false);
        state.Begin(0, 108, 2000, nowMs: 100);
        state.ViewChanged(isIntermediate: false, nowMs: 300);
        Assert.Equal(200, state.PaceMs);
        // The first repeat carries the repeat delay: it must not set the pace.
        state.Stepped(600, held: true);
        Assert.Equal(200, state.PaceMs);
        state.Begin(108, 216, 2000, nowMs: 700);
        state.ViewChanged(isIntermediate: false, nowMs: 900);
        // An animated step is gated by its animation only, never by the pace.
        Assert.False(state.ShouldDrop(1, wasKeyDown: true, nowMs: 920));
        state.Stepped(933, held: true);
        Assert.Equal(333, state.PaceMs);
        // A later animation alone never shortens the measured cycle.
        state.Begin(216, 324, 2000, nowMs: 1033);
        state.ViewChanged(isIntermediate: false, nowMs: 1233);
        Assert.Equal(333, state.PaceMs);
        state.Stepped(1266, held: true);
        // Clamped near the end: nothing animates, and held steps keep the 333 ms cycle.
        Assert.Null(state.Begin(2000, 2108, 2000, nowMs: 1366));
        Assert.True(state.ShouldDrop(1, wasKeyDown: true, nowMs: 1500));
        Assert.False(state.ShouldDrop(1, wasKeyDown: true, nowMs: 1600));
    }

    [Fact]
    public void AScrollCutShortByTheRowsEndNeverSetsThePace()
    {
        var state = new RailScrollState();
        state.Stepped(0, held: true);
        state.Begin(0, 400, 2000, nowMs: 50);
        state.ViewChanged(isIntermediate: false, nowMs: 300);
        state.Stepped(333, held: true);
        Assert.Equal(333, state.PaceMs);
        // The last scroll only reaches the end (asked 2400, clamped to 2000) and is quicker.
        Assert.Equal(2000, state.Begin(1800, 2400, 2000, nowMs: 383));
        state.ViewChanged(isIntermediate: false, nowMs: 480);
        state.Stepped(513, held: true);
        Assert.Equal(333, state.PaceMs);
    }
}
