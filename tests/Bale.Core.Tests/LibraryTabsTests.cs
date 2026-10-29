using Bale.Core.Api.Models;
using Bale.Core.Browse;

namespace Bale.Core.Tests;

public class LibraryTabsTests
{
    private static UserLibrary L(string id, string type, int order) => new() { Id = id, Name = id, Type = type, SortOrder = order };

    [Fact]
    public void GroupsLibrariesByTypeInSkylineOrder()
    {
        var tabs = LibraryTabs.From([L("shows", "series", 3), L("kids", "movies", 2), L("movies", "movies", 1), L("books", "audiobooks", 4)]);

        Assert.Equal([LibraryTabType.Movies, LibraryTabType.Series, LibraryTabType.Audiobooks], tabs.Select(t => t.Type));
        Assert.Equal(["movies", "kids"], tabs[0].Libraries.Select(l => l.Id));
    }

    [Fact]
    public void MixedLibraryAppearsUnderMoviesAndSeries()
    {
        var tabs = LibraryTabs.From([L("all", "Mixed", 1)]);

        Assert.Equal([LibraryTabType.Movies, LibraryTabType.Series], tabs.Select(t => t.Type));
    }

    [Fact]
    public void UnknownTypesGetNoTab() => Assert.Empty(LibraryTabs.From([L("comics", "comics", 1)]));
}
