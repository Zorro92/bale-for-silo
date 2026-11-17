using System.Globalization;

namespace Bale.Core.Detail;

/// <summary>
/// One spelling for an episode everywhere it shows: "S01E01" (owner, 2026-11-17). The official TV apps use it
/// under the season row but "S1E1", "S1 · E1" and "S1:E1" elsewhere (Resume, Calendar, Play button, player
/// title, Up Next); Bale keeps the season row's form throughout.
/// </summary>
public static class EpisodeCode
{
    /// <summary>"S01E01"; "E01" without a season. Numbers above 99 keep all their digits.</summary>
    public static string Format(long? season, long episode) => season is { } s
        ? string.Create(CultureInfo.InvariantCulture, $"S{s:00}E{episode:00}")
        : string.Create(CultureInfo.InvariantCulture, $"E{episode:00}");
}
