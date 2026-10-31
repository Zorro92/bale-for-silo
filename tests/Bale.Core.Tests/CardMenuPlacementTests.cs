using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class CardMenuPlacementTests
{
    [Theory]
    [InlineData(300, 400, 100, 960, 540, 276, 136, 212, 256)]
    [InlineData(300, 144, 100, 960, 540, 276, 136, 212, 0)]
    [InlineData(300, 143, 100, 960, 540, 276, 136, 212, 151)]
    [InlineData(300, 40, 180, 960, 540, 276, 95, 252, 48)]
    [InlineData(0, 300, 88, 960, 540, 276, 136, 0, 156)]
    [InlineData(900, 300, 88, 960, 540, 276, 136, 684, 156)]
    [InlineData(300, 140, 88, 960, 200, 276, 136, 206, 64)]
    [InlineData(20, 20, 88, 200, 100, 276, 136, 0, 0)]
    [InlineData(-100, -20, 88, 960, 540, 276, 136, 0, -12)]
    public void MatchesTheOfficialZeroHeightAnchorAndWindowClamps(double left, double top, double anchorWidth,
        double windowWidth, double windowHeight, double menuWidth, double menuHeight, double expectedX, double expectedY)
    {
        var (x, y) = CardMenuPlacement.Position(left, top, anchorWidth, windowWidth, windowHeight, menuWidth, menuHeight);

        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
    }

    [Theory]
    [InlineData(3, 136)]
    [InlineData(4, 177)]
    [InlineData(5, 218)]
    public void RowCountIncludesOnlyEightPixelPaddingAndThreePixelGaps(int rows, double height)
    {
        var panelHeight = 16 + rows * 38 + (rows - 1) * 3;
        Assert.Equal(height, panelHeight);
        Assert.Equal((212d, 400 - height - 8), CardMenuPlacement.Position(300, 400, 100, 960, 540, 276, panelHeight));
    }
}
