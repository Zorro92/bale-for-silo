using System.Globalization;
using System.Text.RegularExpressions;
using Bale.Core.Api.Models;

namespace Bale.Core.Requests;

public enum RequestDisplay { Pending, OnTheWay, InLibrary, Declined, Failed, Unavailable }
public enum RequestTint { Amber, Sky, Emerald, Rose, Neutral }

/// <summary>RequestDisplayState.kt / RequestProgress: one policy for cards, the preview and detail.</summary>
public sealed record RequestProgress(RequestDisplay Display, int CompletedSteps, int? CurrentStep,
    string ShortLabel, string LongLabel, string? Reason = null)
{
    public RequestTint Tint => Display switch
    {
        RequestDisplay.Pending => RequestTint.Amber,
        RequestDisplay.OnTheWay => RequestTint.Sky,
        RequestDisplay.InLibrary => RequestTint.Emerald,
        RequestDisplay.Declined or RequestDisplay.Failed => RequestTint.Rose,
        _ => RequestTint.Neutral,
    };

    public string DetailTitle => Display switch
    {
        RequestDisplay.Pending => "Requested · Pending",
        RequestDisplay.Declined or RequestDisplay.Failed => RequestPresentation.ReasonCopy(Reason) is { } reason
            ? $"{LongLabel} · {reason}" : LongLabel,
        _ => LongLabel,
    };

    public string? LibraryItemToOpen(string? contentId) =>
        Display == RequestDisplay.InLibrary && !string.IsNullOrWhiteSpace(contentId) ? contentId : null;
}

/// <summary>A page-unique row key also distinguishes a title shown on more than one shelf.</summary>
public sealed record RequestRow(string Key, string Title, IReadOnlyList<RequestTitle> Items, string? Eyebrow = null);

