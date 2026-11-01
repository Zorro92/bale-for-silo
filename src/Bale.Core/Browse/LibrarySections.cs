namespace Bale.Core.Browse;

public enum LibrarySection { Recommended, Browse, Collections }

/// <summary>The implemented subset of shell/TvLibraryPill.kt's type-aware set, in set() order.</summary>
public static class LibrarySections
{
    public static IReadOnlyList<LibrarySection> For(LibraryTabType type, bool? hasCollections) =>
        // components/TvCascadeSelector.kt keeps Collections visible until the shell's probe finishes.
        type != LibraryTabType.Music && hasCollections != false
            ? [LibrarySection.Recommended, LibrarySection.Browse, LibrarySection.Collections]
            : [LibrarySection.Recommended, LibrarySection.Browse];
}
