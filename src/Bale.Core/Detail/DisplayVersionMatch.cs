using Bale.Core.Playback;

namespace Bale.Core.Detail;

/// <summary>
/// Display resolution matching for Auto version choice, a deliberate deviation from the official
/// apps (PlaybackVersionSelector.kt / DetailVersionSelection.swift), which take the highest
/// resolution whatever the display. On by default; the settings toggle (0.2.x) clears it to get
/// the official behaviour back. See docs/player-plan.md "Display resolution matching".
/// </summary>
/// <param name="DisplayMaxHeight">Height of the connected display's largest HDMI mode.</param>
/// <param name="VideoDecode">The decode claims the player sends, so a pick the server would
/// transcode loses to one it can play as it is.</param>
public sealed record DisplayVersionMatch(int DisplayMaxHeight, IReadOnlyList<VideoDecodeCapability> VideoDecode)
{
    private static DisplayVersionMatch? _current;

    /// <summary>
    /// Set by the app from the connected display. Null gives the official highest-first Auto:
    /// matching turned off, or the display modes could not be read.
    /// </summary>
    public static DisplayVersionMatch? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    /// <summary>
    /// The match for a display's supported HDMI modes: its largest mode height. Null (official
    /// Auto) when there are no modes, as on a failed read.
    /// </summary>
    public static DisplayVersionMatch? FromModes(IReadOnlyList<DisplayModeFacts> modes, IReadOnlyList<VideoDecodeCapability> videoDecode)
    {
        var height = modes.Count == 0 ? 0 : modes.Max(mode => mode.Height);
        return height is > 0 and <= int.MaxValue ? new((int)height, videoDecode) : null;
    }
}
