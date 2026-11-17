using Bale.Core.Api.Models;
using Bale.Core.Browse;

namespace Bale.Core.Tests;

/// <summary>CardWatchStatus.of: a count only for a started, unfinished series or season.</summary>
public class CardWatchStatusTests
{
    [Fact]
    public void EpisodesLeftOnlyForAStartedUnfinishedShow()
    {
        Assert.Equal(7, BrowseFeed.EpisodesLeft(new CatalogItemUserState { WatchedCount = 3, UnplayedCount = 7 }));
        Assert.Null(BrowseFeed.EpisodesLeft(new CatalogItemUserState { WatchedCount = 0, UnplayedCount = 10 }));
        Assert.Null(BrowseFeed.EpisodesLeft(new CatalogItemUserState { WatchedCount = 10, UnplayedCount = 0 }));
        Assert.Null(BrowseFeed.EpisodesLeft(new CatalogItemUserState { Played = true, WatchedCount = 3, UnplayedCount = 7 }));
        Assert.Null(BrowseFeed.EpisodesLeft(null));
    }
}
