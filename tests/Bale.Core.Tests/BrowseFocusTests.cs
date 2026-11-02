using Bale.Core.Browse;

namespace Bale.Core.Tests;

public class BrowseFocusTests
{
    private static MediaRow Row(string id, params string[] items) => new(id, id, "recently_added", false, CardShape.Poster,
        items.Select(item => new MediaCard(item, "movie", item, null, null, null, null, null, null, null, null, null, null, [], null, null, null)).ToList());

    [Fact]
    public void RowAndCardReorderingFollowsIdentityWithinTheOriginalSection()
    {
        var target = new BrowseReturnTarget("recent", "m3", 0, 2);
        var rows = new[] { Row("progress", "m3"), Row("recent", "m3", "m2", "m1") };
        Assert.Equal(new BrowseReturnPosition(1, 0), BrowseFocus.Resolve(rows, target));
    }

    [Fact]
    public void RemovingACardUsesItsNearestSurvivingSlotNotACopyInAnotherRow()
    {
        var rows = new[] { Row("progress", "m3"), Row("recent", "m1", "m2") };
        Assert.Equal(new BrowseReturnPosition(1, 1), BrowseFocus.Resolve(rows, new("recent", "m3", 1, 2)));
    }

    [Fact]
    public void RemovingASectionClampsIndicesWithoutFollowingAnOverlappingTitle()
    {
        var rows = new[] { Row("recent", "m3", "m2", "m1") };
        Assert.Equal(new BrowseReturnPosition(0, 2), BrowseFocus.Resolve(rows, new("progress", "m3", 4, 2)));
    }

    [Fact]
    public void EmptyRowsAndDuplicateCardsUseTheRenderedCoordinateSpace()
    {
        var rows = new[] { Row("empty"), Row("recent", "m1", "m1", "m2") };
        Assert.Equal(new BrowseReturnPosition(0, 1), BrowseFocus.Resolve(rows, new("recent", "m2", 0, 1)));
    }

    [Fact]
    public void ExploreAllRemainsTheTargetWhenTheRowChangesLength()
    {
        var row = Row("recent", "m1", "m2", "m3") with { ExploreSource = new("recent", "Recent") };
        Assert.Equal(new BrowseReturnPosition(0, 3), BrowseFocus.Resolve([row], new("recent", BrowseFocus.ExploreAllKey, 0, 1)));
        Assert.Equal(new BrowseReturnPosition(0, 2), BrowseFocus.Resolve([row with { ExploreSource = null }], new("recent", BrowseFocus.ExploreAllKey, 0, 3)));
    }

    [Fact]
    public void EmptyFeedHasNoTargetAndNewEntryStartsAtTheFirstCard()
    {
        Assert.Null(BrowseFocus.Resolve([], new("recent", "m1", 0, 0)));
        Assert.Equal(new BrowseReturnPosition(0, 0), BrowseFocus.Resolve([Row("recent", "m1")], null));
    }
}
