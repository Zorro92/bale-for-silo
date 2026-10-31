using System.Globalization;
using Bale.Core.Api.Models;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Detail;

public sealed partial class DetailFeed
{
    /// <summary>TrailerRail.kt: local files first, then supported remote videos, preserving server rank.</summary>
    internal IReadOnlyList<TrailerCard> Trailers(CatalogItemDetail item)
    {
        var local = (item.Extras ?? []).Select(extra => new TrailerCard(
            "local:" + extra.ContentId,
            ExtraTitle(extra.Title, extra.Kind),
            extra.DurationSeconds is > 0 ? PlayLabel.Clock(extra.DurationSeconds.Value) : null,
            _browse.Resolve(extra.StillUrl)));
        var remote = (item.Videos ?? [])
            .Where(video => string.Equals(video.Site, "youtube", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(video.SiteKey))
            .Select(video => new TrailerCard(
                $"remote:{video.Site}:{video.SiteKey}",
                ExtraTitle(video.Name, video.Kind),
                "YouTube",
                new Uri($"https://i.ytimg.com/vi/{Uri.EscapeDataString(video.SiteKey!)}/hqdefault.jpg")));
        return local.Concat(remote).DistinctBy(card => card.Key).ToList();
    }

    private static string ExtraTitle(string? title, string? kind)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title.Trim();
        }

        return kind?.ToLowerInvariant() switch
        {
            "behind_the_scenes" or "behind-the-scenes" => "Behind the Scenes",
            "deleted_scene" or "deleted-scene" => "Deleted Scene",
            null or "" => "Extra",
            var other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other.Replace('_', ' ').Replace('-', ' ').Trim()),
        };
    }

    /// <summary>TvDetailFactsTable.assembleFacts: labels, token matching, sorted credits, three-name limits and full genres.</summary>
    internal static IReadOnlyList<DetailFact> DetailFacts(CatalogItemDetail item)
    {
        var facts = new List<DetailFact>();
        var crew = item.Crew ?? [];
        void Credits(string label, params string[] jobs)
        {
            var names = crew.Where(member => jobs.Any(job => (member.Job ?? string.Empty).Contains(job, StringComparison.OrdinalIgnoreCase)))
                .Select(member => member.Name?.Trim()).OfType<string>().Where(name => name.Length > 0)
                .Distinct().Order(StringComparer.Ordinal).ToList();
            if (names.Count > 0)
            {
                facts.Add(new DetailFact(label, string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", …" : string.Empty)));
            }
        }

        void Names(string label, IEnumerable<string>? values, int limit = 3)
        {
            var names = values?.Where(value => !string.IsNullOrWhiteSpace(value)).Take(limit).ToList() ?? [];
            if (names.Count > 0)
            {
                facts.Add(new DetailFact(label, string.Join(", ", names)));
            }
        }

        void Date(string label, Date? date, string format)
        {
            if (date is { } value)
            {
                facts.Add(new DetailFact(label, new DateOnly(value.Year, value.Month, value.Day).ToString(format, CultureInfo.GetCultureInfo("en-US"))));
            }
        }

        Credits("Director", "director");
        Credits(crew.Any(member => (member.Job ?? string.Empty).Contains("screenplay", StringComparison.OrdinalIgnoreCase)) ? "Writer" : "Written by",
            "writer", "screenplay", "story", "teleplay");
        Names("Studio", item.Studios);
        Names("Network", item.Networks);
        Names("Country", item.Countries);
        Names("Genres", item.Genres, int.MaxValue);
        if (string.Equals(item.Type, "episode", StringComparison.OrdinalIgnoreCase))
        {
            Date("Aired", item.AirDate ?? item.ReleaseDate, "MMM d, yyyy");
        }
        else
        {
            Date("Released", item.ReleaseDate, "MMMM d, yyyy");
            Date("First Aired", item.FirstAirDate, "MMMM d, yyyy");
            Date("Last Aired", item.LastAirDate, "MMMM d, yyyy");
        }

        return facts;
    }
}
