using System.Globalization;
using Bale.Core.Detail;

namespace Bale.Core.Browse;

/// <summary>The three TV calendar views (TvCalendarScreen.FilterBar).</summary>
public enum CalendarFilter
{
    Following,
    Trending,
    All,
}

public static class CalendarFilters
{
    public static CalendarFilter FromPreference(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "trending" => CalendarFilter.Trending,
        "all" or "everything" => CalendarFilter.All,
        _ => CalendarFilter.Following,
    };

    public static string Preference(CalendarFilter filter) => filter.ToString().ToLowerInvariant();

    public static string EmptyTitle(CalendarFilter filter) => filter switch
    {
        CalendarFilter.Following => "Nothing from shows you follow",
        CalendarFilter.Trending => "Nothing trending this week",
        _ => "Nothing scheduled this week",
    };

    public static string EmptyCopy(CalendarFilter filter) => filter switch
    {
        CalendarFilter.Following => "No upcoming releases this week from shows you watch, favorite, or watchlist.",
        CalendarFilter.Trending => "No trending releases this week.",
        _ => "No movie releases or episode airings in this week.",
    };

    /// <summary>An empty week links to the other two views, never the current one.</summary>
    public static IReadOnlyList<CalendarFilter> EmptyLinks(CalendarFilter filter) =>
        new[] { CalendarFilter.Following, CalendarFilter.Trending, CalendarFilter.All }.Where(value => value != filter).ToArray();
}

/// <summary>One release, retaining its own identity even when its detail target is the parent series.</summary>
public sealed record CalendarItem(
    string ContentId,
    string Type,
    string Title,
    string? EpisodeTitle,
    string? SeriesId,
    long? SeasonNumber,
    long? EpisodeNumber,
    Uri? PosterUrl,
    string? PosterThumbhash,
    bool Watched,
    IReadOnlyList<string> Badges,
    string? AirTime)
{
    public bool IsEpisode => Type == "episode";

    /// <summary>TvCalendarScreen uses prose, not Home's zero-padded TvEpisodeTag.</summary>
    public string? Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (IsEpisode)
            {
                if (SeasonNumber is { } season && EpisodeNumber is { } episode)
                {
                    parts.Add($"S{season} · E{episode}");
                }

                if (!string.IsNullOrWhiteSpace(EpisodeTitle))
                {
                    parts.Add(EpisodeTitle);
                }
            }
            else if (Type == "movie")
            {
                parts.Add("Movie");
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
    }

    /// <summary>tvCalendarDetailTarget: incomplete episode hierarchies open the item itself.</summary>
    public SeriesDetailEntry? SeriesEntry =>
        IsEpisode && SeriesId?.Trim() is { Length: > 0 } series && SeasonNumber is { } season
            ? new SeriesDetailEntry(series, season, ContentId)
            : null;
}

public sealed record CalendarDay(DateOnly Date, IReadOnlyList<CalendarItem> Items)
{
    public string Header(DateOnly today, CultureInfo? culture = null) =>
        Date == today ? "Today" : Date.ToString("dddd, MMMM d", culture ?? CultureInfo.CurrentCulture);
}

/// <summary>A click-created return target, keyed by event identity rather than parent series.</summary>
public sealed record CalendarReturnTarget(DateOnly Date, string ContentId, int DayIndex, int ItemIndex);

public sealed record CalendarReturnLocation(int DayIndex, int ItemIndex);

/// <summary>The full Monday-first week, including event-less days as scroll targets.</summary>
public sealed record CalendarWeek(DateOnly Start, IReadOnlyList<CalendarDay> Days)
{
    public DateOnly End => Start.AddDays(6);

    public bool HasItems => Days.Any(day => day.Items.Count > 0);

    public bool IsCurrentWeek(DateOnly today) => Start == StartOfWeek(today);

    public DateOnly InitialSelectedDay(DateOnly today) => IsCurrentWeek(today) ? today : Start;

    /// <summary>Thursday names the dominant month of a cross-month week.</summary>
    public string MonthYear(CultureInfo? culture = null) => Start.AddDays(3).ToString("MMMM yyyy", culture ?? CultureInfo.CurrentCulture);

    /// <summary>
    /// TvCalendarScreen's detail return follows an event across corrected air dates. If it was
    /// removed, use the nearest surviving shelf and card index; an empty week releases to controls.
    /// </summary>
    public CalendarReturnLocation? Locate(CalendarReturnTarget target)
    {
        var sections = Enumerable.Range(0, Days.Count).Where(index => Days[index].Items.Count > 0).ToArray();
        foreach (var index in sections.OrderBy(index => Days[index].Date == target.Date ? 0 : 1))
        {
            for (var item = 0; item < Days[index].Items.Count; item++)
            {
                if (Days[index].Items[item].ContentId == target.ContentId)
                {
                    return new CalendarReturnLocation(index, item);
                }
            }
        }

        if (sections.Length == 0)
        {
            return null;
        }

        var nearest = sections.OrderBy(index => Math.Abs(index - target.DayIndex)).First();
        return new CalendarReturnLocation(nearest, Math.Clamp(target.ItemIndex, 0, Days[nearest].Items.Count - 1));
    }

    public static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static CalendarWeek Empty(DateOnly date)
    {
        var start = StartOfWeek(date);
        return new CalendarWeek(start, Enumerable.Range(0, 7).Select(offset => new CalendarDay(start.AddDays(offset), [])).ToArray());
    }
}
