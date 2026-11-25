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
    public void AQuietReadThatFailsTransientlyIsReadAgainAfterOneTwoAndFourSeconds()
    {
        var row = Row();
        var state = Loaded(row);
        var read = state.RequestRefresh()!;
        var delays = new List<TimeSpan>();
        for (var retry = 0; retry < HomeFeedState.MaxRetries; retry++)
        {
            Assert.Equal(retry, read.Retry);
            Assert.True(state.Fail(read, new HttpRequestException()));
            var waiting = Assert.IsType<HomeRetry>(state.PendingRetry);
            delays.Add(waiting.Delay);
            // HomeViewModel.kt: "The retry will cover any events that arrived while it waited."
            Assert.Null(state.RequestRefresh());
            Assert.Null(state.TakePendingRefresh());
            Assert.False(state.IsBusy);
            read = Assert.IsType<HomeLoadRequest>(state.StartRetry(waiting));
            Assert.True(read.Quiet);
            Assert.Null(state.PendingRetry);
            Assert.Null(state.StartRetry(waiting));
        }
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
        Assert.Equal(3, read.Retry);
        Assert.True(state.Fail(read, new Microsoft.Kiota.Abstractions.ApiException { ResponseStatusCode = 503 }));
        Assert.Null(state.PendingRetry);
        Assert.Null(state.TakePendingRefresh());
        // The rows stayed throughout, with no blocking error.
        Assert.Same(row, Assert.Single(state.Rows));
        Assert.Equal(HomeFeedStatus.Content, state.Status);
    }

    [Fact]
    public void ASuccessfulRetryEndsTheChainAndALaterFailureStartsANewOne()
    {
        var state = Loaded(Row());
        var read = state.RequestRefresh()!;
        Assert.True(state.Fail(read, new TimeoutException()));
        var retry = state.StartRetry(state.PendingRetry!)!;
        Assert.True(state.Apply(retry, [Row("new")]));
        Assert.Null(state.PendingRetry);
        var next = state.RequestRefresh()!;
        Assert.Equal(0, next.Retry);
        Assert.True(state.Fail(next, new HttpRequestException()));
        Assert.Equal(TimeSpan.FromSeconds(1), state.PendingRetry!.Delay);
    }

    public static TheoryData<Exception> LastingFailures => new()
    {
        new Microsoft.Kiota.Abstractions.ApiException { ResponseStatusCode = 401 },
        new Microsoft.Kiota.Abstractions.ApiException { ResponseStatusCode = 404 },
        new Bale.Core.Api.Models.Problem { ResponseStatusCode = 403, Detail = "Profile verification required" },
        new InvalidOperationException(),
    };

    [Theory]
    [MemberData(nameof(LastingFailures))]
    public void OnlyATransientFailureIsRetried(Exception error)
    {
        var state = Loaded(Row());
        Assert.True(state.Fail(state.RequestRefresh()!, error));
        Assert.Null(state.PendingRetry);
    }

    [Fact]
    public void TheInitialLoadAndAnExplicitRetryAreNotRetried()
    {
        var state = new HomeFeedState();
        Assert.Null(state.OnShown());
        Assert.True(state.Fail(state.BeginLoad()!, new HttpRequestException()));
        Assert.Null(state.PendingRetry);
        Assert.Equal(HomeFeedStatus.Error, state.Status);
        Assert.True(state.Fail(state.BeginLoad()!, new HttpRequestException()));
        Assert.Null(state.PendingRetry);
    }

    [Fact]
    public void HidingDropsAWaitingRetryAndTheReturnReadsAgain()
    {
        var state = Loaded(Row());
        Assert.True(state.Fail(state.RequestRefresh()!, new HttpRequestException()));
        var waiting = state.PendingRetry!;
        state.OnHidden();
        Assert.Null(state.PendingRetry);
        Assert.Null(state.StartRetry(waiting));
        var back = Assert.IsType<HomeLoadRequest>(state.OnShown());
        Assert.True(back.Quiet);
        Assert.Equal(0, back.Retry);
        // The old wait ending after the return starts nothing.
        Assert.Null(state.StartRetry(waiting));
    }

    [Fact]
    public void AnExplicitLoadReplacesAWaitingRetry()
    {
        var state = Loaded();
        Assert.True(state.Fail(state.RequestRefresh()!, new HttpRequestException()));
        Assert.Equal(HomeFeedStatus.Error, state.Status);
        var waiting = state.PendingRetry!;
        var load = Assert.IsType<HomeLoadRequest>(state.BeginLoad());
        Assert.False(load.Quiet);
        Assert.Null(state.PendingRetry);
        Assert.Null(state.StartRetry(waiting));
        Assert.True(state.Apply(load, [Row()]));
        Assert.Null(state.TakePendingRefresh());
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
