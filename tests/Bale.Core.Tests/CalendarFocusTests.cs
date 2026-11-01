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
    public void DownFromTheTopBarTargetsTheSelectedFilter() =>
        Assert.Equal(new CalendarFocusTarget(CalendarFocusRegion.Filters, CalendarFocusEntry.Selected),
            CalendarFocus.Move(Week(), CalendarFocusRegion.TopBar, 1));

    [Fact]
    public void DownFromTheFiltersTargetsTheSelectedDay() =>
        Assert.Equal(new CalendarFocusTarget(CalendarFocusRegion.WeekStrip, CalendarFocusEntry.Selected),
            CalendarFocus.Move(Week((0, ["episode-1"])), CalendarFocusRegion.Filters, 1));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void DownFromTheStripAlwaysTargetsTheFirstCardOfTheFirstNonemptyDay(int firstDay)
    {
        var week = Week((firstDay, ["episode-1", "episode-2", "episode-3"]));

        // Even a previously visited shelf must use First here, not its remembered Rail entry.
        Assert.Equal(new CalendarFocusTarget(CalendarFocusRegion.DayRow, CalendarFocusEntry.First, firstDay),
            CalendarFocus.Move(week, CalendarFocusRegion.WeekStrip, 1));
    }

    [Fact]
    public void StripEntryUsesTheFirstDayNotTheSelectedDaysShelf()
    {
        var week = Week((1, ["episode-1"]), (5, ["episode-2"]));

        Assert.Equal(new CalendarFocusTarget(CalendarFocusRegion.DayRow, CalendarFocusEntry.First, 1),
            CalendarFocus.Move(week, CalendarFocusRegion.WeekStrip, 1));
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(2, 1, 6)]
    [InlineData(6, -1, 2)]
    [InlineData(2, -1, 0)]
    public void VerticalShelfMovesSkipEmptyDaysAndUseTheDestinationRailsMemory(int from, int step, int to)
    {
        var week = Week((0, ["episode-1", "episode-2"]), (2, ["episode-3"]), (6, ["episode-4", "episode-5"]));

        // Rail, not Calendar, chooses this row's last card or its first if never focused.
        Assert.Equal(new CalendarFocusTarget(CalendarFocusRegion.DayRow, CalendarFocusEntry.Remembered, to),
            CalendarFocus.Move(week, CalendarFocusRegion.DayRow, step, from));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void UpFromTheFirstNonemptyDayTargetsTheSelectedDay(int firstDay) =>
        Assert.Equal(new CalendarFocusTarget(CalendarFocusRegion.WeekStrip, CalendarFocusEntry.Selected),
            CalendarFocus.Move(Week((firstDay, ["episode-1"])), CalendarFocusRegion.DayRow, -1, firstDay));

    [Theory]
    [InlineData(CalendarFocusRegion.WeekStrip, CalendarFocusRegion.Filters)]
    [InlineData(CalendarFocusRegion.Filters, CalendarFocusRegion.TopBar)]
    public void MovingUpThroughControlsAlwaysUsesSelectionNeverRememberedEntry(CalendarFocusRegion from, CalendarFocusRegion to) =>
        Assert.Equal(new CalendarFocusTarget(to, CalendarFocusEntry.Selected),
            CalendarFocus.Move(Week((0, ["episode-1"])), from, -1));

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void DownAtTheLastShelfStaysPutInsteadOfMovingSideways(int lastDay) =>
        Assert.Null(CalendarFocus.Move(Week((lastDay, ["episode-1", "episode-2"])), CalendarFocusRegion.DayRow, 1, lastDay));

    [Fact]
    public void AnEmptyWeekOffersStateActionsAndUpReturnsToTheSelectedDay()
    {
        Assert.Equal(new CalendarFocusTarget(CalendarFocusRegion.StateActions, CalendarFocusEntry.First),
            CalendarFocus.Move(Week(), CalendarFocusRegion.WeekStrip, 1));
        Assert.Equal(new CalendarFocusTarget(CalendarFocusRegion.WeekStrip, CalendarFocusEntry.Selected),
            CalendarFocus.Move(Week(), CalendarFocusRegion.StateActions, -1));
        Assert.Null(CalendarFocus.Move(Week(), CalendarFocusRegion.StateActions, 1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void AnInvalidShelfHasNoGeometricFallback(int dayIndex) =>
        Assert.Null(CalendarFocus.Move(Week((0, ["episode-1"])), CalendarFocusRegion.DayRow, 1, dayIndex));

    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(2)]
    public void OnlyVerticalStepsHaveTargets(int step) =>
        Assert.Null(CalendarFocus.Move(Week((0, ["episode-1"])), CalendarFocusRegion.WeekStrip, step));

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
