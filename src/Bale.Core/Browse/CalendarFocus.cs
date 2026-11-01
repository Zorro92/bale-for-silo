namespace Bale.Core.Browse;

public enum CalendarFocusRegion
{
    TopBar,
    Filters,
    WeekStrip,
    DayRow,
    StateActions,
}

public enum CalendarFocusEntry
{
    Selected,
    First,
    Remembered,
}

public sealed record CalendarFocusTarget(CalendarFocusRegion Region, CalendarFocusEntry Entry, int DayIndex = -1);

/// <summary>
/// Calendar deliberately follows Apple TV's CalendarFilterBar.swift, CalendarWeekStrip.swift and
/// CalendarDayShelf.swift (silo-apple PR #527), not Android TV's geometric focus. Between shelves,
/// Bale keeps each Rail's remembered card rather than resetting every shelf to its first card.
/// </summary>
public static class CalendarFocus
{
    /// <summary>Only the destination row is chosen here; its Rail owns remembered-item entry.</summary>
    public static CalendarFocusTarget? Move(CalendarWeek week, CalendarFocusRegion from, int step, int dayIndex = -1) => (from, step) switch
    {
        (CalendarFocusRegion.TopBar, 1) => new(CalendarFocusRegion.Filters, CalendarFocusEntry.Selected),
        (CalendarFocusRegion.Filters, -1) => new(CalendarFocusRegion.TopBar, CalendarFocusEntry.Selected),
        (CalendarFocusRegion.Filters, 1) => new(CalendarFocusRegion.WeekStrip, CalendarFocusEntry.Selected),
        (CalendarFocusRegion.WeekStrip, -1) => new(CalendarFocusRegion.Filters, CalendarFocusEntry.Selected),
        (CalendarFocusRegion.WeekStrip, 1) => FirstDay(week),
        (CalendarFocusRegion.DayRow, -1 or 1) => AdjacentDay(week, dayIndex, step),
        (CalendarFocusRegion.StateActions, -1) => new(CalendarFocusRegion.WeekStrip, CalendarFocusEntry.Selected),
        _ => null,
    };

    private static CalendarFocusTarget FirstDay(CalendarWeek week)
    {
        for (var index = 0; index < week.Days.Count; index++)
        {
            if (week.Days[index].Items.Count > 0)
            {
                return new(CalendarFocusRegion.DayRow, CalendarFocusEntry.First, index);
            }
        }

        return new(CalendarFocusRegion.StateActions, CalendarFocusEntry.First);
    }

    private static CalendarFocusTarget? AdjacentDay(CalendarWeek week, int dayIndex, int step)
    {
        if (dayIndex < 0 || dayIndex >= week.Days.Count)
        {
            return null;
        }

        var index = dayIndex + step;
        while (index >= 0 && index < week.Days.Count && week.Days[index].Items.Count == 0)
        {
            index += step;
        }

        if (index < 0)
        {
            return new(CalendarFocusRegion.WeekStrip, CalendarFocusEntry.Selected);
        }

        return index < week.Days.Count ? new(CalendarFocusRegion.DayRow, CalendarFocusEntry.Remembered, index) : null;
    }
}
