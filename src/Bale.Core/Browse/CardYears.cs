using System.Globalization;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Browse;

/// <summary>
/// CardYears.swift (Apple 9718f17b; owner, 2026-11-18, a deliberate deviation from Android's single year): the years
/// line under a card. A series shows the years it ran, "2019 – 2023" once it has ended or been cancelled and
/// "2021 – Present" while it is returning, from the row's show_status and last_air_date. A series that started and
/// ended in one year, one whose row doesn't say, and every other kind of item keep the single year.
/// </summary>
public static class CardYears
{
    public static string? Caption(string? type, long? year, string? showStatus, Date? lastAirDate)
    {
        if (year is not > 0) return null;
        var first = year.Value.ToString(CultureInfo.InvariantCulture);
        if (showStatus is null || !IsSeries(type)) return first;
        // The server stores one spelling of each airing state.
        switch (showStatus)
        {
            case "returning":
                return first + " – Present";
            case "ended" or "cancelled":
                return lastAirDate is { Year: var last } && last > year
                    ? first + " – " + last.ToString(CultureInfo.InvariantCulture) : first;
            default:
                return first;
        }
    }

    /// <summary>Models.swift SiloMediaType.isSeries.</summary>
    private static bool IsSeries(string? type) =>
        type?.Trim().ToLowerInvariant() is "series" or "show" or "shows" or "tv" or "tvshows";
}
