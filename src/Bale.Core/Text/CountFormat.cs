using System.Globalization;

namespace Bale.Core.Text;

/// <summary>
/// LocaleFormat.groupedCount (Android b2b0fb565, Apple 710169c9): counts that can run large (a library's items,
/// a person's titles) use the culture's thousands separator, "1,284 titles". Years, codes and episode numbers
/// stay ungrouped.
/// </summary>
public static class CountFormat
{
    public static string Grouped(long value, CultureInfo? culture = null) =>
        value.ToString("N0", culture ?? CultureInfo.CurrentCulture);

    /// <summary>"1 title" / "1,284 titles".</summary>
    public static string Noun(long value, string singular, string plural, CultureInfo? culture = null) =>
        value == 1 ? "1 " + singular : Grouped(value, culture) + " " + plural;
}
