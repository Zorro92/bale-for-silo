using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Detail;

/// <summary>
/// One edition (cut), every presentation of it merged (PlaybackEditions.kt). Id is the trimmed edition
/// key, "" for the unnamed edition. Versions are the files the Version menu offers, in server order:
/// each presentation's first part, else every file of the edition. FileIds cover every part.
/// </summary>
public sealed record PlaybackEdition(string Id, string Label, IReadOnlyList<FileVersion> Versions, IReadOnlySet<string> FileIds);

/// <summary>Rust's <c>formatting::Edition</c>: indexes into the versions the call was given.</summary>
internal sealed record EditionIndexes(string Id, string Label, int[] Versions, string[] FileIds);

/// <summary>
/// Port of shared/model/catalog/PlaybackEditions.kt, implemented in Rust (native/bale-core/src/formatting.rs):
/// one edition per edition key in server order, keeping the catalog file objects.
/// </summary>
public static class PlaybackEditions
{
    public static string? Label(string? raw, string? key)
    {
        if (!string.IsNullOrWhiteSpace(raw)) return raw.Trim();
        if (string.IsNullOrWhiteSpace(key)) return null;
        return string.Join(" ", key.Trim().Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(word =>
            word.Equals("imax", StringComparison.OrdinalIgnoreCase) ? "IMAX" : word[..1].ToUpperInvariant() + word[1..]));
    }

    public static IReadOnlyList<PlaybackEdition> For(IReadOnlyList<FileVersion> versions,
        IReadOnlyList<PlaybackVariant>? variants = null)
    {
        if (versions.Count == 0) return [];
        var editions = BaleCoreNative.Call("formatting.playback_editions", writer =>
        {
            writer.Server("versions", versions);
            writer.Server("variants", variants ?? []);
        }, BaleCoreJson.Default.EditionIndexesArray);
        return editions.Select(edition => new PlaybackEdition(edition.Id, edition.Label,
            edition.Versions.Select(index => versions[index]).ToList(),
            edition.FileIds.ToHashSet(StringComparer.Ordinal))).ToList();
    }

    /// <summary>The Edition control shows with two or more edition keys (hasEditionChoices).</summary>
    public static bool HasChoices(IReadOnlyList<PlaybackEdition> editions) => editions.Count > 1;

    public static PlaybackEdition? Current(IReadOnlyList<PlaybackEdition> editions, FileVersion? currentVersion) =>
        editions.FirstOrDefault(edition => currentVersion?.FileId is { } id && edition.FileIds.Contains(id)) ?? editions.FirstOrDefault();

    /// <summary>TvPlaybackSelectorRow.kt scopes Version choices to the current edition when Edition shows.</summary>
    public static IReadOnlyList<FileVersion> ScopedVersions(IReadOnlyList<FileVersion> versions,
        IReadOnlyList<PlaybackEdition> editions, FileVersion? currentVersion) =>
        HasChoices(editions) ? Current(editions, currentVersion)?.Versions ?? [] : versions;
}
