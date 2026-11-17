using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>TvPlayerRemoteKeyActionTest.kt for Windows virtual keys.</summary>
public class PlaybackRemoteKeysTests
{
    [Theory]
    [InlineData(PlaybackRemoteKeys.MediaPlayPause, PlaybackKeyAction.PlayPause)]
    [InlineData(PlaybackRemoteKeys.Space, PlaybackKeyAction.PlayPause)]
    [InlineData(PlaybackRemoteKeys.MediaStop, PlaybackKeyAction.Stop)]
    [InlineData(PlaybackRemoteKeys.GamepadLeftTrigger, PlaybackKeyAction.SkipBack)]
    [InlineData(PlaybackRemoteKeys.J, PlaybackKeyAction.SkipBack)]
    [InlineData(PlaybackRemoteKeys.MediaPreviousTrack, PlaybackKeyAction.SkipBack)]
    [InlineData(PlaybackRemoteKeys.GamepadDPadLeft, PlaybackKeyAction.SkipBack)]
    [InlineData(PlaybackRemoteKeys.Left, PlaybackKeyAction.SkipBack)]
    [InlineData(PlaybackRemoteKeys.GamepadRightTrigger, PlaybackKeyAction.SkipForward)]
    [InlineData(PlaybackRemoteKeys.L, PlaybackKeyAction.SkipForward)]
    [InlineData(PlaybackRemoteKeys.MediaNextTrack, PlaybackKeyAction.SkipForward)]
    [InlineData(PlaybackRemoteKeys.GamepadLeftThumbstickRight, PlaybackKeyAction.SkipForward)]
    public void TheFirstPressActsAndRepeatsAndKeyUpAreSwallowed(int key, PlaybackKeyAction action)
    {
        Assert.Equal(action, PlaybackRemoteKeys.Action(key, keyDown: true, firstDown: true));
        Assert.Equal(PlaybackKeyAction.ConsumeOnly, PlaybackRemoteKeys.Action(key, keyDown: true, firstDown: false));
        Assert.Equal(PlaybackKeyAction.ConsumeOnly, PlaybackRemoteKeys.Action(key, keyDown: false, firstDown: false));
    }

    [Fact]
    public void LeftRightBelongToFocusWhileASurfaceIsUpButDedicatedSkipKeysDoNot()
    {
        Assert.Equal(PlaybackKeyAction.None, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.GamepadDPadLeft, true, true, horizontalSeek: false));
        Assert.Equal(PlaybackKeyAction.None, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.Right, false, false, horizontalSeek: false));
        Assert.Equal(PlaybackKeyAction.SkipForward, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.GamepadRightTrigger, true, true, horizontalSeek: false));
        Assert.Equal(PlaybackKeyAction.PlayPause, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.MediaPlayPause, true, true, horizontalSeek: false));
    }

    [Fact]
    public void SpaceWithAModifierIsASystemShortcutAndOtherKeysAreNotOurs()
    {
        Assert.Equal(PlaybackKeyAction.None, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.Space, true, true, modifier: true));
        Assert.Equal(PlaybackKeyAction.None, PlaybackRemoteKeys.Action(195 /* GamepadA */, true, true));
        Assert.Equal(PlaybackKeyAction.None, PlaybackRemoteKeys.Action(204 /* GamepadDPadDown */, true, true));
    }

    [Fact]
    public void OnePressArrivingByBothRoutesActsOnce()
    {
        var dedup = new PlaybackTransportDedup(500);
        Assert.True(dedup.AcceptKey(1000));
        Assert.False(dedup.AcceptSystem(1200));
        Assert.True(dedup.AcceptSystem(1600));
        Assert.False(dedup.AcceptKey(1900));
        Assert.True(dedup.AcceptKey(2200));
        Assert.True(dedup.AcceptKey(2300));
    }
}
