using Bale.Xbox.Services;
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
