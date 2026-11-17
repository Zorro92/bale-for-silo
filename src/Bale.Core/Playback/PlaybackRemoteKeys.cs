namespace Bale.Core.Playback;

/// <summary>The player's intent for one remote or keyboard key (TvPlayerRemoteKeyAction.kt).</summary>
public enum PlaybackKeyAction
{
    /// <summary>Not a player key: focus navigation and other handlers get it.</summary>
    None,
    PlayPause,
    SkipBack,
    SkipForward,
    Stop,
    /// <summary>A player key's repeat or key-up half: swallowed so nothing acts on it twice.</summary>
    ConsumeOnly,
}

/// <summary>
/// TvPlayerRemoteKeyAction.kt for Windows virtual keys. Media keys and Space act on the first press
/// and consume repeats and key-up; Left/Right skip only while no focus-owning surface is up
/// (<paramref name="horizontalSeek"/>); the dedicated skip keys (triggers, J/L, media
/// previous/next) skip in every state.
/// </summary>
public static class PlaybackRemoteKeys
{
    // Windows.System.VirtualKey values; the media keys have no named VirtualKey member.
    public const int Space = 32;
    public const int Left = 37;
    public const int Right = 39;
    public const int J = 74;
    public const int L = 76;
    public const int MediaNextTrack = 176;
    public const int MediaPreviousTrack = 177;
    public const int MediaStop = 178;
    public const int MediaPlayPause = 179;
    public const int GamepadLeftTrigger = 201;
    public const int GamepadRightTrigger = 202;
    public const int GamepadDPadLeft = 205;
    public const int GamepadDPadRight = 206;
    public const int GamepadLeftThumbstickRight = 213;
    public const int GamepadLeftThumbstickLeft = 214;

    /// <param name="key">The virtual key.</param>
    /// <param name="keyDown">True for key-down, false for key-up.</param>
    /// <param name="firstDown">A key-down that is not an auto-repeat.</param>
    /// <param name="horizontalSeek">No focus-owning surface (controls, HUD, Up Next) is up.</param>
    /// <param name="modifier">Ctrl/Alt/Windows held: Space is then a system shortcut.</param>
    public static PlaybackKeyAction Action(int key, bool keyDown, bool firstDown, bool horizontalSeek = true,
        bool modifier = false)
    {
        PlaybackKeyAction FirstPress(PlaybackKeyAction action) =>
            keyDown && firstDown ? action : PlaybackKeyAction.ConsumeOnly;

        return key switch
        {
            Space when modifier => PlaybackKeyAction.None,
            MediaPlayPause or Space => FirstPress(PlaybackKeyAction.PlayPause),
            MediaStop => FirstPress(PlaybackKeyAction.Stop),
            GamepadLeftTrigger or J or MediaPreviousTrack => FirstPress(PlaybackKeyAction.SkipBack),
            GamepadRightTrigger or L or MediaNextTrack => FirstPress(PlaybackKeyAction.SkipForward),
            Left or GamepadDPadLeft or GamepadLeftThumbstickLeft when horizontalSeek => FirstPress(PlaybackKeyAction.SkipBack),
            Right or GamepadDPadRight or GamepadLeftThumbstickRight when horizontalSeek => FirstPress(PlaybackKeyAction.SkipForward),
            _ => PlaybackKeyAction.None,
        };
    }
}

/// <summary>
/// The media remote's Play/Pause can arrive twice on a console: as a key and through the system
/// media controls. A toggle and an explicit Play/Pause from the other route within the window are
/// one press, so the second is dropped (otherwise a pause would immediately resume).
/// </summary>
public sealed class PlaybackTransportDedup(long windowMs = 500)
{
    private long _lastKey = long.MinValue / 2;
    private long _lastSystem = long.MinValue / 2;

    /// <summary>A Play/Pause key press at <paramref name="nowMs"/>; false when the system route just acted.</summary>
    public bool AcceptKey(long nowMs)
    {
        if (nowMs - _lastSystem < windowMs) return false;
        _lastKey = nowMs;
        return true;
    }

    /// <summary>A system media control Play or Pause; false when a key just acted.</summary>
    public bool AcceptSystem(long nowMs)
    {
        if (nowMs - _lastKey < windowMs) return false;
        _lastSystem = nowMs;
        return true;
    }
}
