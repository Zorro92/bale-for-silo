using Bale.Core.Text;

namespace Bale.Core.Tests;

public class FlowLayoutTests
{
    [Fact]
    public void WrapsOnlyWhenTheNextItemWouldPassTheWidth()
    {
        var (slots, width, height) = FlowLayout.Arrange([(140, 38), (200, 38), (130, 38)], 450, 11, 11);
        Assert.Equal([0, 0, 1], slots.Select(slot => slot.Line));
        Assert.Equal(151, slots[1].X);
        Assert.Equal((0d, 49d), (slots[2].X, slots[2].Y));
        Assert.Equal(351, width);
        Assert.Equal(87, height);

        var (oneLine, _, lineHeight) = FlowLayout.Arrange([(200, 38), (130, 38)], 450, 11, 11);
        Assert.All(oneLine, slot => Assert.Equal(0, slot.Line));
        Assert.Equal(38, lineHeight);
        Assert.Equal(0, FlowLayout.Arrange([(500, 38)], 450, 11, 11).Slots[0].Line);
        Assert.Equal(0, FlowLayout.Arrange([], 450, 11, 11).Height);
    }
}
