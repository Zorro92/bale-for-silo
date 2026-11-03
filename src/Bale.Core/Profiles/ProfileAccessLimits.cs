using Bale.Core.Api.Models;

namespace Bale.Core.Profiles;

/// <summary>ProfileAccessLimits.kt: Child fills untouched defaults; disabling Child clears the visible limits.</summary>
public sealed record ProfileAccessLimits
{
    public bool IsChild { get; init; }
    public string? MaxContentRating { get; init; }
    public bool LibraryRestrictionsEnabled { get; init; }
    public IReadOnlySet<string> AllowedLibraryIds { get; init; } = new HashSet<string>();
    public bool RatingTouched { get; init; }
    public bool LibrariesTouched { get; init; }

    public ProfileAccessLimits WithChild(bool child)
    {
        if (!child) return new();
        var fillLibraries = !LibrariesTouched && !LibraryRestrictionsEnabled;
        return this with
        {
            IsChild = true,
            MaxContentRating = !RatingTouched && MaxContentRating is null ? "PG" : MaxContentRating,
            LibraryRestrictionsEnabled = LibraryRestrictionsEnabled || fillLibraries,
            AllowedLibraryIds = fillLibraries ? new HashSet<string>() : AllowedLibraryIds,
        };
    }

    public ProfileAccessLimits WithRating(string? rating) => this with { MaxContentRating = rating, RatingTouched = true };
    public ProfileAccessLimits WithRestrictions(bool enabled) => this with { LibraryRestrictionsEnabled = enabled, LibrariesTouched = true };
    public ProfileAccessLimits WithLibrary(string id, bool allowed)
    {
        var ids = new HashSet<string>(AllowedLibraryIds);
        if (allowed) ids.Add(id); else ids.Remove(id);
        return this with { AllowedLibraryIds = ids, LibrariesTouched = true };
    }

    public bool AllowlistChangedFrom(ProfileAccessLimits loaded) => LibraryRestrictionsEnabled != loaded.LibraryRestrictionsEnabled
        || (LibraryRestrictionsEnabled && !AllowedLibraryIds.SetEquals(loaded.AllowedLibraryIds));

    public bool NeedsLibrary(ProfileAccessLimits? loaded = null, IReadOnlySet<string>? assignable = null) => LibraryRestrictionsEnabled
        && (loaded is null || AllowlistChangedFrom(loaded)) && !AllowedLibraryIds.Any(id => assignable is null || assignable.Contains(id));

    public static ProfileAccessLimits From(Profile profile) => new()
    {
        IsChild = profile.IsChild == true,
        MaxContentRating = string.IsNullOrEmpty(profile.MaxContentRating) ? null : profile.MaxContentRating,
        LibraryRestrictionsEnabled = profile.LibraryRestrictionsEnabled == true,
        AllowedLibraryIds = new HashSet<string>(profile.AllowedLibraryIds ?? []),
    };

    public static IReadOnlyList<string> Ratings { get; } = ["G", "PG", "PG-13", "R", "NC-17"];
    public static IReadOnlyList<string?> RatingOptions(string? current) =>
        (current is not null && !Ratings.Contains(current) ? new[] { current }.Concat(Ratings) : Ratings).Cast<string?>().Append(null).ToArray();
    public static string RatingCaption(string? ceiling) => ceiling is null ? "Can watch titles of any rating." : $"Can watch titles rated {ceiling} and below.";
    public static bool UnderCeiling(string rating, string? ceiling) => Ratings.Contains(rating)
        && (ceiling is null || Ratings.ToList().IndexOf(rating) < Ratings.ToList().IndexOf(ceiling));
}