public sealed record RequestTitle(string Key, string MediaType, long TmdbId, string Title, long? Year,
    string? Overview, Uri? PosterUrl, Uri? BackdropUrl, RequestProgress? Progress, string? LibraryContentId,
    MediaRequest? Record = null, double? VoteAverage = null)
{
    public string? LibraryItemToOpen => Progress?.LibraryItemToOpen(LibraryContentId);
    public string Caption => Record is null ? Year is > 0 ? Year.Value.ToString(CultureInfo.CurrentCulture) : string.Empty
        : Progress?.ShortLabel ?? string.Empty;
    public string StatusText => Record is { } record && Progress is { } progress
        ? RequestPresentation.RowStatus(record, progress) : Progress?.LongLabel ?? string.Empty;
    public string Facts => string.Join(" · ", new[]
    {
        Year is > 0 ? Year.Value.ToString(CultureInfo.CurrentCulture) : null,
        RequestPresentation.MediaTypeLabel(MediaType),
        Record is { } own && RequestPresentation.Day(own.CreatedAt) is { } day ? $"Requested {day}"
            : VoteAverage is > 0 ? $"TMDB {VoteAverage.Value.ToString("0.0", CultureInfo.CurrentCulture)}" : null,
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

public static class RequestPresentation
{
    public const string UnconfirmedSubmit = "We couldn't confirm the request. Check My Requests before trying again.";
    public const string UnconfirmedCancel = "We couldn't confirm the cancellation. Refresh your requests to check it.";
    public static IReadOnlyList<string> Steps { get; } = ["Requested", "Approved", "Downloading", "In your library"];

    // TvRequestPresentation.kt: TV deliberately omits ebooks, but retains audiobook aliases.
    public static bool Supports(string? type) => type?.Trim().ToLowerInvariant() is "movie" or "series" or "audiobook" or "audiobooks";
    public static bool CanRequest(string? type) => type is "movie" or "series";
    public static bool IsEnabled(FeatureStatus? status) => status is
        { RequestsEnabled: true, Allowed: true, State: FeatureStatus_state.Available };

    public static RequestProgress Progress(MediaRequest record)
    {
        var decision = record.State is "declined" or "cancelled"
            || record.State is null && record.Outcome is "declined" or "cancelled";
        var reason = decision ? record.OutcomeReason
            : !string.IsNullOrWhiteSpace(record.LastError) ? record.LastError : record.OutcomeReason;
        return Progress(record.State, record.Status, record.Outcome, reason);
    }

    public static RequestProgress? Progress(string? availability, RequestMediaState? request)
    {
        request ??= new RequestMediaState();
        // An active request for missing seasons outranks an already-present series in the library.
        if (KnownState(request.State)) return Progress(request.State, request.Status, "active");
        if (availability == "available") return Progress("available", null, null);
        if (!string.IsNullOrWhiteSpace(request.Status)) return Progress(null, request.Status, "active");
        return request.Requestable == true ? null : Progress("cancelled", null, null, request.Reason);
    }

    public static RequestProgress Progress(string? state, string? status, string? outcome, string? reason = null)
    {
        var display = state switch
        {
            "pending" => RequestDisplay.Pending,
            "approved" or "processing" or "partially_available" => RequestDisplay.OnTheWay,
            "available" => RequestDisplay.InLibrary,
            "declined" => RequestDisplay.Declined,
            "failed" => RequestDisplay.Failed,
            "cancelled" => RequestDisplay.Unavailable,
            _ => outcome switch
            {
                "declined" => RequestDisplay.Declined,
                "failed" => RequestDisplay.Failed,
                "cancelled" => RequestDisplay.Unavailable,
                _ => status switch
                {
                    "pending" => RequestDisplay.Pending,
                    "completed" => RequestDisplay.InLibrary,
                    "failed" => RequestDisplay.Failed,
                    _ => RequestDisplay.OnTheWay,
                },
            },
        };
        return display switch
        {
            RequestDisplay.Pending => new(display, 1, 1, "Pending", "Waiting for approval"),
            RequestDisplay.InLibrary => new(display, 4, null, "In library", "In your library"),
            RequestDisplay.Declined => new(display, 1, 1, "Declined", "Declined", reason),
            RequestDisplay.Failed => new(display, 2, 2, "Failed", "Request failed", reason),
            RequestDisplay.Unavailable => new(display, 0, null, "Unavailable", ReasonCopy(reason) ?? "Unavailable", reason),
            _ when state == "partially_available" => new(display, 3, 3, "Partly in library", "Partly in your library"),
            _ when status == "completed" => new(display, 3, 3, "Adding to library", "Adding to your library"),
            _ when status == "downloading" => new(display, 2, 2, "Downloading", "Downloading"),
            _ when status is "approved" or "queued" => new(display, 2, 2, "Queued", "Queued for download"),
            _ => new(display, 2, 2, "On the way", "On the way"),
        };
    }

    private static bool KnownState(string? state) => state is "pending" or "approved" or "processing"
        or "partially_available" or "available" or "declined" or "failed" or "cancelled";

    public static RequestTitle Title(MediaRequest record) => new($"request:{record.Id}", record.MediaType ?? string.Empty,
        record.TmdbId ?? 0, record.Title ?? string.Empty, record.Year, record.Overview, Image(record.PosterPath),
        Image(record.BackdropPath, backdrop: true), Progress(record), record.LibraryContentId, record);

    public static RequestTitle Title(RequestMediaResult result) => new($"tmdb:{result.MediaType}:{result.TmdbId}",
        result.MediaType ?? string.Empty, result.TmdbId ?? 0, result.Title ?? string.Empty, result.Year, result.Overview,
        Image(result.PosterPath), Image(result.BackdropPath, backdrop: true), Progress(result.Availability, result.Request),
        result.LibraryContentId, VoteAverage: result.VoteAverage);

    /// <summary>TvRequestsPage.kt / MyRequestsBucket: in motion, needs attention, landed; cancelled disappears.</summary>
    public static IReadOnlyList<RequestRow> Rows(IEnumerable<MediaRequest> mine, DiscoverSectionCollection discover)
    {
        var rows = new List<RequestRow>();
        var own = mine.Where(record => Supports(record.MediaType) && Progress(record).Display != RequestDisplay.Unavailable)
            .OrderBy(record => Progress(record).Display switch
            {
                RequestDisplay.Pending or RequestDisplay.OnTheWay => 0,
                RequestDisplay.Declined or RequestDisplay.Failed => 1,
                _ => 2,
            }).ThenByDescending(record => record.CreatedAt).Select(Title).ToArray();
        if (own.Length > 0) rows.Add(new("mine", "Your requests", own));
        var sections = DiscoverSections(discover);
        var first = true;
        foreach (var section in sections)
        {
            var items = (section.Results ?? []).Where(result => Supports(result.MediaType)).Select(Title).ToArray();
            if (items.Length == 0) continue;
            rows.Add(new($"discover:{section.Key}", section.Title ?? string.Empty, items, first ? "Discover" : null));
            first = false;
        }
        return rows;
    }

    // RequestCarouselMerge.kt: use the admin layout when supplied; older servers get two interleaved shelves.
    private static IEnumerable<DiscoverSection> DiscoverSections(DiscoverSectionCollection discover)
    {
        var sections = discover.Items ?? [];
        if (discover.RowOrder is { Count: > 0 } order)
        {
            return order.Where(row => row.Type is not ("studios" or "networks" or "genres"))
                .Select(row => sections.FirstOrDefault(section => section.Key == row.Key)).OfType<DiscoverSection>();
        }
        return new[]
        {
            Merge("trending", "Trending now", "trending_movies", "trending_series"),
            Merge("popular", "Crowd favorites", "popular_movies", "popular_series"),
        };

        DiscoverSection Merge(string key, string title, string movie, string series)
        {
            var movies = sections.FirstOrDefault(section => section.Key == movie)?.Results ?? [];
            var shows = sections.FirstOrDefault(section => section.Key == series)?.Results ?? [];
            var items = Enumerable.Range(0, Math.Max(movies.Count, shows.Count))
                .SelectMany(index => new[] { movies.ElementAtOrDefault(index), shows.ElementAtOrDefault(index) })
                .OfType<RequestMediaResult>().DistinctBy(item => (item.MediaType, item.TmdbId)).ToList();
            return new DiscoverSection { Key = key, Title = title, Results = items };
        }
    }

    public static Uri? Image(string? path, bool backdrop = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var value = path.StartsWith('/') ? $"https://image.tmdb.org/t/p/{(backdrop ? "w780" : "w500")}{path}" : path;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;
    }

    public static string MediaTypeLabel(string type) => type switch { "movie" => "Movie", "series" => "Series", _ => "Title" };
    public static string? Day(DateTimeOffset? timestamp, TimeZoneInfo? timezone = null, CultureInfo? culture = null) =>
        timestamp is { } instant ? TimeZoneInfo.ConvertTime(instant, timezone ?? TimeZoneInfo.Local)
            .ToString("MMM d", culture ?? CultureInfo.CurrentCulture) : null;

    public static string? QualitySummary(IEnumerable<RequestTarget>? targets)
    {
        var list = targets?.ToArray() ?? [];
        if (list.Length > 1)
        {
            var parts = list.Where(target => !string.IsNullOrWhiteSpace(target.Quality)).Select(target =>
            {
                var word = target.Status switch
                {
                    "completed" => "done", "downloading" => "downloading", "approved" or "queued" => "queued",
                    "pending" => "pending", "failed" => "failed", _ => null,
                };
                return word is null ? null : $"{target.Quality} {word}";
            }).OfType<string>().ToArray();
            if (parts.Length > 0) return string.Join(" · ", parts);
        }
        var qualities = list.Select(target => target.Quality).Where(value => !string.IsNullOrWhiteSpace(value));
        var text = string.Join(" · ", qualities);
        return text.Length > 0 ? text : null;
    }

    public static string RowStatus(MediaRequest record, RequestProgress progress)
    {
        if (progress.Display is RequestDisplay.Declined or RequestDisplay.Failed && ReasonCopy(progress.Reason) is { } reason)
            return $"{progress.LongLabel} · {reason}";
        if (progress.Display == RequestDisplay.OnTheWay && record.Targets is { Count: > 1 } && QualitySummary(record.Targets) is { } qualities)
            return $"{progress.LongLabel} · {qualities}";
        return progress.LongLabel;
    }

    public static string? ReasonCopy(string? token)
    {
        var value = token?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return value switch
        {
            "already_requested" => "Already requested", "already_available" => "Already in your library",
            "quota_exceeded" or "limit_reached" => "Request limit reached", "requests_disabled" => "Requests are turned off",
            "blocked" or "requesting_blocked" => "You can't request media right now",
            "validation_failed" => "That request couldn't be submitted", "invalid_state" => "This request can no longer be changed",
            "not_found" => "This title is no longer available", "request_unconfirmed" => "Not confirmed yet",
            _ => Regex.IsMatch(value, "^[a-z0-9_]+$", RegexOptions.CultureInvariant) ? null : char.ToUpper(value[0], CultureInfo.CurrentCulture) + value[1..],
        };
    }
}
