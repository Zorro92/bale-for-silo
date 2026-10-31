using Bale.Core.Browse;

namespace Bale.Core.Tests;

public class CatalogGridTests
{
    private static MediaCard Card(string id, string type = "movie") =>
        new(id, type, id, null, null, null, null, null, null, null, null, null, null, [], null, null, null);

    private static SearchResultPage Page(bool more, params MediaCard[] cards) =>
        new(cards, 100, true, more ? new SearchContinuation("next", ["next"]) : null);

    [Theory]
    [InlineData(false, 1, true, true)]
    [InlineData(false, 20, true, true)]
    [InlineData(true, 1, true, false)]
    [InlineData(false, 0, true, false)]
    [InlineData(false, 1, false, false)]
    public void EveryNonemptyNonfeaturedSectionOffersExploreEvenWhenShort(bool featured, int count, bool section, bool expected)
    {
        var row = new MediaRow("row", "Row", "recent", featured, CardShape.Poster,
            Enumerable.Range(1, count).Select(id => Card(id.ToString())).ToList())
        {
            ExploreSource = section ? new SectionBrowseSource("row", "Row") : null,
        };

        Assert.Equal(expected, row.OffersExploreAll);
    }

    [Theory]
    [InlineData("for_you", "Anything", true)]
    [InlineData("FORYOU", "Anything", true)]
    [InlineData("recommendations", "Anything", true)]
    [InlineData("custom", "For You", true)]
    [InlineData("custom", "RECOMMENDED FOR YOU", true)]
    [InlineData("recent", "Recently Added", false)]
    public void SeeAllIsOnlyTheHomeForYouShortcut(string type, string title, bool expected)
    {
        var row = new MediaRow("row", title, type, false, CardShape.Poster, [Card("1")])
        {
            ExploreSource = new SectionBrowseSource("row", title),
        };

        Assert.Equal(expected, row.OffersSeeAll);
        Assert.False((row with { ExploreSource = new SectionBrowseSource("row", title, "12") }).OffersSeeAll);
        Assert.False((row with { ExploreSource = null }).OffersSeeAll);
    }

    [Theory]
    [InlineData("audiobook", 1)]
    [InlineData(" Audiobooks ", 1)]
    [InlineData("movie", 2d / 3)]
    [InlineData("series", 2d / 3)]
    [InlineData("episode", 2d / 3)]
    public void ArtworkKeepsSquareAudiobookCovers(string type, double expected) =>
        Assert.Equal(expected, Card("1", type).PosterAspectRatio);

    [Fact]
    public void PagesDeduplicateInOrderWithoutReplacingExistingCards()
    {
        var state = new CatalogGridResults();
        var firstCard = Card("1");
        var first = state.Begin();
        Assert.True(state.IsLoading);
        Assert.False(state.HasLoaded);
        Assert.False(first.Append);
        Assert.Null(first.Continuation);
        Assert.Null(state.BeginMore());
        Assert.True(state.Apply(first, Page(true, firstCard, Card("1"), Card("2"))));
        var more = state.BeginMore()!;
        Assert.True(more.Append);
        Assert.Same(state.Continuation, more.Continuation);
        Assert.Null(state.BeginMore());

        Assert.True(state.Apply(more, Page(false, Card("2"), Card("3"))));

        Assert.Equal(["1", "2", "3"], state.Items.Select(card => card.ContentId));
        Assert.Same(firstCard, state.Items[0]);
        Assert.True(state.HasLoaded);
        Assert.False(state.IsLoading);
        Assert.False(state.HasMore);
        Assert.Null(state.BeginMore());
        Assert.Null(state.Error);
        Assert.Null(state.LoadMoreError);
    }

    [Fact]
    public void InitialFailureAndRetryDoNotBecomeAPagingError()
    {
        var state = new CatalogGridResults();

        Assert.True(state.Fail(state.Begin()));

        Assert.Equal("Couldn't load this row.", state.Error);
        Assert.Null(state.LoadMoreError);
        Assert.False(state.HasLoaded);
        Assert.False(state.IsLoading);
        Assert.Null(state.BeginMore(retry: true));
        var retry = state.Begin();
        Assert.Null(state.Error);
        Assert.True(state.IsLoading);
        Assert.True(state.Apply(retry, Page(false)));
        Assert.True(state.HasLoaded);
        Assert.Empty(state.Items);
        Assert.Null(state.Error);
    }

