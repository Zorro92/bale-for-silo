using Bale.Core.Playback;

namespace Bale.Core.Tests;

// shared/.../model/playback/PlaybackStartPositionTest.kt, plus the TV start-request handoff.
public class PlaybackStartPositionTests
{
    [Theory]
    [InlineData(null, 318.5, 0, 318.5)]
    [InlineData(0.0, 318.5, 200, 0)]
    [InlineData(null, 0, 204, 204)]
    public void ResolvePrefersOverrideThenSessionThenDetail(double? explicitPosition, double session, double detail, double expected) =>
        Assert.Equal(expected, PlaybackStartPosition.Resolve(explicitPosition, session, detail));

    [Theory]
    [InlineData(null, 0, null)]
    [InlineData(null, 204, 204.0)]
    [InlineData(0.0, 204, 0.0)]
    public void RequestPositionOmitsEmptyProgressButKeepsStartOver(double? explicitPosition, double detail, double? expected) =>
        Assert.Equal(expected, PlaybackStartPosition.ResolveRequestPosition(explicitPosition, detail));

    [Theory]
    [InlineData(0, false, 7, 0)]
    [InlineData(600, false, 7, 593)]
    [InlineData(20, false, 7, 20)]
    [InlineData(31, false, 100, 0)]
    [InlineData(600, true, 7, 600)]
    [InlineData(600, false, 0, 600)]
    [InlineData(30, false, 7, 23)]
    [InlineData(double.NaN, false, 7, 0)]
    [InlineData(double.PositiveInfinity, false, 7, 0)]
    [InlineData(-5, false, 7, 0)]
    [InlineData(600, false, double.NaN, 600)]
    [InlineData(600, false, double.PositiveInfinity, 600)]
    public void ResumeRewindMatchesTheOfficialPolicy(double position, bool isExplicit, double rewind, double expected) =>
        Assert.Equal(expected, PlaybackStartPosition.ApplyResumeRewind(position, isExplicit, rewind));

    [Theory]
    [InlineData(null, 600.0, 593.0)]
    [InlineData(0.0, 600.0, 0.0)]
    [InlineData(null, 10.0, 10.0)]
    [InlineData(null, null, null)]
    public void StartRequestRewindsResumeButNotStartOver(double? explicitPosition, double? detail, double? expected) =>
        Assert.Equal(expected, PlaybackStartPosition.ForStartRequest(explicitPosition, detail));
}
