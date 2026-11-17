namespace Bale.Core.Playback;

/// <summary>The player's intent for one controller, remote or keyboard key.</summary>
public enum PlaybackKeyAction
{
    /// <summary>Not a player key: focus navigation and other handlers get it.</summary>
    None,
    /// <summary>Media Play/Pause or Space: toggle.</summary>
    PlayPause,
    /// <summary>A / Enter on clean playback: pause (and show the controls) or, when paused, resume.</summary>
    Select,
    /// <summary>The D-pad on clean playback: show the controls; the press does nothing else.</summary>
    RevealControls,
    SkipBack,
    SkipForward,
    PreviousChapter,
    NextChapter,
    Stop,
    /// <summary>A player key's repeat or key-up half: swallowed so nothing acts on it twice.</summary>
    ConsumeOnly,
}

/// <summary>
/// The player's key map. Media keys follow TvPlayerRemoteKeyAction.kt (first press acts, repeats and
/// key-up are swallowed). The controller map is Bale's own (AGENTS.md, deviations): the D-pad
/// reveals the controls, A pauses/resumes, triggers skip 10 s back / 30 s forward, bumpers move
/// between chapters. While the controls (or another focus-owning surface) are up, the D-pad and A
/// belong to focus navigation; the dedicated transport keys act in every state.
/// </summary>
public static class PlaybackRemoteKeys
{
    // Windows.System.VirtualKey values; the media keys have no named VirtualKey member.
    public const int Enter = 13;
    public const int Space = 32;
    public const int PageUp = 33;
    public const int PageDown = 34;
    public const int Left = 37;
    public const int Up = 38;
    public const int Right = 39;
    public const int Down = 40;
    public const int J = 74;
    public const int L = 76;
    public const int MediaNextTrack = 176;
    public const int MediaPreviousTrack = 177;
    public const int MediaStop = 178;
    public const int MediaPlayPause = 179;
    public const int GamepadA = 195;
    public const int GamepadRightShoulder = 199;
    public const int GamepadLeftShoulder = 200;
    public const int GamepadLeftTrigger = 201;
    public const int GamepadRightTrigger = 202;
    public const int GamepadDPadUp = 203;
    public const int GamepadDPadDown = 204;
    public const int GamepadDPadLeft = 205;
    public const int GamepadDPadRight = 206;
    public const int GamepadLeftThumbstickUp = 211;
    public const int GamepadLeftThumbstickDown = 212;
    public const int GamepadLeftThumbstickRight = 213;
    public const int GamepadLeftThumbstickLeft = 214;

    /// <param name="key">The virtual key.</param>
    /// <param name="keyDown">True for key-down, false for key-up.</param>
    /// <param name="firstDown">A key-down that is not an auto-repeat.</param>
    /// <param name="cleanPlayback">No controls or other focus-owning surface on screen.</param>
    /// <param name="modifier">Ctrl/Alt/Windows held: Space is then a system shortcut.</param>
    public static PlaybackKeyAction Action(int key, bool keyDown, bool firstDown, bool cleanPlayback = true,
        bool modifier = false)
    {
        PlaybackKeyAction FirstPress(PlaybackKeyAction action) =>
            keyDown && firstDown ? action : PlaybackKeyAction.ConsumeOnly;

        return key switch
        {
            Space when modifier => PlaybackKeyAction.None,
            // With the controls up, Space (which the console also sends for A) presses the focused button.
            Space when !cleanPlayback => PlaybackKeyAction.None,
            MediaPlayPause or Space => FirstPress(PlaybackKeyAction.PlayPause),
            MediaStop => FirstPress(PlaybackKeyAction.Stop),
            GamepadLeftTrigger or J or MediaPreviousTrack => FirstPress(PlaybackKeyAction.SkipBack),
            GamepadRightTrigger or L or MediaNextTrack => FirstPress(PlaybackKeyAction.SkipForward),
            GamepadLeftShoulder or PageUp => FirstPress(PlaybackKeyAction.PreviousChapter),
            GamepadRightShoulder or PageDown => FirstPress(PlaybackKeyAction.NextChapter),
            _ when !cleanPlayback => PlaybackKeyAction.None,
            GamepadA or Enter => FirstPress(PlaybackKeyAction.Select),
            Left or Up or Right or Down or GamepadDPadUp or GamepadDPadDown or GamepadDPadLeft or GamepadDPadRight
                or GamepadLeftThumbstickUp or GamepadLeftThumbstickDown or GamepadLeftThumbstickLeft
                or GamepadLeftThumbstickRight => FirstPress(PlaybackKeyAction.RevealControls),
            _ => PlaybackKeyAction.None,
        };
    }
}

/// <summary>Bumper chapter jumps.</summary>
public static class PlaybackChapters
{
    /// <summary>Back within this many seconds of a chapter's start goes to the chapter before it.</summary>
    public const double RestartWindowSeconds = 3;

    /// <summary>
    /// Next: the first chapter that starts after the position (none: no jump). Previous: the start of
    /// the current chapter, or of the one before when the position is within
    /// <see cref="RestartWindowSeconds"/> of the current start (the start of the title before the
    /// first chapter). Null when there is nowhere to go.
    /// </summary>
    public static double? Target(IEnumerable<double?> starts, double position, bool forward)
    {
        var ordered = starts.OfType<double>().Where(start => double.IsFinite(start) && start >= 0)
            .Distinct().Order().ToList();
        if (ordered.Count == 0 || !double.IsFinite(position)) return null;
        if (forward)
        {
            foreach (var start in ordered)
                if (start > position + 0.5) return start;
            return null;
        }

        var current = ordered.LastOrDefault(start => start <= position + 0.5, double.NaN);
        if (!double.IsNaN(current) && position - current > RestartWindowSeconds) return current;
        var previous = ordered.LastOrDefault(start => start < (double.IsNaN(current) ? position : current) - 0.001, double.NaN);
        if (!double.IsNaN(previous)) return previous;
        return position > 0.5 ? 0 : null;
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
