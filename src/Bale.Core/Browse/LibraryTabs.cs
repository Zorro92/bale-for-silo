using Bale.Core.Api.Models;

namespace Bale.Core.Browse;

/// <summary>A top-bar content-type tab, in Skyline's fixed order.</summary>
public enum LibraryTabType
{
    Movies,
    Series,
    Music,
    Audiobooks,
}

/// <summary>A tab and the libraries it scopes over, in the server's sort order.</summary>
public sealed record LibraryTab(LibraryTabType Type, IReadOnlyList<UserLibrary> Libraries)
{
    public string Title => Type.ToString();
}

/// <summary>
/// Skyline §3.1: tabs are library <em>types</em>, not libraries. A type's tab appears only when the
/// profile can see at least one library of it. Classification mirrors Silo's TV apps
/// (<c>TvLibraryTabType.matches</c>), including "mixed" libraries under both Movies and Series.
/// </summary>
public static class LibraryTabs
{
    private static readonly HashSet<string> MovieTypes = ["movie", "movies", "video"];
    private static readonly HashSet<string> SeriesTypes = ["series", "show", "shows", "tv"];
    private static readonly HashSet<string> MusicTypes = ["music", "album", "albums", "artist", "artists", "audio"];
    private static readonly HashSet<string> AudiobookTypes = ["audiobook", "audiobooks"];

    public static IReadOnlyList<LibraryTab> From(IEnumerable<UserLibrary> libraries)
    {
        var sorted = libraries.OrderBy(library => library.SortOrder ?? long.MaxValue).ToList();
        return Enum.GetValues<LibraryTabType>()
            .Select(type => new LibraryTab(type, sorted.Where(library => Matches(type, library.Type)).ToList()))
            .Where(tab => tab.Libraries.Count > 0)
            .ToList();
    }

    internal static bool Matches(LibraryTabType tab, string? libraryType)
    {
        var type = (libraryType ?? string.Empty).Trim().ToLowerInvariant();
        return tab switch
        {
            LibraryTabType.Movies => MovieTypes.Contains(type) || type == "mixed",
            LibraryTabType.Series => SeriesTypes.Contains(type) || type == "mixed",
            LibraryTabType.Music => MusicTypes.Contains(type),
            LibraryTabType.Audiobooks => AudiobookTypes.Contains(type),
            _ => false,
        };
    }
}
