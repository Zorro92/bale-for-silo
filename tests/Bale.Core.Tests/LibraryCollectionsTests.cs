using Bale.Core.Browse;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class LibraryCollectionsTests
{
    private static LibraryCollection Collection(string id, bool user = false) => new(id, "Collection " + id, null, user);

    [Theory]
    [InlineData(LibraryTabType.Movies)]
    [InlineData(LibraryTabType.Series)]
    [InlineData(LibraryTabType.Audiobooks)]
    public void CollectionsPillFollowsTheOfficialSetOrderAndRemainsUntilProbed(LibraryTabType type)
    {
        Assert.Equal([LibrarySection.Recommended, LibrarySection.Browse, LibrarySection.Collections], LibrarySections.For(type, null));
        Assert.Equal(LibrarySections.For(type, null), LibrarySections.For(type, true));
        Assert.Equal([LibrarySection.Recommended, LibrarySection.Browse], LibrarySections.For(type, false));
        Assert.DoesNotContain(LibrarySection.Collections, LibrarySections.For(LibraryTabType.Music, true));
    }

    [Fact]
    public void GridSkipsEmptyGroupsAndCountsOnlyVisibleHeadersInLazyIndexes()
    {
        var grid = new LibraryCollectionsGrid([
            new("Empty", []),
            new("First", [Collection("1"), Collection("2")]),
            new("", [Collection("3")]),
            new("Last", [Collection("4")]),
        ]);
        Assert.Equal([1, 2, 3, 5], grid.Cells.Select(cell => cell.GridIndex));
        Assert.Equal([1, 1, 1, 3], grid.Cells.Select(cell => cell.Row));
        Assert.Equal([0, 1, 2, 0], grid.Cells.Select(cell => cell.Column));
        Assert.Equal(["First", null, "Last", null], grid.Rows.Select(row => row.Header));
        Assert.Equal("3", grid.Entry("3")!.Collection.Id);
        Assert.Equal("1", grid.Entry("removed")!.Collection.Id);
        Assert.Null(new LibraryCollectionsGrid([new("Empty", [])]).Entry("removed"));
    }

    [Fact]
    public void GridNavigationPreservesColumnsAcrossHeadersAndNeverWrapsOrFillsMissingCells()
    {
        var grid = new LibraryCollectionsGrid([
            new("First", Enumerable.Range(0, 8).Select(id => Collection(id.ToString())).ToList()),
            new("Second", Enumerable.Range(8, 7).Select(id => Collection(id.ToString())).ToList()),
        ]);
        Assert.Equal(5, grid.Move(5, GridDirection.Right));
        Assert.Equal(6, grid.Move(6, GridDirection.Left));
        Assert.Equal(5, grid.Move(5, GridDirection.Down));
        Assert.Equal(9, grid.Move(7, GridDirection.Down));
        Assert.Equal(7, grid.Move(9, GridDirection.Up));
        Assert.Equal(13, grid.Move(13, GridDirection.Down));
        Assert.Equal(14, grid.Move(14, GridDirection.Down));
        Assert.Equal(8, grid.Move(14, GridDirection.Up));
        Assert.Equal(0, grid.Move(0, GridDirection.Up));
    }

    [Theory]
    [InlineData(65, 195, 540, 94, -29)]
    [InlineData(94, 195, 540, 94, 0)]
    [InlineData(300, 195, 540, 94, 73.8)]
    public void GridRevealRespectsTheTopBarAndOfficialGutters(double offset, double size, double viewport, double inset, double expected) =>
        Assert.Equal(expected, LibraryCollectionsGrid.ScrollDistance(offset, size, viewport, inset), precision: 6);

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "movie")]
    public async Task DetailDefaultsToCollectionOrderAndUsesTheCorrectSourceAndScope(bool user, string? mediaScope)
    {
        ServerAddress.TryParse("silo.example.com", out var server);
        var stub = new StubHandler().Enqueue("""{"items":[{"content_id":"1","title":"One","type":"movie"}],"page":{"has_more":false}}""")
            .Enqueue("""{"genres":["Drama"]}""");
        using var client = new SiloClient(server, new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), stub);
        var source = LibraryCatalogSource.Collection("7", mediaScope, Collection("12", user), client.LibraryBrowse);
        await source.LoadPageAsync(client.Browse, ArtworkWidths.Tv1080, null);
        await source.FiltersAsync();
        var query = Query(stub.Requests[0]);
        Assert.Equal(user ? "user_collection" : "library_collection", query["source"]);
        Assert.Equal("12", query["collection_id"]);
        Assert.Equal("60", query["limit"]);
        Assert.False(query.ContainsKey("sort"));
        Assert.Equal(mediaScope is not null, query.ContainsKey("library_id"));
        if (mediaScope is not null)
        {
            Assert.Equal("movie", query["type"]);
            Assert.Equal("7", query["library_id"]);
        }
        Assert.True(source.IsCollection);
        Assert.Equal(LibrarySortOption.CollectionOrder, source.Query.Filter.Sort);
        var selected = source.Query.Filter.SelectSort(LibrarySortOption.CollectionOrder);
        Assert.Equal(selected, selected.SelectSort(LibrarySortOption.CollectionOrder));
        source.SetFilter(selected.SelectSort(LibrarySortOption.Title));
        Assert.False(source.ShowsAlphabet);
        var facets = Query(stub.Requests[1]);
        Assert.Equal(user ? "user_collection" : "library_collection", facets["source"]);
        Assert.Equal("12", facets["collection_id"]);
        Assert.False(facets.ContainsKey("library_id"));
        Assert.Equal(LibrarySortOption.CollectionOrder, LibrarySortOption.For(mediaScope, collection: true)[0]);
    }

    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?')
        .Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
}
