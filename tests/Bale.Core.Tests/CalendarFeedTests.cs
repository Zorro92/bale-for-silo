using System.Globalization;
using System.Net;
using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Core.Connection;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Tests;

public class CalendarFeedTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly DateOnly Today = new(2026, 10, 31);

    private static (SiloConnection Connection, CalendarFeed Feed) Create(StubHandler handler)
    {
        var server = new Uri("https://example.com/silo/");
        var connection = new SiloConnection(server, new ClientIdentity("d", "x", "xbox", "Bale"), transport: handler);
        return (connection, new CalendarFeed(connection.Api, new BrowseFeed(connection.Api, server)));
    }

    [Theory]
    [InlineData(CalendarFilter.Following, "following")]
    [InlineData(CalendarFilter.Trending, "trending")]
    [InlineData(CalendarFilter.All, "all")]
    public async Task RequestsMondayThroughSundayInTheViewerTimezone(CalendarFilter filter, string queryFilter)
    {
        var handler = new StubHandler().Enqueue("""{"events": []}""");
        var (connection, feed) = Create(handler);
        using var _ = connection;
        var timezone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

        var week = await feed.LoadWeekAsync(Today, filter, timezone);

        var request = Assert.Single(handler.Requests).RequestUri!;
        Assert.Equal("/silo/api/v2/calendar", request.AbsolutePath);
        var query = Uri.UnescapeDataString(request.Query);
        Assert.Contains("start=2026-10-26", query);
        Assert.Contains("end=2026-11-01", query);
        Assert.Contains($"filter={queryFilter}", query);
        Assert.Contains("timezone=America/Chicago", query);
        Assert.DoesNotContain("library_id", query);
        Assert.Equal(7, week.Days.Count);
        Assert.Equal(new DateOnly(2026, 10, 26), week.Start);
        Assert.Equal(new DateOnly(2026, 11, 1), week.End);
        Assert.All(week.Days, day => Assert.Empty(day.Items));
        Assert.False(week.HasItems);
    }

    [Fact]
    public async Task KeepsLocalDayBucketsAndServerOrderWithoutCollapsingEpisodesOntoTheirSeries()
    {
        var handler = new StubHandler().Enqueue("""
            {"events": [
              {"date": "2026-10-31", "items": [
                {"content_id": "e2", "type": "episode", "title": "A show", "series_id": "series", "season_number": 2,
                 "episode_number": 2, "episode_title": "Second", "air_date": "2026-11-01", "local_air_date": "2026-10-31",
                 "poster_url": "/api/v2/artwork/poster.jpg?sig=example", "poster_thumbhash": "hash", "watched": true,
                 "badges": ["series_premiere", "season_premiere", "finale", "future_badge"]},
                {"content_id": "e1", "type": "episode", "title": "A show", "series_id": "series", "season_number": 2,
                 "episode_number": 1, "air_date": "2026-10-31", "local_air_date": "2026-10-31"}
              ]},
              {"date": "2026-10-27", "items": [{"content_id": "m1", "type": "movie", "title": "A movie"}]},
              {"date": "2026-11-02", "items": [{"content_id": "outside", "title": "Outside week"}]}
            ]}
            """);
        var (connection, feed) = Create(handler);
        using var _ = connection;

        var week = await feed.LoadWeekAsync(Today, CalendarFilter.Following, TimeZoneInfo.Utc);

        Assert.True(week.HasItems);
        Assert.Equal(["e2", "e1"], week.Days[5].Items.Select(item => item.ContentId));
        var episode = week.Days[5].Items[0];
        Assert.Equal("S2 · E2 · Second", episode.Subtitle);
        Assert.Equal("https://example.com/silo/api/v2/artwork/poster.jpg?sig=example", episode.PosterUrl!.ToString());
        Assert.Equal("hash", episode.PosterThumbhash);
        Assert.True(episode.Watched);
        Assert.Equal(["PREMIERE", "NEW SEASON", "FINALE"], episode.Badges);
        Assert.Equal("Movie", Assert.Single(week.Days[1].Items).Subtitle);
        Assert.Empty(week.Days[6].Items);
        Assert.Equal(3, week.Days.Sum(day => day.Items.Count));
    }

    [Theory]
    [InlineData("episode", " series ", 0L, true)]
    [InlineData(" Episode ", "series", 2L, true)]
    [InlineData("episode", " ", 2L, false)]
    [InlineData("episode", null, 2L, false)]
    [InlineData("episode", "series", null, false)]
    [InlineData("movie", "series", 2L, false)]
    [InlineData("season_premiere", "series", 2L, false)]
    public void DetailSelectionMatchesTvCalendarDetailTarget(string type, string? seriesId, long? season, bool opensSeries)
    {
        var (connection, feed) = Create(new StubHandler());
        using var _ = connection;
        var item = feed.ToItem(new CalendarEvent
        {
            ContentId = "event", Type = type, SeriesId = seriesId, SeasonNumber = season,
        }, TimeZoneInfo.Utc);

        if (opensSeries)
        {
            Assert.Equal("series", item.SeriesEntry!.SeriesId);
            Assert.Equal(season, item.SeriesEntry.SeasonNumber);
            Assert.Equal("event", item.SeriesEntry.EpisodeId);
        }
        else
        {
            Assert.Null(item.SeriesEntry);
            Assert.Equal("event", item.ContentId);
        }
    }

    [Theory]
    [InlineData(1L, 2L, "Pilot", "S1 · E2 · Pilot")]
    [InlineData(null, 2L, "Pilot", "Pilot")]
    [InlineData(1L, null, " ", null)]
    [InlineData(0L, 1L, null, "S0 · E1")]
    public void EpisodeCaptionUsesCalendarProseNotTheHomeEpisodeTag(long? season, long? episode, string? title, string? expected)
    {
        var (connection, feed) = Create(new StubHandler());
        using var _ = connection;
        var item = feed.ToItem(new CalendarEvent
        {
            ContentId = "episode", Type = "episode", SeasonNumber = season, EpisodeNumber = episode, EpisodeTitle = title,
        }, TimeZoneInfo.Utc);

        Assert.Equal(expected, item.Subtitle);
    }

    [Fact]
    public void AirAtIsConvertedToLocalTimeEvenForMidnight()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        Assert.Equal("8:30 PM", CalendarFeed.DisplayAirTime(DateTimeOffset.Parse("2026-11-01T01:30:00Z", English), "00:00", zone, English));
        Assert.Equal("12:00 AM", CalendarFeed.DisplayAirTime(DateTimeOffset.Parse("2026-10-31T00:00:00Z", English), null, TimeZoneInfo.Utc, English));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", null)]
    [InlineData("00:00", null)]
    [InlineData("21:15", "9:15 PM")]
    [InlineData("TBA", "TBA")]
    public void LegacyAirTimeHidesDateOnlyMidnightAndDoesNotGuessItsTimezone(string? time, string? expected) =>
        Assert.Equal(expected, CalendarFeed.DisplayAirTime(null, time, TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"), English));

    [Fact]
    public void WindowsTimezoneIdIsSentAsIana() =>
        Assert.Equal("America/Chicago", CalendarFeed.TimezoneId(TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time")));

    [Theory]
    [InlineData("2026-11-01", "2026-10-26", "October 2026")]
    [InlineData("2026-11-02", "2026-11-02", "November 2026")]
    [InlineData("2027-01-01", "2026-12-28", "December 2026")]
    public void WeekMathIsMondayFirstWithThursdayMonthLabel(string date, string monday, string month)
    {
        var today = DateOnly.Parse(date, English);
        var week = CalendarWeek.Empty(today);
        Assert.Equal(DateOnly.Parse(monday, English), week.Start);
        Assert.Equal(month, week.MonthYear(English));
        Assert.True(week.IsCurrentWeek(today));
        Assert.Equal(today, week.InitialSelectedDay(today));
        Assert.Equal(week.Start, week.InitialSelectedDay(today.AddDays(7)));
        Assert.Equal("Today", new Bale.Core.Browse.CalendarDay(today, []).Header(today, English));
        Assert.Equal("Monday, October 26", new Bale.Core.Browse.CalendarDay(new DateOnly(2026, 10, 26), []).Header(Today, English));
    }

    [Fact]
    public async Task CachedWeekIsAvailableForRevalidationAndIsNotReplacedByAnError()
    {
        var handler = new StubHandler().Enqueue("""{"events": []}""")
            .Enqueue(HttpStatusCode.BadRequest, """{"status":400,"title":"Bad Request","detail":"Calendar unavailable"}""");
        var (connection, feed) = Create(handler);
        using var _ = connection;
        Assert.Null(feed.CachedWeek(Today, CalendarFilter.Following, TimeZoneInfo.Utc));

        var week = await feed.LoadWeekAsync(Today, CalendarFilter.Following, TimeZoneInfo.Utc);
        Assert.Same(week, feed.CachedWeek(Today.AddDays(-1), CalendarFilter.Following, TimeZoneInfo.Utc));
        Assert.Null(feed.CachedWeek(Today, CalendarFilter.All, TimeZoneInfo.Utc));
        Assert.Null(feed.CachedWeek(Today.AddDays(7), CalendarFilter.Following, TimeZoneInfo.Utc));
        Assert.Null(feed.CachedWeek(Today, CalendarFilter.Following, TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")));
        await Assert.ThrowsAnyAsync<ApiException>(() => feed.LoadWeekAsync(Today, CalendarFilter.Following, TimeZoneInfo.Utc));
        Assert.Same(week, feed.CachedWeek(Today, CalendarFilter.Following, TimeZoneInfo.Utc));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"events\":[{\"date\":\"2026-10-31\"}]}")]
    [InlineData("{\"events\":[{\"items\":[]}]}")]
    [InlineData("{\"events\":[{\"date\":\"2026-10-31\",\"items\":[{\"content_id\":\" \"}]}]}")]
    public async Task IncompleteResponsesAreNotSuccessfulEmptyWeeks(string response)
    {
        var (connection, feed) = Create(new StubHandler().Enqueue(response));
        using var _ = connection;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => feed.LoadWeekAsync(Today, CalendarFilter.Following, TimeZoneInfo.Utc));
        Assert.Equal("The server returned an incomplete calendar response.", error.Message);
        Assert.Null(feed.CachedWeek(Today, CalendarFilter.Following, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task EmptyFollowingDoesNotFallBackToAllUnlikeTheHomeUpcomingRail()
    {
        var handler = new StubHandler().Enqueue("""{"events": []}""");
        var (connection, feed) = Create(handler);
        using var _ = connection;
        Assert.False((await feed.LoadWeekAsync(Today, CalendarFilter.Following, TimeZoneInfo.Utc)).HasItems);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void ErrorsUseServerDetailAndOfficialNetworkCopy()
    {
        Assert.Equal("Calendar unavailable", CalendarFeed.ErrorMessage(new Problem { Detail = "Calendar unavailable" }));
        Assert.Equal("Failed to load calendar", CalendarFeed.ErrorMessage(new Problem { Detail = " " }));
        Assert.Equal("Failed to load calendar", CalendarFeed.ErrorMessage(new InvalidOperationException("Invalid payload")));
        Assert.Equal("Network error. Check your connection.", CalendarFeed.ErrorMessage(new HttpRequestException("Offline")));
        Assert.Equal("The server took too long to respond. Try again.", CalendarFeed.ErrorMessage(new TaskCanceledException()));
        Assert.Equal("The server took too long to respond. Try again.", CalendarFeed.ErrorMessage(new HttpRequestException("Timed out", new TimeoutException())));
    }

    [Theory]
    [InlineData(null, CalendarFilter.Following)]
    [InlineData("everything", CalendarFilter.All)]
    [InlineData("all", CalendarFilter.All)]
    [InlineData("trending", CalendarFilter.Trending)]
    [InlineData("unknown", CalendarFilter.Following)]
    public void PreferencesAndEmptyLinksUseOnlyTheThreeTvViews(string? value, CalendarFilter expected)
    {
        var filter = CalendarFilters.FromPreference(value);
        Assert.Equal(expected, filter);
        Assert.Equal(filter, CalendarFilters.FromPreference(CalendarFilters.Preference(filter)));
        var links = CalendarFilters.EmptyLinks(filter);
        Assert.Equal(2, links.Count);
        Assert.DoesNotContain(filter, links);
        Assert.False(string.IsNullOrWhiteSpace(CalendarFilters.EmptyTitle(filter)));
        Assert.False(string.IsNullOrWhiteSpace(CalendarFilters.EmptyCopy(filter)));
    }
}
