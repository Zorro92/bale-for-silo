using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class TopMenuPreviewTimingTests
{
    // shell/TvTopMenuBar.kt: library/For You use openPanel, but profile always waits 180ms.
    [Theory]
    [InlineData(false, false, 180)]
    [InlineData(true, false, 80)]
    [InlineData(false, true, 180)]
    [InlineData(true, true, 180)]
    public void MatchesTheOfficialFirstSwitchAndProfileDelays(bool hasOpenPanel, bool isProfile, int milliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), TopMenuPreviewTiming.Delay(hasOpenPanel, isProfile));
    }
}
