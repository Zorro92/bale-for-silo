using Bale.Core.Api.Models;
using Bale.Xbox.Services;
using Windows.Graphics.Display;
using Windows.Graphics.Display.Core;

namespace Bale.Core.Tests;

public class DisplayHdrOutputTests : IDisposable
{
    private readonly HdmiDisplayInformation _hdmi = new();
    private readonly HdmiDisplayMode _hdr = new() { IsSmpte2084Supported = true, Is2086MetadataSupported = true };

    public DisplayHdrOutputTests()
    {
        HdmiDisplayInformation.ForCurrentView = () => _hdmi;
        _hdmi.ReadModes = () => [new(), _hdr];
        Log.Messages.Clear();
    }

    public void Dispose()
    {
        HdmiDisplayInformation.ForCurrentView = () => null;
        DisplayInformation.ReadColor = () => new();
        Log.Messages.Clear();
    }

    [Fact]
    public void DetectionReportsTheConnectedDisplayWithoutChangingIt()
    {
        var capabilities = DisplayHdrOutput.DetectCapabilities();
        Assert.True(capabilities.Hdr.Hdr10);
        Assert.Equal("exact", capabilities.DisplayHdrEvidence);
        Assert.Equal(0, _hdmi.Switches);
        Assert.Equal(0, _hdmi.Restores);
        Assert.Equal("display HDR: read=True modes=2 pq=1 metadata=1 stereo=0 hdr10=True", Assert.Single(Log.Messages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOrThrowingReadIsUnknownAndNeverLogsTheException(bool throws)
    {
        HdmiDisplayInformation.ForCurrentView = () => throws ? throw new InvalidOperationException("private diagnostic") : null;
        var capabilities = DisplayHdrOutput.DetectCapabilities();
        Assert.False(capabilities.Hdr.Hdr10);
        Assert.Equal("unknown", capabilities.DisplayHdrEvidence);
        Assert.Equal("display HDR: read=False modes=0 pq=0 metadata=0 stereo=0 hdr10=False", Assert.Single(Log.Messages));
    }

    [Fact]
    public void EnumerationFailureDiscardsTheDisplayClaim()
    {
        _hdmi.ReadModes = () => throw new InvalidOperationException("private diagnostic");
        var capabilities = DisplayHdrOutput.DetectCapabilities();
        Assert.False(capabilities.Hdr.Hdr10);
        Assert.Equal("unknown", capabilities.DisplayHdrEvidence);
        Assert.DoesNotContain(Log.Messages, message => message.Contains("private diagnostic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchUsesAnEnumeratedPqModeAndRestoreIsIdempotent()
    {
        var output = new DisplayHdrOutput();
        Assert.True(await output.TryEnableHdr10Async());
        Assert.Same(_hdr, _hdmi.RequestedMode);
        Assert.Equal(HdmiDisplayHdrOption.Eotf2084, _hdmi.RequestedOption);
        await output.RestoreAsync();
        await output.RestoreAsync();
        Assert.Equal(1, _hdmi.Switches);
        Assert.Equal(1, _hdmi.Restores);
        Assert.Equal(new[] { "display HDR: switched=True", "display HDR: restored=True" }, Log.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FalseOrThrowingSwitchStillRetainsRestoreAuthority(bool throws)
    {
        _hdmi.Switch = () => throws ? throw new InvalidOperationException("private diagnostic") : Task.FromResult(false);
        var output = new DisplayHdrOutput();
        Assert.False(await output.TryEnableHdr10Async());
        await output.RestoreAsync();
        Assert.Equal(1, _hdmi.Restores);
        Assert.Equal(new[] { "display HDR: switched=False", "display HDR: restored=True" }, Log.Messages);
    }

    [Fact]
    public async Task MountRechecksModesRatherThanUsingAStaleStartSnapshot()
    {
        Assert.True(DisplayHdrOutput.DetectCapabilities().Hdr.Hdr10);
        _hdmi.ReadModes = () => [new(), new() { IsSmpte2084Supported = true, StereoEnabled = true }];
        var output = new DisplayHdrOutput();
        Assert.False(await output.TryEnableHdr10Async());
        await output.RestoreAsync();
        Assert.Equal(0, _hdmi.Switches);
        Assert.Equal(0, _hdmi.Restores);
    }

    [Fact]
    public async Task FailedRestoreCanBeRetriedDuringTheNextTeardown()
    {
        var output = new DisplayHdrOutput();
        Assert.True(await output.TryEnableHdr10Async());
        _hdmi.Restore = () => throw new InvalidOperationException("private diagnostic");
        await output.RestoreAsync();
        _hdmi.Restore = () => Task.CompletedTask;
        await output.RestoreAsync();
        await output.RestoreAsync();
        Assert.Equal(2, _hdmi.Restores);
        Assert.Equal(new[] { "display HDR: switched=True", "display HDR: restored=False", "display HDR: restored=True" }, Log.Messages);
    }

    [Fact]
    public async Task ContentMatchUsesOneSdrRequestAndTheExistingRestoreAuthority()
    {
        var movie = new HdmiDisplayMode { RefreshRate = 23.976 };
        _hdmi.ReadModes = () => [new(), movie];
        var output = new DisplayHdrOutput();
        Assert.True(await output.TryMatchContentAsync(Plan(23.976)));
        Assert.Same(movie, _hdmi.RequestedMode);
        Assert.Equal(HdmiDisplayHdrOption.None, _hdmi.RequestedOption);
        Assert.Equal("display match: content=23.976 1920x1080 picked=1920x1080@23.976 switched=True", Assert.Single(Log.Messages));
        await output.RestoreAsync();
        await output.RestoreAsync();
        Assert.Equal(1, _hdmi.Switches);
        Assert.Equal(1, _hdmi.Restores);
    }

    [Fact]
    public async Task HdrAndRefreshMatchingShareOnePqRequest()
    {
        var pq = new HdmiDisplayMode { RefreshRate = 24, IsSmpte2084Supported = true };
        _hdmi.ReadModes = () => [new() { RefreshRate = 23.976 }, _hdr, pq];
        var output = new DisplayHdrOutput();
        Assert.True(await output.TryMatchContentAsync(Plan(23.976, hdr: true)));
        Assert.Same(pq, _hdmi.RequestedMode);
        Assert.Equal(HdmiDisplayHdrOption.Eotf2084, _hdmi.RequestedOption);
        Assert.Equal(1, _hdmi.Switches);
        await output.RestoreAsync();
        Assert.Equal(1, _hdmi.Restores);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameTransportSkipsOnlyWhenRequiredHdrIsAlreadyActive(bool activeHdr)
    {
        _hdmi.Current = _hdr;
        DisplayInformation.ReadColor = () => new() { CurrentAdvancedColorKind = activeHdr
            ? AdvancedColorKind.HighDynamicRange : AdvancedColorKind.StandardDynamicRange };
        var output = new DisplayHdrOutput();
        Assert.True(await output.TryMatchContentAsync(Plan(60, hdr: true)));
        Assert.Equal(activeHdr ? 0 : 1, _hdmi.Switches);
        await output.RestoreAsync();
        Assert.Equal(activeHdr ? 0 : 1, _hdmi.Restores);
    }

    [Fact]
    public async Task MatchingCurrentSdrModeNeedsNeitherSwitchNorRestore()
    {
        var output = new DisplayHdrOutput();
        Assert.True(await output.TryMatchContentAsync(Plan(60)));
        await output.RestoreAsync();
        Assert.Equal(0, _hdmi.Switches);
        Assert.Equal(0, _hdmi.Restores);
        Assert.Equal("display match: content=60 1920x1080 picked=1920x1080@60 switched=False", Assert.Single(Log.Messages));
    }

    [Fact]
    public async Task SuccessfulSignaturesAreCoalescedUntilRestoreAndIncludeHdr()
    {
        var sdr = new HdmiDisplayMode { RefreshRate = 24 };
        var hdr = new HdmiDisplayMode { RefreshRate = 24, IsSmpte2084Supported = true };
        _hdmi.ReadModes = () => [sdr, hdr];
        var output = new DisplayHdrOutput();
        Assert.True(await output.TryMatchContentAsync(Plan(24)));
        Assert.True(await output.TryMatchContentAsync(Plan(24.0004)));
        Assert.Equal(1, _hdmi.Switches);
        Assert.Single(Log.Messages);
        Assert.True(await output.TryMatchContentAsync(Plan(24, hdr: true)));
        Assert.Equal(2, _hdmi.Switches);
        Assert.Equal(HdmiDisplayHdrOption.Eotf2084, _hdmi.RequestedOption);
        await output.RestoreAsync();
        Assert.True(await output.TryMatchContentAsync(Plan(24, hdr: true)));
        Assert.Equal(3, _hdmi.Switches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMatchIsNotCachedAndRetainsAuthorityAcrossAFailedRestore(bool throws)
    {
        _hdmi.ReadModes = () => [new() { RefreshRate = 24 }];
        _hdmi.Switch = () => throws ? throw new InvalidOperationException("private diagnostic") : Task.FromResult(false);
        var output = new DisplayHdrOutput();
        Assert.False(await output.TryMatchContentAsync(Plan(24)));
        Assert.False(await output.TryMatchContentAsync(Plan(24)));
        Assert.Equal(2, _hdmi.Switches);
        _hdmi.Restore = () => throw new InvalidOperationException("private diagnostic");
        await output.RestoreAsync();
        _hdmi.Restore = () => Task.CompletedTask;
        await output.RestoreAsync();
        await output.RestoreAsync();
        Assert.Equal(2, _hdmi.Restores);
        Assert.DoesNotContain(Log.Messages, message => message.Contains("private diagnostic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedNewSignatureInvalidatesThePreviouslyAppliedSignature()
    {
        _hdmi.ReadModes = () => [new() { RefreshRate = 24 }, new() { RefreshRate = 50 }];
        var output = new DisplayHdrOutput();
        Assert.True(await output.TryMatchContentAsync(Plan(24)));
        _hdmi.Switch = () => Task.FromResult(false);
        Assert.False(await output.TryMatchContentAsync(Plan(25)));
        _hdmi.Switch = () => Task.FromResult(true);
        Assert.True(await output.TryMatchContentAsync(Plan(24)));
        Assert.Equal(3, _hdmi.Switches);
    }

    [Fact]
    public async Task MissingPqModeFailsHdrMatchingWithoutAnSdrRequest()
    {
        _hdmi.ReadModes = () => [new() { RefreshRate = 24 }];
        var output = new DisplayHdrOutput();
        Assert.False(await output.TryMatchContentAsync(Plan(24, hdr: true)));
        Assert.Equal(0, _hdmi.Switches);
        Assert.Contains("picked=none switched=False", Assert.Single(Log.Messages));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task UnknownFrameRateSkipsMatchingButKeepsHdrNegotiation(double? fps)
    {
        var output = new DisplayHdrOutput();
        Assert.True(await output.TryMatchContentAsync(Plan(fps)));
        Assert.Equal(0, _hdmi.Switches);
        Assert.Equal("display match: skipped (unknown frame rate)", Assert.Single(Log.Messages));
        Assert.True(await output.TryMatchContentAsync(Plan(fps, hdr: true)));
        Assert.Equal(1, _hdmi.Switches);
        Assert.Equal(HdmiDisplayHdrOption.Eotf2084, _hdmi.RequestedOption);
    }

    [Fact]
    public async Task HlsUsesEffectiveDimensionsAndFrameRateInsteadOfOriginalPicture()
    {
        var hd50 = new HdmiDisplayMode { RefreshRate = 50 };
        _hdmi.ReadModes = () => [new(), hd50,
            new() { ResolutionWidthInRawPixels = 3840, ResolutionHeightInRawPixels = 2160, RefreshRate = 24 }];
        var plan = Plan(23.976);
        plan.Source!.Width = 3840;
        plan.Source.Height = 2160;
        plan.Delivery = "server_transcode_hls";
        plan.EffectiveRecipe = new() { Width = 1920, Height = 1080, FrameRate = 25 };
        Assert.True(await new DisplayHdrOutput().TryMatchContentAsync(plan));
        Assert.Same(hd50, _hdmi.RequestedMode);
    }

    [Fact]
    public async Task MissingDimensionsSkipInsteadOfPickingAnArbitraryResolution()
    {
        var plan = Plan(24);
        plan.Source!.Width = null;
        Assert.True(await new DisplayHdrOutput().TryMatchContentAsync(plan));
        Assert.Equal(0, _hdmi.Switches);
        Assert.Equal("display match: skipped (unknown video size)", Assert.Single(Log.Messages));
    }

    [Theory]
    [InlineData(false, "SDR")]
    [InlineData(true, "HDR")]
    public void DisplayStatsReadCurrentTimingAndActiveHdrRatherThanPqSupport(bool hdr, string expected)
    {
        _hdmi.Current = new() { RefreshRate = 59.94, IsSmpte2084Supported = true };
        DisplayInformation.ReadColor = () => new() { CurrentAdvancedColorKind = hdr
            ? AdvancedColorKind.HighDynamicRange : AdvancedColorKind.StandardDynamicRange };
        Assert.Equal("1920x1080@59.94Hz · " + expected, DisplayHdrOutput.CurrentModeText());
        DisplayInformation.ReadColor = () => throw new InvalidOperationException("private diagnostic");
        Assert.Equal("1920x1080@59.94Hz · HDR/SDR unavailable", DisplayHdrOutput.CurrentModeText());
        HdmiDisplayInformation.ForCurrentView = () => null;
        Assert.Equal("unavailable", DisplayHdrOutput.CurrentModeText());
        Assert.Empty(Log.Messages);
    }

    private static PlaybackPlan Plan(double? fps, bool hdr = false) => new()
    {
        Delivery = "original_http",
        Source = new() { Width = 1920, Height = 1080, FrameRate = fps, DynamicRange = hdr ? "hdr10" : "sdr" },
    };

    [Fact]
    public void PlayerAppliesBeforeOpeningAndRestoresBeforeReplanAndFinalReport()
    {
        var media = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlayerPage.Media.cs.txt"));
        var mount = media[media.IndexOf("private async Task MountPlanAsync", StringComparison.Ordinal)..];
        Assert.True(mount.IndexOf("await _hdrOutput.TryEnableHdr10Async()", StringComparison.Ordinal)
            < mount.IndexOf("Playback.OpenMedia", StringComparison.Ordinal));
        Assert.Contains("hdrOutputFailed: true", mount);
        var player = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlayerPage.cs.txt"));
        var recovery = player[player.IndexOf("private async Task RecoverCoreAsync", StringComparison.Ordinal)..];
        Assert.Contains("if (attempt.Replanned)", recovery);
        Assert.Contains("PlaybackReplan.ForHdrOutputFailure", recovery);
        Assert.Contains("hdrOutputFailed && DisplayHdrPolicy.RequiresHdr10(attempt.Plan)", recovery);
        Assert.True(recovery.IndexOf("await _hdrOutput.RestoreAsync()", StringComparison.Ordinal)
            < recovery.IndexOf("Playback.ReplanAsync", StringComparison.Ordinal));
        var stop = player[player.IndexOf("private async Task StopAttemptAsync", StringComparison.Ordinal)..];
        Assert.True(stop.IndexOf("await _hdrOutput.RestoreAsync()", StringComparison.Ordinal)
            < stop.IndexOf("await StopSessionAsync", StringComparison.Ordinal));
        Assert.Contains("private void OnUnloaded(object sender, RoutedEventArgs e) => _ = ShutdownAsync()", player);
        Assert.DoesNotContain("ex.Message", player);
        Assert.DoesNotContain("ex.Message", media);
    }
}
