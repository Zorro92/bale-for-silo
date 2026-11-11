using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PgsSubtitleRangeTests
{
    [Theory]
    [InlineData(0, 0, 180)]
    [InlineData(20, 0, 180)]
    [InlineData(1500, 1470, 1650)]
    public void WindowsHavePrerollBoundedDurationAndRefreshBeforeTheirTail(double source, double start, double end)
    {
        var range = PgsSubtitleRange.At(source);
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
        Assert.True(range.Contains(source));
        Assert.False(range.NeedsRefresh(source, null));
        Assert.True(range.NeedsRefresh(end - 45, null));
        Assert.False(range.NeedsRefresh(end - 45, end - 10)); // Last window reaches EOF.
        Assert.True(range.NeedsRefresh(end, end - 10));
        Assert.True(range.NeedsRefresh(start - 1, null));
    }

    [Fact]
    public void HlsOriginIsAppliedOnceForResumeReanchorStartOverAndBothSeekDirections()
    {
        var mounted = new PlaybackTimeline(1500, 8, 1492);
        var source = mounted.SourceForPlayer(8)!.Value;
        var range = PgsSubtitleRange.At(source);
        Assert.Equal(1470, range.Start);
        Assert.Equal(1_500_000, SubtitleClockPush.Milliseconds(source));
        Assert.False(range.NeedsRefresh(mounted.SourceForPlayer(38)!.Value, null));
        Assert.False(range.NeedsRefresh(mounted.SourceForPlayer(0)!.Value, null));
        // A reanchor gives a new player origin. SUP PTS still uses source 1800, even paused.
        mounted = new(1800, 4, 1796);
        Assert.Equal(1800, mounted.SourceForPlayer(4));
        Assert.True(range.NeedsRefresh(mounted.SourceForPlayer(4)!.Value, null));
        range = PgsSubtitleRange.At(mounted.SourceForPlayer(4)!.Value);
        Assert.Equal(1770, range.Start);
        // Start over remounts and reloads coverage from source zero, ignoring the old origin.
        mounted = new(0, 0, 0);
        Assert.True(range.NeedsRefresh(mounted.SourceForPlayer(0)!.Value, null));
        Assert.Equal(new(0,180), PgsSubtitleRange.At(mounted.SourceForPlayer(0)!.Value));
    }
}
