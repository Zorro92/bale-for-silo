using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackTimelineTests
{
    [Fact]
    public void OffsetMapsBothClocks()
    {
        var timeline = PlaybackTimeline.From(new TimelineV3 { TimelineOffsetSeconds = 120 });
        Assert.Equal(130, timeline.SourceForPlayer(10));
        Assert.Equal(10, timeline.PlayerForSource(130));
        Assert.Null(timeline.PlayerForSource(10));
        Assert.Null(new PlaybackTimeline(0, 0, -120).SourceForPlayer(10));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidPositionsDoNotMap(double position)
    {
        var timeline = new PlaybackTimeline(120, 0, 120);
        Assert.Null(timeline.SourceForPlayer(position));
        Assert.Null(timeline.PlayerForSource(position));
    }

    [Fact]
    public void PlayerStartUsesThePlanUnlessExplicitlyStartingOver()
    {
        var timeline = new PlaybackTimeline(600, 10, 590);
        Assert.Equal(10, timeline.PlayerStart(null));
        Assert.Equal(0, timeline.PlayerStart(0));
        Assert.Equal(10, timeline.PlayerStart(600));
    }

    [Fact]
    public void SourceStartUsesTheRequestThenThePlan()
    {
        var timeline = new PlaybackTimeline(600, 10, 590);
        Assert.Equal(593, timeline.SourceStart(593, null));
        Assert.Equal(600, timeline.SourceStart(null, null));
        Assert.Equal(0, timeline.SourceStart(0, 0));
        Assert.Equal(10, new PlaybackTimeline(0, 10, 0).SourceStart(null, null));
    }

    [Fact]
    public void MissingTimelineIsAllZeros() => Assert.Equal(new PlaybackTimeline(0, 0, 0), PlaybackTimeline.From(null));

    [Fact]
    public void InvalidPlanCoordinatesAreSanitizedButNegativeOffsetsAreValid()
    {
        Assert.Equal(new PlaybackTimeline(0, 0, 0), PlaybackTimeline.From(new TimelineV3
        {
            SourceStartSeconds = -1, PlayerStartSeconds = double.NaN, TimelineOffsetSeconds = double.PositiveInfinity,
        }));
        Assert.Equal(-120, PlaybackTimeline.From(new TimelineV3 { TimelineOffsetSeconds = -120 }).TimelineOffsetSeconds);
    }
}
