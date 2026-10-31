using Bale.Core.Browse;

namespace Bale.Core.Tests;

public class CalendarFocusTests
{
    private static readonly DateOnly Monday = new(2026, 10, 26);

    private static CalendarWeek Week(params (int Day, string[] Ids)[] shelves) => new(Monday,
        Enumerable.Range(0, 7).Select(index => new CalendarDay(Monday.AddDays(index),
            (shelves.FirstOrDefault(shelf => shelf.Day == index).Ids ?? []).Select(id =>
                new CalendarItem(id, "episode", "Same series", null, "series", 1, 1, null, null, false, [], null)).ToArray())).ToArray());

    [Fact]
    public void ReturnUsesTheClickedEpisodeIdentityNotItsSharedSeriesOrOldIndex()
    {
        var week = Week((1, ["episode-2", "episode-1"]));
        var target = new CalendarReturnTarget(Monday.AddDays(1), "episode-1", 1, 0);

        Assert.Equal(new CalendarReturnLocation(1, 1), week.Locate(target));
    }

    [Fact]
    public void CorrectedAirDateFollowsTheEpisodeToAnotherDay()
    {
        var week = Week((1, ["episode-2"]), (5, ["episode-3", "episode-1"]));
        var target = new CalendarReturnTarget(Monday.AddDays(1), "episode-1", 1, 0);

        Assert.Equal(new CalendarReturnLocation(5, 1), week.Locate(target));
    }

    [Fact]
    public void RemovedEpisodeUsesTheNearestSurvivingIndexInItsDay()
    {
        var week = Week((1, ["episode-2", "episode-3"]), (5, ["episode-4"]));
        var target = new CalendarReturnTarget(Monday.AddDays(1), "removed", 1, 4);

        Assert.Equal(new CalendarReturnLocation(1, 1), week.Locate(target));
    }

    [Fact]
    public void EmptyDayFallsBackToTheNearestNonemptyShelf()
    {
        var week = Week((0, ["episode-2"]), (5, ["episode-3", "episode-4"]));
        var target = new CalendarReturnTarget(Monday.AddDays(4), "removed", 4, 1);

        Assert.Equal(new CalendarReturnLocation(5, 1), week.Locate(target));
    }

    [Fact]
    public void EmptyWeekReleasesFocusBackToTheControls()
    {
        var target = new CalendarReturnTarget(Monday.AddDays(4), "removed", 4, 1);

        Assert.Null(CalendarWeek.Empty(Monday).Locate(target));
    }
}