    [Fact]
    public void FailedAppendKeepsCardsAndCursorAndRequiresExplicitRetry()
    {
        var state = new CatalogGridResults();
        state.Apply(state.Begin(), Page(true, Card("1")));
        var more = state.BeginMore()!;

        Assert.True(state.Fail(more));

        Assert.Equal("1", Assert.Single(state.Items).ContentId);
        Assert.Null(state.Error);
        Assert.Equal("Couldn't load more", state.LoadMoreError);
        Assert.True(state.HasMore);
        Assert.False(state.IsLoading);
        Assert.Null(state.BeginMore());
        var retry = state.BeginMore(retry: true)!;
        Assert.Same(more.Continuation, retry.Continuation);
        Assert.Null(state.LoadMoreError);
        Assert.True(state.Apply(retry, Page(false, Card("2"))));
        Assert.Equal(["1", "2"], state.Items.Select(card => card.ContentId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateOnlyPagesPauseAutomaticPagingOnlyWhenMorePagesRemain(bool hasMore)
    {
        var state = new CatalogGridResults();
        state.Apply(state.Begin(), Page(true, Card("1")));

        state.Apply(state.BeginMore()!, Page(hasMore, Card("1")));

        Assert.Single(state.Items);
        Assert.Null(state.Error);
        Assert.Null(state.BeginMore());
        Assert.Equal(hasMore, state.LoadMoreError is not null);
        Assert.Equal(hasMore, state.BeginMore(retry: true) is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotFoundMarksTheSectionUnavailableRatherThanRetryable(bool append)
    {
        var state = new CatalogGridResults();
        var request = state.Begin();
        if (append)
        {
            state.Apply(request, Page(true, Card("1")));
            request = state.BeginMore()!;
        }

        Assert.True(state.Fail(request, notFound: true));

        Assert.True(state.Unavailable);
        Assert.False(state.HasMore);
        Assert.False(state.IsLoading);
        Assert.Null(state.Continuation);
        Assert.Null(state.Error);
        Assert.Null(state.LoadMoreError);
        Assert.Null(state.BeginMore(retry: true));
        state.Begin();
        Assert.False(state.Unavailable);
        Assert.Empty(state.Items);
    }

    [Fact]
    public void ReloadClearsCardsAndRejectsThePreviousQueriesSuccessAndFailure()
    {
        var state = new CatalogGridResults();
        state.Apply(state.Begin(), Page(true, Card("old")));
        var old = state.BeginMore()!;

        var current = state.Begin();

        Assert.Empty(state.Items);
        Assert.Null(state.Continuation);
        Assert.True(state.IsLoading);
        Assert.False(state.HasLoaded);
        Assert.False(state.Apply(old, Page(false, Card("stale"))));
        Assert.False(state.Fail(old, notFound: true));
        Assert.True(state.IsLoading);
        Assert.False(state.Unavailable);
        Assert.True(state.Apply(current, Page(false, Card("current"))));
        Assert.Equal("current", Assert.Single(state.Items).ContentId);
    }

    [Fact]
    public void LeavingRetainsPagesForBackButFencesInFlightRequests()
    {
        var state = new CatalogGridResults();
        var card = Card("1");
        state.Apply(state.Begin(), Page(true, card));
        var interrupted = state.BeginMore()!;

        state.StopLoading();

        Assert.True(state.HasLoaded);
        Assert.True(state.HasMore);
        Assert.False(state.IsLoading);
        Assert.Same(card, Assert.Single(state.Items));
        Assert.Same(interrupted.Continuation, state.Continuation);
        Assert.False(state.Apply(interrupted, Page(false, Card("stale"))));
        Assert.False(state.Fail(interrupted));
        var resumed = state.BeginMore()!;
        Assert.Same(interrupted.Continuation, resumed.Continuation);
        Assert.True(state.Apply(resumed, Page(false, Card("2"))));
        Assert.Equal(["1", "2"], state.Items.Select(item => item.ContentId));
    }

    [Fact]
    public void MediaActionsUpdateTheRetainedCardWithoutChangingPagingOrAddingUnknownCards()
    {
        var state = new CatalogGridResults();
        state.Apply(state.Begin(), Page(true, Card("1"), Card("2")));
        var continuation = state.Continuation;
        var updated = state.Items[0].WithPlayed(true);

        state.UpdateCard(updated);
        state.UpdateCard(Card("unknown"));

        Assert.Same(updated, state.Items[0]);
        Assert.True(state.Items[0].Flags.Played);
        Assert.Equal(["1", "2"], state.Items.Select(card => card.ContentId));
        Assert.Same(continuation, state.Continuation);
        Assert.True(state.HasMore);
    }

    [Theory]
    [InlineData(0, 14, 6, GridDirection.Left, 0)]
    [InlineData(5, 14, 6, GridDirection.Right, 5)]
    [InlineData(6, 14, 6, GridDirection.Left, 6)]
    [InlineData(13, 14, 6, GridDirection.Right, 13)]
    [InlineData(0, 14, 6, GridDirection.Up, 0)]
    [InlineData(5, 14, 6, GridDirection.Up, 5)]
    [InlineData(1, 14, 6, GridDirection.Right, 2)]
    [InlineData(7, 14, 6, GridDirection.Left, 6)]
    [InlineData(2, 14, 6, GridDirection.Down, 8)]
    [InlineData(7, 14, 6, GridDirection.Down, 13)]
    [InlineData(8, 14, 6, GridDirection.Down, 8)]
    [InlineData(11, 14, 6, GridDirection.Down, 11)]
    [InlineData(13, 14, 6, GridDirection.Up, 7)]
    [InlineData(8, 14, 6, GridDirection.Up, 2)]
    [InlineData(-1, 14, 6, GridDirection.Right, -1)]
    [InlineData(5, 0, 6, GridDirection.Down, 5)]
    [InlineData(4, 20, 0, GridDirection.Down, 4)]
    [InlineData(3, 20, -1, GridDirection.Right, 3)]
    public void GridEdgesNeverWrapOrSubstituteASidewaysCard(int index, int count, int columns, GridDirection direction, int expected) =>
        Assert.Equal(expected, CatalogGridNavigation.Move(index, count, columns, direction));
}
