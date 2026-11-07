using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class DisplayMatchPolicyTests
{
    [Theory]
    [InlineData(23.976, 23.976)]
    [InlineData(23.976, 24)]
    [InlineData(24, 24)]
    [InlineData(25, 50)]
    [InlineData(59.94, 59.94)]
    [InlineData(59.94, 60)]
    [InlineData(60, 60)]
    public void CleanCadenceBeats60HzPulldown(double fps, double expectedRefresh)
    {
        var current = Mode(1920, 1080, 60);
        Assert.Equal(1, DisplayMatchPolicy.PickMode(current,
            [Mode(1920, 1080, fps >= 50 ? 50 : 60), Mode(1920, 1080, expectedRefresh)], 1920, 1080, fps));
    }

    [Fact]
    public void ClosestIntegerMultipleWinsOverHigherMultiples()
    {
        Assert.Equal(2, Pick([Mode(1920, 1080, 60), Mode(1920, 1080, 48), Mode(1920, 1080, 23.976)], fps: 23.976));
        Assert.Equal(1, Pick([Mode(1920, 1080, 50), Mode(1920, 1080, 25), Mode(1920, 1080, 60)], fps: 25));
        Assert.Equal(1, Pick([Mode(1920, 1080, 60), Mode(1920, 1080, 59.94)], fps: 59.94));
    }

    [Fact]
    public void IntegerMultipleClassWinsEvenWithALargerRawDelta()
    {
        Assert.Equal(1, Pick([Mode(1920, 1080, 26), Mode(1920, 1080, 48)], fps: 24));
        Assert.Equal(1, Pick([Mode(1920, 1080, 23), Mode(1920, 1080, 50)], fps: 25));
        Assert.Equal(1, Pick([Mode(1920, 1080, 10), Mode(1920, 1080, 50)], fps: 25));
    }

    [Fact]
    public void ExactResolutionComesBeforeRefreshAndLargerArea()
    {
        Assert.Equal(1, Pick([Mode(3840, 2160, 24), Mode(1920, 1080, 60)], fps: 24));
    }

    [Fact]
    public void FourKContentOnA1080pDisplayUsesOnlyTheLargestModes()
    {
        Assert.Equal(2, Pick([Mode(1280, 720, 24), Mode(1920, 1080, 60), Mode(1920, 1080, 50)],
            width: 3840, height: 2160, fps: 25));
    }

    [Fact]
    public void CroppedFourKRequiresAHoldingResolutionEvenWhenHdHasBetterRefresh()
    {
        Assert.Equal(2, Pick([Mode(1920, 1080, 24), Mode(3840, 2160, 60), Mode(3840, 2160, 24)],
            width: 3840, height: 1600, fps: 24));
    }

    [Theory]
    [InlineData(24, 24)]
    [InlineData(24, 24.004)]
    [InlineData(60, 60)]
    public void TiesPreferSixteenByNineOverDci(double dciRefresh, double tvRefresh)
    {
        Assert.Equal(1, Pick([Mode(4096, 2160, dciRefresh), Mode(3840, 2160, tvRefresh)],
            width: 3840, height: 1600, fps: 24));
    }

    [Fact]
    public void TieToleranceNeverLetsANonIntegerClassDisplaceAnIntegerMultiple()
    {
        // Straddle the 0.05 ratio boundary near 2x without letting the 0.005 Hz tie mix classes.
        Assert.Equal(0, Pick([Mode(4096, 2160, 48.751), Mode(3840, 2160, 48.749)],
            width: 3840, height: 1600, fps: 25));
    }

    [Fact]
    public void OutsideTheTieToleranceBetterRefreshWinsEvenOverSixteenByNine()
    {
        Assert.Equal(0, Pick([Mode(4096, 2160, 24), Mode(3840, 2160, 24.006)],
            width: 3840, height: 1600, fps: 24));
    }

    [Fact]
    public void TiedHoldingModesPreferTheLargestAreaAfterAspectRatio()
    {
        Assert.Equal(1, Pick([Mode(1920, 1080, 24), Mode(3840, 2160, 24.004)], width: 1280, height: 700, fps: 24));
        Assert.Equal(1, Pick([Mode(2000, 1100, 24), Mode(4000, 2200, 24)], width: 1280, height: 700, fps: 24));
    }

    [Fact]
    public void CurrentModeOnlyBreaksOtherwiseEquivalentTies()
    {
        var current = Mode(1920, 1080, 24, pq: true);
        Assert.Equal(1, DisplayMatchPolicy.PickMode(current, [Mode(1920, 1080, 24), current], 1920, 1080, 24));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NothingAbove60HzOrStereoEverWins(bool hdr10)
    {
        var current = Mode(3840, 2160, 120, pq: true);
        DisplayModeFacts[] modes =
        [
            current, Mode(1920, 1080, 60.001, pq: true), Mode(1920, 1080, 24, pq: true, stereo: true),
            Mode(3840, 2160, 50, pq: true),
        ];
        Assert.Equal(3, DisplayMatchPolicy.PickMode(current, modes, 1920, 1080, 24, hdr10));
        Assert.Null(DisplayMatchPolicy.PickMode(current, modes[..3], 1920, 1080, 24, hdr10));
    }

    [Fact]
    public void Hdr10FiltersToPqBeforeResolutionAndRefreshSelection()
    {
        DisplayModeFacts[] modes = [Mode(1920, 1080, 24), Mode(3840, 2160, 50, pq: true), Mode(3840, 2160, 24, pq: true)];
        Assert.Equal(0, Pick(modes, fps: 24));
        Assert.Equal(2, Pick(modes, fps: 24, hdr10: true));
        Assert.Null(Pick([modes[0]], fps: 24, hdr10: true));
        // Unlike the HDR-only policy, content matching can shrink to the TV's largest PQ mode.
        Assert.Equal(0, Pick([Mode(1920, 1080, 24, pq: true)], width: 3840, height: 2160, fps: 24, hdr10: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidFrameRatesAndModesAreRejected(double invalid)
    {
        Assert.Null(Pick([Mode(1920, 1080, 60)], fps: invalid));
        Assert.Null(Pick([Mode(1920, 1080, invalid)]));
    }

    [Fact]
    public void EmptyOrUnknownDimensionsNeverPickAMode()
    {
        Assert.Null(Pick([]));
        Assert.Null(Pick([Mode(1920, 1080, 60)], width: 0));
        Assert.Null(Pick([Mode(1920, 1080, 60)], height: -1));
        Assert.Null(Pick([Mode(0, 1080, 60), Mode(1920, 0, 60)]));
    }

    private static int? Pick(IReadOnlyList<DisplayModeFacts> modes, long width = 1920, long height = 1080,
        double fps = 24, bool hdr10 = false) => DisplayMatchPolicy.PickMode(Mode(1920, 1080, 60), modes, width, height, fps, hdr10);

    private static DisplayModeFacts Mode(uint width, uint height, double refresh, bool pq = false, bool stereo = false) =>
        new(pq, false, stereo) { Width = width, Height = height, RefreshRate = refresh };
}
