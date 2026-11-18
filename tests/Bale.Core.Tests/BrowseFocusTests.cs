using Bale.Core.Browse;

namespace Bale.Core.Tests;

public class BrowseFocusTests
{
    private static MediaRow Row(string id, params string[] items) => new(id, id, "recently_added", false, CardShape.Poster,
        items.Select(item => new MediaCard(item, "movie", item, null, null, null, null, null, null, null, null, null, null, [], null, null, null)).ToList());

    [Fact]
    public void AContinueWatchingCardFollowsItsSeriesWhenTheNextEpisodeReplacesIt()
    {
        // Android 45a869871: progress rows key an episode by its series, so the advanced show keeps focus even
        // after it moves to the front of the row.
        MediaCard Episode(string id, string series) =>
            new MediaCard(id, "episode", id, null, null, null, null, null, null, null, null, null, null, [], null, null, null) { SeriesId = series };
        var before = new MediaRow("cw", "Continue Watching", "continue_watching", false, CardShape.Landscape,
            [Episode("a1", "a"), Episode("b4", "b")]);
        var target = new BrowseReturnTarget("cw", BrowseFocus.CardKey(before, before.Cards[1]), 0, 1);
        var after = before with { Cards = [Episode("b5", "b"), Episode("a1", "a")] };
        Assert.Equal(new BrowseReturnPosition(0, 0), BrowseFocus.Resolve([after], target));

        // Ordinary rows still key by content.
        var recent = Row("recent", "m1");
        Assert.Equal("m1", BrowseFocus.CardKey(recent, recent.Cards[0]));
    }

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
