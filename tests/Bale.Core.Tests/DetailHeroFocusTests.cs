using Bale.Core.Detail;

namespace Bale.Core.Tests;

/// <summary>TVDetailActions.swift entryAction (Apple 067e6cee).</summary>
public class DetailHeroFocusTests
{
    private static Func<DetailHeroAction, bool> AllBut(params DetailHeroAction[] unavailable) =>
        action => !unavailable.Contains(action);

    [Fact]
    public void FirstEntryLandsOnPlay() =>
        Assert.Equal(DetailHeroAction.Play, DetailHeroFocus.Entry(null, AllBut()));

    [Theory]
    [InlineData(DetailHeroAction.Watchlist)]
    [InlineData(DetailHeroAction.Subtitles)]
    [InlineData(DetailHeroAction.Version)]
    [InlineData(DetailHeroAction.Edition)]
    [InlineData(DetailHeroAction.StartOver)]
    [InlineData(DetailHeroAction.More)]
    public void EntryReturnsToTheLastFocusedAction(DetailHeroAction last) =>
        Assert.Equal(last, DetailHeroFocus.Entry(last, AllBut()));

    [Fact]
    public void AnActionThatCannotTakeFocusFallsBackToPlay()
    {
        // Start Over once the title is no longer in progress; Version disabled while an episode's versions load.
        Assert.Equal(DetailHeroAction.Play, DetailHeroFocus.Entry(DetailHeroAction.StartOver, AllBut(DetailHeroAction.StartOver)));
        Assert.Equal(DetailHeroAction.Play, DetailHeroFocus.Entry(DetailHeroAction.Version, AllBut(DetailHeroAction.Version)));
        Assert.Equal(DetailHeroAction.Play, DetailHeroFocus.Entry(DetailHeroAction.Edition, AllBut(DetailHeroAction.Edition)));
    }

    [Fact]
    public void WatchlistStandsInForPlayWhilePlayCannotTakeFocus()
    {
        Assert.Equal(DetailHeroAction.Watchlist, DetailHeroFocus.Entry(null, AllBut(DetailHeroAction.Play)));
        Assert.Equal(DetailHeroAction.Watchlist,
            DetailHeroFocus.Entry(DetailHeroAction.Audio, AllBut(DetailHeroAction.Play, DetailHeroAction.Audio)));
        Assert.Equal(DetailHeroAction.Audio, DetailHeroFocus.Entry(DetailHeroAction.Audio, AllBut(DetailHeroAction.Play)));
    }
}
