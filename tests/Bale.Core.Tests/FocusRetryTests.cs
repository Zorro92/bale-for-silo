using Bale.Xbox.Services;
using Windows.UI.Xaml;

namespace Bale.Core.Tests;

public class FocusRetryTests
{
    [Fact]
    public async Task ImmediateFocusNeedsNoLayoutBeforeThePageCanFade()
    {
        var page = new FrameworkElement();
        var calls = 0;
        FocusRetry.Until(page, () => ++calls == 1);

        await FocusRetry.WhenSettledAsync(page, CancellationToken.None);
        page.Layout();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PageStaysHiddenUntilItsRememberedCardCanTakeFocus()
    {
        var page = new FrameworkElement();
        var card = new FrameworkElement { Parent = page };
        var ready = false;
        var focused = false;
        FocusRetry.Until(card, () => focused = ready);
        var settled = FocusRetry.WhenSettledAsync(page, CancellationToken.None);

        Assert.False(settled.IsCompleted);
        card.Layout();
        Assert.False(settled.IsCompleted);
        ready = true;
        card.Layout();
        await settled;
        Assert.True(focused);
    }

    [Fact]
    public async Task PendingFocusInAnotherFrameDoesNotHoldUpThisPage()
    {
        var outgoing = new FrameworkElement();
        var incoming = new FrameworkElement();
        var calls = 0;
        FocusRetry.Until(outgoing, () => { calls++; return false; });

        await FocusRetry.WhenSettledAsync(incoming, CancellationToken.None);
        outgoing.Unload();
        outgoing.Layout();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExhaustedRetriesStopAndReleaseThePage()
    {
        var page = new FrameworkElement();
        var calls = 0;
        FocusRetry.Until(page, () => { calls++; return false; }, attempts: 2);
        var settled = FocusRetry.WhenSettledAsync(page, CancellationToken.None);

        page.Layout();
        Assert.False(settled.IsCompleted);
        page.Layout();
        await settled;
        page.Layout();
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task UnloadingStopsRetriesInsteadOfStealingFocusFromTheNextPage()
    {
        var page = new FrameworkElement();
        var calls = 0;
        FocusRetry.Until(page, () => { calls++; return false; });
        var settled = FocusRetry.WhenSettledAsync(page, CancellationToken.None);

        page.Unload();
        await settled;
        page.Layout();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RedirectCancelsWaitingForReturnFocus()
    {
        var page = new FrameworkElement();
        using var redirect = new CancellationTokenSource();
        FocusRetry.Until(page, () => false);
        var settled = FocusRetry.WhenSettledAsync(page, redirect.Token);

        redirect.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settled);
        page.Unload();
    }

    [Fact]
    public async Task RetryStartedByAnotherRetryMustAlsoSettleBeforeTheFade()
    {
        var page = new FrameworkElement();
        var firstReady = false;
        var secondReady = false;
        FocusRetry.Until(page, () =>
        {
            if (!firstReady) return false;
            FocusRetry.Until(page, () => secondReady);
            return true;
        });
        var settled = FocusRetry.WhenSettledAsync(page, CancellationToken.None);

        firstReady = true;
        page.Layout();
        Assert.False(settled.IsCompleted);
        secondReady = true;
        page.Layout();
        await settled;
    }

    [Fact]
    public async Task MissingLayoutCannotLockInputOrMoveFocusAfterTheFadeStarts()
    {
        var page = new FrameworkElement();
        var calls = 0;
        FocusRetry.Until(page, () => { calls++; return false; });

        await FocusRetry.WhenSettledAsync(page, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        page.Layout();
        Assert.Equal(1, calls);
    }
}
