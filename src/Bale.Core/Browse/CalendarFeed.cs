using System.Globalization;
using Bale.Core.Api;
using Bale.Core.Api.Api.V2.Calendar;
using Bale.Core.Api.Models;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Browse;

/// <summary>
/// The TV calendar's week buckets (shared CalendarViewModel / TvCalendarScreen). Create one per
/// screen and profile: revisiting a week or filter displays its cached snapshot while revalidating.
/// </summary>
public sealed class CalendarFeed
{
    private readonly SiloApiClient _api;
    private readonly BrowseFeed _browse;
    private readonly Dictionary<(DateOnly Start, CalendarFilter Filter, string Timezone), CalendarWeek> _cache = new();
    private readonly object _cacheLock = new();

    public CalendarFeed(SiloApiClient api, BrowseFeed browse)
    {
        _api = api;
        _browse = browse;
    }

    public CalendarWeek? CachedWeek(DateOnly date, CalendarFilter filter, TimeZoneInfo timezone)
    {
        lock (_cacheLock)
        {
            return _cache.GetValueOrDefault((CalendarWeek.StartOfWeek(date), filter, TimezoneId(timezone)));
        }
    }

    /// <summary>Always revalidates; only successful, complete responses enter the screen's cache.</summary>
    public async Task<CalendarWeek> LoadWeekAsync(DateOnly date, CalendarFilter filter, TimeZoneInfo timezone,
        CancellationToken cancellationToken = default)
    {
        var start = CalendarWeek.StartOfWeek(date);
        var end = start.AddDays(6);
        var zone = TimezoneId(timezone);
        var response = await _api.Api.V2.Calendar.GetAsync(config =>
        {
            config.QueryParameters.Start = new Date(start.Year, start.Month, start.Day);
            config.QueryParameters.End = new Date(end.Year, end.Month, end.Day);
            config.QueryParameters.Timezone = zone;
            config.QueryParameters.Filter = filter switch
            {
                CalendarFilter.Following => GetFilterQueryParameterType.Following,
                CalendarFilter.Trending => GetFilterQueryParameterType.Trending,
                _ => GetFilterQueryParameterType.All,
            };
        }, cancellationToken).ConfigureAwait(false);

        if (response?.Events is not { } events || events.Any(day => day.Date is null || day.Items is null
            || day.Items.Any(item => string.IsNullOrWhiteSpace(item.ContentId))))
        {
            // DefaultCalendarApi rejects missing events/items and blank event identities too.
            throw new InvalidOperationException("The server returned an incomplete calendar response.");
        }

        // Keep the server's local-day buckets and item order; never regroup by the source air_date.
        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var day = start.AddDays(offset);
            var bucket = events.FirstOrDefault(value => value.Date is { } valueDate
                && valueDate.Year == day.Year && valueDate.Month == day.Month && valueDate.Day == day.Day);
            return new CalendarDay(day, bucket?.Items?.Select(item => ToItem(item, timezone)).ToArray() ?? []);
        }).ToArray();
        var week = new CalendarWeek(start, days);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_cacheLock)
        {
            _cache[(start, filter, zone)] = week;
        }

        return week;
    }

    /// <summary>The API takes IANA zones; Windows/Xbox reports Windows timezone identifiers.</summary>
    public static string TimezoneId(TimeZoneInfo timezone) =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(timezone.Id, out var iana) ? iana : timezone.Id;

    /// <summary>ApiResult.errorMessage: server detail when available, otherwise the TV's network copy.</summary>
    public static string ErrorMessage(Exception exception)
    {
        if (exception is Problem problem && !string.IsNullOrWhiteSpace(problem.Detail))
        {
            return problem.Detail;
        }

        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is TimeoutException or OperationCanceledException)
            {
                return "The server took too long to respond. Try again.";
            }
        }

        return exception is HttpRequestException ? "Network error. Check your connection." : "Failed to load calendar";
    }

    internal CalendarItem ToItem(CalendarEvent item, TimeZoneInfo timezone) => new(
        item.ContentId!,
        item.Type?.Trim().ToLowerInvariant() ?? string.Empty,
        item.Title ?? string.Empty,
        item.EpisodeTitle,
        item.SeriesId,
        item.SeasonNumber,
        item.EpisodeNumber,
        _browse.Resolve(item.PosterUrl),
        item.PosterThumbhash,
        item.Watched == true,
        (item.Badges ?? []).Select(BadgeLabel).OfType<string>().ToArray(),
        DisplayAirTime(item.AirAt, item.AirTime, timezone));

    private static string? BadgeLabel(string badge) => badge switch
    {
        "series_premiere" => "PREMIERE",
        "season_premiere" => "NEW SEASON",
        "finale" => "FINALE",
        _ => null,
    };

    /// <summary>CalendarTimeFormatter: authoritative instant first; legacy midnight means date-only.</summary>
    internal static string? DisplayAirTime(DateTimeOffset? airAt, string? airTime, TimeZoneInfo timezone, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (airAt is { } instant)
        {
            return TimeZoneInfo.ConvertTime(instant, timezone).ToString("h:mm tt", culture);
        }

        if (string.IsNullOrWhiteSpace(airTime) || airTime == "00:00")
        {
            return null;
        }

        return TimeOnly.TryParse(airTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wallClock)
            ? wallClock.ToString("h:mm tt", culture)
            : airTime;
    }
}
