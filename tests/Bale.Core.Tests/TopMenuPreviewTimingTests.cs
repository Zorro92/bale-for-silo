using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class TopMenuPreviewTimingTests
{
    // shell/TvTopMenuBar.kt (Android 5d90cf32f): library/For You wait 120ms and swap at once while a menu is open;
    // profile always waits 120ms.
    [Theory]
    [InlineData(false, false, 120)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 120)]
    [InlineData(true, true, 120)]
    public void MatchesTheOfficialFirstSwitchAndProfileDelays(bool hasOpenPanel, bool isProfile, int milliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), TopMenuPreviewTiming.Delay(hasOpenPanel, isProfile));
    }
}
