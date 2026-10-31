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
}
