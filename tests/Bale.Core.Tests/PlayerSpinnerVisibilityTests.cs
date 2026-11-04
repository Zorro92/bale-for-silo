using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlayerSpinnerVisibilityTests
{
    [Fact]
    public void ShortLoadsNeverShowTheSpinner()
    {
        var spinner = new PlayerSpinnerVisibility();
        Assert.False(spinner.Update(true, 0));
        Assert.False(spinner.Update(true, 499));
        Assert.False(spinner.Update(false, 500));
        Assert.False(spinner.Update(false, 1000));
    }

    [Fact]
    public void AContinuousLoadShowsAfterHalfASecond()
    {
        var spinner = new PlayerSpinnerVisibility();
        Assert.False(spinner.Update(true, 100));
        Assert.False(spinner.Update(true, 599));
        Assert.True(spinner.Update(true, 600));
        Assert.True(spinner.Update(true, 2000));
    }

    [Fact]
    public void AShownSpinnerStaysForAtLeastHalfASecond()
    {
        var spinner = new PlayerSpinnerVisibility();
        spinner.Update(true, 0);
        Assert.True(spinner.Update(true, 500));
        Assert.True(spinner.Update(false, 501));
        Assert.True(spinner.Update(false, 999));
        Assert.False(spinner.Update(false, 1000));
    }

    [Fact]
    public void EachNewLoadMustBeContinuous()
    {
        var spinner = new PlayerSpinnerVisibility();
        spinner.Update(true, 0);
        spinner.Update(false, 400);
        Assert.False(spinner.Update(true, 450));
        Assert.False(spinner.Update(true, 949));
        Assert.True(spinner.Update(true, 950));
    }

    [Fact]
    public void LoadingAgainDuringTheMinimumVisibilityCancelsHiding()
    {
        var spinner = new PlayerSpinnerVisibility();
        spinner.Update(true, 0);
        spinner.Update(true, 500);
        spinner.Update(false, 600);
        Assert.True(spinner.Update(true, 700));
        Assert.True(spinner.Update(true, 1200));
        Assert.False(spinner.Update(false, 1201));
        Assert.False(spinner.Update(true, 1300));
        Assert.True(spinner.Update(true, 1800));
    }
}
