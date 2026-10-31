using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class CardRailMetricsTests
{
    [Theory]
    [InlineData(88, 132, RailFocusVisual.Card, 10, 11)]
    [InlineData(180, 100, RailFocusVisual.Card, 12, 10)]
    [InlineData(100, 100, RailFocusVisual.Card, 10, 10)]
    [InlineData(130, 195, RailFocusVisual.Card, 11, 13)]
    [InlineData(138, 207, RailFocusVisual.Card, 11, 13)]
    [InlineData(180, 101, RailFocusVisual.Episode, 5, 4)]
    [InlineData(124, 186, RailFocusVisual.Calendar, 8, 10)]
    [InlineData(235, 180, RailFocusVisual.Trailer, 7, 6)]
    public void EveryCardShapeHasEnoughRoomForItsExistingFocusVisual(double width, double height,
        RailFocusVisual visual, double horizontal, double vertical)
    {
        var (padding, margin) = CardRailMetrics.Layout(width, height, new RailInsets(0, 0, 0, 0), visual);

        Assert.Equal(new RailInsets(horizontal, vertical, horizontal, vertical), padding);
        Assert.Equal(new RailInsets(0, -vertical, 0, -vertical), margin);
    }

    [Fact]
    public void RelatedPostersGainHeadroomWithoutMovingTheHeaderArtOrNextSection()
    {
        var inset = new RailInsets(50, 0, 50, 24);
        var (padding, margin) = CardRailMetrics.Layout(130, 195, inset);

        Assert.Equal(new RailInsets(50, 13, 50, 24), padding);
        Assert.Equal(new RailInsets(0, -13, 0, 0), margin);
        Assert.Equal(inset.Top, margin.Top + padding.Top);
        Assert.Equal(inset.Bottom, padding.Bottom + margin.Bottom);
    }

    [Theory]
    [InlineData(88, 132)]
    [InlineData(180, 100)]
    public void HomeKeepsItsRestingRowHeightButAddsSpaceInsideBothViewportEdges(double width, double height)
    {
        var inset = new RailInsets(44, 7, 0, 7);
        var (padding, margin) = CardRailMetrics.Layout(width, height, inset);

        Assert.Equal(44, padding.Left);
        Assert.Equal(inset.Top, padding.Top + margin.Top);
        Assert.Equal(inset.Bottom, padding.Bottom + margin.Bottom);
        Assert.True(padding.Right >= CardRailMetrics.Headroom(width));
        Assert.Equal(0, margin.Right); // Negative right margins would lose the last card to the page clip.
    }

    [Fact]
    public void ExistingLargerInsetsAreNotReduced()
    {
        var inset = new RailInsets(50, 12, 50, 12);
        var (padding, margin) = CardRailMetrics.Layout(100, 100, inset);

        Assert.Equal(inset, padding);
        Assert.Equal(new RailInsets(0, 0, 0, 0), margin);
    }

    [Theory]
    [InlineData(132)]
    [InlineData(195)]
    [InlineData(207)]
    [InlineData(400)]
    public void HeadroomContainsTheScaledGlowNotJustTheUnscaledRing(double height)
    {
        var focusedTop = height * (CardRailMetrics.ArtScale - 1) / 2
            + CardRailMetrics.GlowOutset * CardRailMetrics.ArtScale;
        var headroom = CardRailMetrics.Headroom(height);

        Assert.True(headroom >= focusedTop + CardRailMetrics.SafetyMargin);
        Assert.True(headroom < focusedTop + CardRailMetrics.SafetyMargin + 1);
    }
}
