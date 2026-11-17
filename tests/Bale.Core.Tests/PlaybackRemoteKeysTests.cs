using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>Bale's player key map (media keys as TvPlayerRemoteKeyActionTest.kt, controller per AGENTS.md).</summary>
public class PlaybackRemoteKeysTests
{
    [Theory]
    [InlineData(PlaybackRemoteKeys.MediaPlayPause, PlaybackKeyAction.PlayPause)]
    [InlineData(PlaybackRemoteKeys.Space, PlaybackKeyAction.PlayPause)]
    [InlineData(PlaybackRemoteKeys.MediaStop, PlaybackKeyAction.Stop)]
    [InlineData(PlaybackRemoteKeys.GamepadLeftTrigger, PlaybackKeyAction.SkipBack)]
    [InlineData(PlaybackRemoteKeys.J, PlaybackKeyAction.SkipBack)]
    [InlineData(PlaybackRemoteKeys.MediaPreviousTrack, PlaybackKeyAction.SkipBack)]
    [InlineData(PlaybackRemoteKeys.GamepadRightTrigger, PlaybackKeyAction.SkipForward)]
    [InlineData(PlaybackRemoteKeys.L, PlaybackKeyAction.SkipForward)]
    [InlineData(PlaybackRemoteKeys.MediaNextTrack, PlaybackKeyAction.SkipForward)]
    [InlineData(PlaybackRemoteKeys.GamepadLeftShoulder, PlaybackKeyAction.PreviousChapter)]
    [InlineData(PlaybackRemoteKeys.PageUp, PlaybackKeyAction.PreviousChapter)]
    [InlineData(PlaybackRemoteKeys.GamepadRightShoulder, PlaybackKeyAction.NextChapter)]
    [InlineData(PlaybackRemoteKeys.PageDown, PlaybackKeyAction.NextChapter)]
    [InlineData(PlaybackRemoteKeys.GamepadA, PlaybackKeyAction.Select)]
    [InlineData(PlaybackRemoteKeys.Enter, PlaybackKeyAction.Select)]
    [InlineData(PlaybackRemoteKeys.GamepadDPadLeft, PlaybackKeyAction.RevealControls)]
    [InlineData(PlaybackRemoteKeys.GamepadDPadDown, PlaybackKeyAction.RevealControls)]
    [InlineData(PlaybackRemoteKeys.Right, PlaybackKeyAction.RevealControls)]
    [InlineData(PlaybackRemoteKeys.GamepadLeftThumbstickUp, PlaybackKeyAction.RevealControls)]
    public void TheFirstPressActsAndRepeatsAndKeyUpAreSwallowed(int key, PlaybackKeyAction action)
    {
        Assert.Equal(action, PlaybackRemoteKeys.Action(key, keyDown: true, firstDown: true));
        Assert.Equal(PlaybackKeyAction.ConsumeOnly, PlaybackRemoteKeys.Action(key, keyDown: true, firstDown: false));
        Assert.Equal(PlaybackKeyAction.ConsumeOnly, PlaybackRemoteKeys.Action(key, keyDown: false, firstDown: false));
    }

    [Fact]
    public void WithControlsUpTheDPadAndABelongToFocusButTransportKeysStillAct()
    {
        Assert.Equal(PlaybackKeyAction.None, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.GamepadDPadLeft, true, true, cleanPlayback: false));
        Assert.Equal(PlaybackKeyAction.None, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.GamepadA, true, true, cleanPlayback: false));
        Assert.Equal(PlaybackKeyAction.None, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.Right, false, false, cleanPlayback: false));
        Assert.Equal(PlaybackKeyAction.SkipForward, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.GamepadRightTrigger, true, true, cleanPlayback: false));
        Assert.Equal(PlaybackKeyAction.NextChapter, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.GamepadRightShoulder, true, true, cleanPlayback: false));
        Assert.Equal(PlaybackKeyAction.PlayPause, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.MediaPlayPause, true, true, cleanPlayback: false));
    }

    [Fact]
    public void SpaceWithAModifierIsASystemShortcutAndOtherKeysAreNotOurs()
    {
        Assert.Equal(PlaybackKeyAction.None, PlaybackRemoteKeys.Action(PlaybackRemoteKeys.Space, true, true, modifier: true));
        Assert.Equal(PlaybackKeyAction.None, PlaybackRemoteKeys.Action(196 /* GamepadB */, true, true));
        Assert.Equal(PlaybackKeyAction.None, PlaybackRemoteKeys.Action(208 /* GamepadView */, true, true));
    }

    [Theory]
    [InlineData(100.0, true, 300.0)]
    [InlineData(299.8, true, 600.0)]
    [InlineData(900.0, true, null)]
    [InlineData(310.0, false, 300.0)]
    [InlineData(302.0, false, 0.0)]
    [InlineData(601.0, false, 300.0)]
    [InlineData(2.0, false, 0.0)]
    [InlineData(0.2, false, null)]
    public void ChapterJumpsGoToStartsAndBackTwiceWithinTheRestartWindow(double position, bool forward, double? expected) =>
        Assert.Equal(expected, PlaybackChapters.Target([0, 300, 600, null, double.NaN, 300], position, forward));

    [Fact]
    public void NoChaptersMeansNoJumpAndBackBeforeTheFirstChapterGoesToTheStart()
    {
        Assert.Null(PlaybackChapters.Target([], 50, true));
        Assert.Null(PlaybackChapters.Target([null], 50, false));
        Assert.Equal(0, PlaybackChapters.Target([120], 50, false));
        Assert.Equal(120, PlaybackChapters.Target([120], 50, true));
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
