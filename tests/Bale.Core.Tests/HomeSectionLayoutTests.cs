using System.Text.Json;
using Bale.Core.Browse;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

public class HomeSectionLayoutTests
{
    private static MediaRow Row(string id, bool populated = true) => new(id, id, "recent", false, CardShape.Poster,
        populated ? [new MediaCard(id, "movie", id, null, null, null, null, null, null, null, null, null, null, [], null, null, null)] : []);

    [Fact]
    public void UntouchedLayoutPreservesServerOrderAndOmitsUnpopulatedRows()
    {
        var rows = new[] { Row("b"), Row("empty", false), Row("a") };
        Assert.Equal(["b", "a"], new HomeSectionLayout().Arrange(rows).Select(row => row.Id));
    }

    [Fact]
    public void SavedOrderWinsAndNewRowsAppendInServerOrder()
    {
        var layout = new HomeSectionLayout { OrderedSectionIds = ["c", "c", "absent", "a", " "] };
        Assert.Equal(["c", "a", "b", "new"], layout.Arrange([Row("a"), Row("b"), Row("new"), Row("c")]).Select(row => row.Id));
    }

    [Fact]
    public void HidingRowsLeavesNoGapsButEditorStillIncludesThem()
    {
        var layout = new HomeSectionLayout().WithVisible("a", false);
        var rows = new[] { Row("a"), Row("b") };
        Assert.Equal("b", Assert.Single(layout.Arrange(rows)).Id);
        Assert.Equal(["a", "b"], layout.Arrange(rows, includingHidden: true).Select(row => row.Id));
        Assert.Equal(["a", "b"], layout.WithVisible("a", true).Arrange(rows).Select(row => row.Id));
        Assert.False(new HomeSectionLayout().HiddenSectionIds.Contains("a"));
    }

    [Fact]
    public void AllHiddenIsDifferentFromNoPopulatedServerRows()
    {
        var layout = new HomeSectionLayout().WithVisible("a", false);
        Assert.True(layout.AllHidden([Row("a")]));
        Assert.False(layout.AllHidden([]));
        Assert.False(layout.AllHidden([Row("a", false)]));
        Assert.False(layout.AllHidden([Row("a"), Row("new")]));
    }

    [Fact]
    public void ChangingOrderRetainsTemporarilyAbsentRowsWithoutDuplicates()
    {
        var layout = new HomeSectionLayout { OrderedSectionIds = ["absent", "b", "a", "absent"] };
        Assert.Equal(["a", "b", "absent"], layout.WithOrder(["a", "b", "a", " "]).OrderedSectionIds);
    }

    [Fact]
    public void MoveUsesVisibleAndHiddenRowsAndStopsAtEnds()
    {
        var rows = new[] { Row("a"), Row("b"), Row("c") };
        var layout = new HomeSectionLayout().WithVisible("b", false);
        var moved = layout.Move(rows, "b", -1);
        Assert.Equal(["b", "a", "c"], moved.OrderedSectionIds);
        Assert.Contains("b", moved.HiddenSectionIds);
        Assert.Same(moved, moved.Move(rows, "b", -1));
        Assert.Same(layout, layout.Move(rows, "c", 1));
        Assert.Same(layout, layout.Move(rows, "missing", 1));
        Assert.Equal(["a", "c", "b"], layout.Move(rows, "b", 1).OrderedSectionIds);
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Move(rows, "a", 2));
    }

    [Fact]
    public void LocalPayloadRoundTripsArbitraryIdsAndMapsOnlyLayoutFields()
    {
        var ids = new[] { "first:one", "a\nb", "é漢字", "\"quoted\"" };
        var layout = new HomeSectionLayout().WithOrder(ids).WithVisible(ids[2], false);
        using var payload = JsonDocument.Parse(layout.Serialize());
        Assert.Equal(["orderedSectionIds", "hiddenSectionIds"], payload.RootElement.EnumerateObject().Select(property => property.Name));
        var restored = HomeSectionLayout.Deserialize(layout.Serialize());
        Assert.Equal(ids, restored.OrderedSectionIds);
        Assert.Equal([ids[2]], restored.HiddenSectionIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"orderedSectionIds\":[],\"hiddenSectionIds\":12}")]
    [InlineData("{\"orderedSectionIds\":[12],\"hiddenSectionIds\":[]}")]
    public void CorruptPreferencesFallBackToVisibleServerOrder(string? payload)
    {
        var layout = HomeSectionLayout.Deserialize(payload);
        Assert.Empty(layout.OrderedSectionIds);
        Assert.Empty(layout.HiddenSectionIds);
    }

    [Fact]
    public void LocalPayloadNormalizesDuplicatesAndKeepsUnknownRowsForTheirReturn()
    {
        var layout = HomeSectionLayout.Deserialize("""{"orderedSectionIds":["a","a","","future"],"hiddenSectionIds":["future","future"," "]}""");
        Assert.Equal(["a", "future"], layout.OrderedSectionIds);
        Assert.Equal(["future"], layout.HiddenSectionIds);
    }

    [Fact]
    public void StorageAuthorityIsPerServerAndProfileAndDoesNotLeakEitherInTheKey()
    {
        var server = new Uri("https://home.invalid/base");
        var key = HomeSectionLayout.StorageKey(server, "one");
        Assert.Equal(key, HomeSectionLayout.StorageKey(new Uri("https://home.invalid/base/"), "one"));
        Assert.NotEqual(key, HomeSectionLayout.StorageKey(server, "two"));
        Assert.NotEqual(key, HomeSectionLayout.StorageKey(new Uri("https://other.invalid/base/"), "one"));
        Assert.NotEqual(key, HomeSectionLayout.StorageKey(new Uri("https://home.invalid/elsewhere/"), "one"));
        Assert.DoesNotContain("home.invalid", key);
    }

    [Fact]
    public void HomeRetainsHiddenRowsAcrossQuietFailuresAndUnhidesWithoutARead()
    {
        var state = new HomeFeedState();
        state.OnShown();
        state.Apply(state.BeginLoad()!, [Row("a")]);
        state.SetLayout(new HomeSectionLayout().WithVisible("a", false));
        Assert.True(state.SectionsHiddenByPreference);
        Assert.Equal(HomeFeedStatus.Empty, state.Status);
        Assert.Single(state.AvailableRows);
        var request = state.RequestRefresh()!;
        Assert.True(state.Fail(request, new HttpRequestException()));
        Assert.Null(state.Error);
        Assert.True(state.SectionsHiddenByPreference);
        state.SetLayout(state.Layout.WithVisible("a", true));
        Assert.Single(state.Rows);
        Assert.False(state.SectionsHiddenByPreference);
        Assert.Equal(HomeFeedStatus.Content, state.Status);
    }

    [Fact]
    public void ApplyingAReadProjectsTheLatestLayoutAndStillShowsNewRows()
    {
        var state = new HomeFeedState();
        var read = state.BeginLoad()!;
        state.SetLayout(new HomeSectionLayout().WithVisible("a", false));
        Assert.True(state.Apply(read, [Row("a"), Row("new")]));
        Assert.Equal("new", Assert.Single(state.Rows).Id);
        Assert.Equal(2, state.AvailableRows.Count);
        Assert.False(state.SectionsHiddenByPreference);
    }
}
