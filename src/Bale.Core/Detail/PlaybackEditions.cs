using Bale.Core.Api.Models;

namespace Bale.Core.Detail;

/// <summary>An edition's first playable part, plus file identities across every part (PlaybackEditions.kt).</summary>
public sealed record PlaybackEdition(string Id, string Label, IReadOnlyList<FileVersion> Versions,
    FileVersion DefaultVersion, IReadOnlySet<string> FileIds);

/// <summary>Port of shared/model/catalog/PlaybackEditions.kt; preserve server order and catalog file objects.</summary>
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
        if (variants is { Count: > 0 })
        {
            // Kotlin's associateBy keeps the last catalog object for an identity. Never substitute
            // the nested part's copy: track inventories and effective preferences live on the catalog file.
            var byId = new Dictionary<string, FileVersion>(StringComparer.Ordinal);
            foreach (var version in versions)
            {
                if (version.FileId is { } id) byId[id] = version;
            }

            var editions = new List<PlaybackEdition>();
            foreach (var variant in variants)
            {
                var firstPart = variant.Parts?.MinBy(part => part.PartIndex ?? 0);
                if (firstPart is null) continue;
                var choices = (firstPart.Versions ?? []).Select(file => file.FileId is { } id ? byId.GetValueOrDefault(id) : null)
                    .OfType<FileVersion>().ToList();
                var defaultVersion = choices.FirstOrDefault(file => file.FileId == firstPart.DefaultFileId) ?? choices.FirstOrDefault();
                if (defaultVersion is null) continue;
                var fileIds = (variant.Parts ?? []).SelectMany(part => part.Versions ?? []).Select(file => file.FileId)
                    .OfType<string>().ToHashSet(StringComparer.Ordinal);
                editions.Add(new PlaybackEdition(variant.VariantId ?? string.Empty,
                    Label(variant.EditionRaw, variant.EditionKey) ?? "Standard", choices, defaultVersion, fileIds));
            }

            // An explicit variants payload is authoritative, even if every variant is unplayable.
            return editions;
        }

        return versions.GroupBy(file => NonBlank(file.EditionKey) ?? Label(file.EditionRaw, file.EditionKey) ?? string.Empty, StringComparer.Ordinal)
            .Select(group =>
            {
                var files = group.ToList();
                return new PlaybackEdition(group.Key, Label(files[0].EditionRaw, files[0].EditionKey) ?? "Standard", files, files[0],
                    files.Select(file => file.FileId).OfType<string>().ToHashSet(StringComparer.Ordinal));
            }).ToList();
    }

    // Multiple encodes/parts with the same edition label must not create a redundant Edition trigger.
    public static bool HasChoices(IReadOnlyList<PlaybackEdition> editions) =>
        editions.Count > 1 && editions.Select(edition => edition.Label).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();

    public static PlaybackEdition? Current(IReadOnlyList<PlaybackEdition> editions, FileVersion? currentVersion) =>
        editions.FirstOrDefault(edition => currentVersion?.FileId is { } id && edition.FileIds.Contains(id)) ?? editions.FirstOrDefault();

    /// <summary>TvPlaybackSelectorRow.kt scopes quality choices only when the Edition control is shown.</summary>
    public static IReadOnlyList<FileVersion> ScopedVersions(IReadOnlyList<FileVersion> versions,
        IReadOnlyList<PlaybackEdition> editions, FileVersion? currentVersion) =>
        HasChoices(editions) ? Current(editions, currentVersion)?.Versions ?? [] : versions;

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
