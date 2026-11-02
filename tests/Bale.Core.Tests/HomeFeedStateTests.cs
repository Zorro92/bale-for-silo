using Bale.Core.Browse;

namespace Bale.Core.Tests;

public class HomeFeedStateTests
{
    private static MediaRow Row(string id = "recent") => new(id, id, "recently_added", false, CardShape.Poster,
        [new MediaCard("m1", "movie", "One", null, null, null, null, null, null, null, null, null, null, [], null, null, null)]);

    private static HomeFeedState Loaded(params MediaRow[] rows)
    {
        var state = new HomeFeedState();
        Assert.Null(state.OnShown());
        Assert.True(state.Apply(state.BeginLoad()!, rows));
        return state;
    }

    [Fact]
    public void FirstShownDoesNotDuplicateAnInitialRead()
    {
        var state = new HomeFeedState();
        var initial = state.BeginLoad()!;
        Assert.Equal(HomeFeedStatus.Loading, state.Status);
        Assert.Null(state.OnShown());
        Assert.Null(state.BeginLoad());
        Assert.True(state.Apply(initial, [Row()]));
        Assert.Null(state.OnShown());
        Assert.Null(state.TakePendingRefresh());
        Assert.False(state.IsBusy);
        Assert.Equal(HomeFeedStatus.Content, state.Status);
    }

    [Fact]
    public void InitialResultBeforeLoadedDoesNotCauseASecondRead()
    {
        var state = new HomeFeedState();
        Assert.True(state.Apply(state.BeginLoad()!, [Row()]));
        Assert.Null(state.OnShown());
        Assert.Null(state.TakePendingRefresh());
    }

    [Fact]
    public void DetailReturnWaitsForFocusBeforeStartingItsQuietRead()
    {
        var row = Row();
        var state = Loaded(row);
        state.OnHidden();
        Assert.Null(state.OnShown(deferUntilFocusRestored: true));
        Assert.Null(state.OnShown());
        Assert.Null(state.RequestRefresh());
        Assert.False(state.IsBusy);
        var refresh = Assert.IsType<HomeLoadRequest>(state.OnFocusRestored());
        Assert.True(refresh.Quiet);
        Assert.Same(row, Assert.Single(state.Rows));
        Assert.Equal(HomeFeedStatus.Content, state.Status);
        Assert.Null(state.OnFocusRestored());
    }

    [Fact]
    public void ReturningFromBackgroundRefreshesOnlyOnceWithoutAContentFocusMove()
    {
        var state = Loaded(Row());
        state.OnHidden();
        var refresh = Assert.IsType<HomeLoadRequest>(state.OnShown());
        Assert.True(refresh.Quiet);
        Assert.Null(state.OnShown());
        Assert.False(state.IsLoading);
        Assert.Equal(HomeFeedStatus.Content, state.Status);
    }

    [Fact]
    public void AResumeDuringTheFirstLoadDoesNotRefetchIt()
    {
        var state = new HomeFeedState();
        var initial = state.BeginLoad()!;
        state.OnShown();
        state.OnHidden();
        Assert.Null(state.OnShown());
        Assert.True(state.Apply(initial, [Row()]));
        Assert.Null(state.TakePendingRefresh());
    }

    [Fact]
    public void FreshnessSignalsCoalesceIntoOneFollowupRead()
    {
        var state = Loaded(Row());
        var refresh = state.RequestRefresh()!;
        Assert.Null(state.RequestRefresh());
        Assert.Null(state.RequestRefresh());
        Assert.Null(state.BeginLoad());
        Assert.True(state.Apply(refresh, [Row("new")]));
        var followup = Assert.IsType<HomeLoadRequest>(state.TakePendingRefresh());
        Assert.True(followup.Quiet);
        Assert.True(followup.Generation > refresh.Generation);
        Assert.True(state.Apply(followup, [Row("latest")]));
        Assert.Null(state.TakePendingRefresh());
    }

    [Fact]
    public void HiddenRefreshCannotPublishOverItsSuccessor()
    {
        var state = Loaded(Row());
        var old = state.RequestRefresh()!;
        state.OnHidden();
        Assert.False(state.IsCurrent(old));
        Assert.False(state.IsBusy);
        var current = state.OnShown()!;
        Assert.True(state.IsCurrent(current));
        Assert.False(state.Apply(old, []));
        Assert.False(state.Fail(old, new HttpRequestException()));
        Assert.True(state.Apply(current, [Row("new")]));
        Assert.Equal("new", Assert.Single(state.Rows).Id);
        Assert.Null(state.Error);
    }

    [Fact]
    public void HiddenSignalsWaitUntilHomeIsShown()
    {
        var state = Loaded(Row());
        state.OnHidden();
        Assert.Null(state.RequestRefresh());
        Assert.Null(state.OnFocusRestored());
        Assert.Null(state.TakePendingRefresh());
        Assert.False(state.IsBusy);
        Assert.True(state.OnShown()!.Quiet);
    }

    [Fact]
    public void QuietFailureKeepsRowsAndDoesNotBecomeABlockingError()
    {
        var row = Row();
        var state = Loaded(row);
        Assert.True(state.Fail(state.RequestRefresh()!, new HttpRequestException()));
        Assert.Same(row, Assert.Single(state.Rows));
        Assert.Equal(HomeFeedStatus.Content, state.Status);
        Assert.Null(state.Error);
        Assert.False(state.IsLoading);
    }

    [Fact]
    public void EmptyRefreshStaysEmptyWithoutASpinnerAndCanThenShowAnError()
    {
        var state = Loaded();
        Assert.Equal(HomeFeedStatus.Empty, state.Status);
        var refresh = state.RequestRefresh()!;
        Assert.False(state.IsLoading);
        Assert.Equal(HomeFeedStatus.Empty, state.Status);
        Assert.True(state.Fail(refresh, new HttpRequestException()));
        Assert.Equal(HomeFeedStatus.Error, state.Status);
        Assert.Equal("Network error. Check your connection.", state.Error);
        var retry = state.BeginLoad()!;
        Assert.False(retry.Quiet);
        Assert.Equal(HomeFeedStatus.Loading, state.Status);
        Assert.Null(state.Error);
        Assert.True(state.Apply(retry, [Row()]));
        Assert.Equal(HomeFeedStatus.Content, state.Status);
    }

    [Fact]
    public void EmptyRowsAreRemovedAndOptimisticDismissalUpdatesTheEmptyState()
    {
        var row = Row();
        var state = Loaded(row, row with { Id = "empty", Cards = [] });
        Assert.Single(state.Rows);
        state.SetRows([]);
        Assert.Equal(HomeFeedStatus.Empty, state.Status);
    }

    [Fact]
    public void FirstLoadMayFinishWhileHiddenWithoutStartingAnotherRead()
    {
        var state = new HomeFeedState();
        var initial = state.BeginLoad()!;
        state.OnShown();
        state.OnHidden();
        Assert.True(state.Apply(initial, [Row()]));
        Assert.Null(state.TakePendingRefresh());
        Assert.False(state.IsShown);
        Assert.True(state.OnShown()!.Quiet);
    }
}
