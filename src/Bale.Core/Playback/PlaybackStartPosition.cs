namespace Bale.Core.Playback;

/// <summary>
/// Start positions, ported from shared/.../model/playback/PlaybackStartPosition.kt. A non-null override
/// is an explicit command (0 = Start Over); stored progress only counts when it's a real resume point.
/// </summary>
public static class PlaybackStartPosition
{
    /// <summary>MinResumeForRewindSeconds: below this a resume isn't nudged back.</summary>
    public const double MinResumeForRewindSeconds = 30.0;

    /// <summary>DefaultResumeRewindSeconds (the per-profile setting's default; Bale has no setting yet).</summary>
    public const double DefaultResumeRewindSeconds = 7.0;

    public static bool IsExplicitStartOver(double? overridePosition) => overridePosition == 0.0;

    /// <summary>resolvePlaybackStartRequestPosition: the position sent to playback/start.</summary>
    public static double? ResolveRequestPosition(double? overridePosition, double? detailPosition) =>
        overridePosition is { } o && double.IsFinite(o) && o >= 0 ? o
        : detailPosition is { } d && double.IsFinite(d) && d > 0 ? d
        : null;

    /// <summary>resolvePlaybackStartPosition: the position after the server answers.</summary>
    public static double Resolve(double? overridePosition, double sessionPosition, double? detailPosition) =>
        overridePosition is { } o && double.IsFinite(o) && o >= 0 ? o
        : double.IsFinite(sessionPosition) && sessionPosition > 0 ? sessionPosition
        : detailPosition is { } d && double.IsFinite(d) && d > 0 ? d
        : 0;

    /// <summary>applyResumeRewind: a genuine resume starts a few seconds early; overrides never move.</summary>
    public static double ApplyResumeRewind(double resolvedStartPosition, bool isExplicitOverride, double rewindSeconds)
    {
        var start = double.IsFinite(resolvedStartPosition) && resolvedStartPosition > 0 ? resolvedStartPosition : 0;
        if (isExplicitOverride) return start;
        if (!double.IsFinite(rewindSeconds) || rewindSeconds <= 0) return start;
        if (start < MinResumeForRewindSeconds) return start;
        return Math.Max(0, start - rewindSeconds);
    }

    /// <summary>
    /// TvVideoPlaybackStarter.kt's startRequestPosition: Start Over suppresses the rewind, and the one
    /// rewound value drives both the server seek and the player start.
    /// </summary>
    public static double? ForStartRequest(double? overridePosition, double? detailPosition,
        double rewindSeconds = DefaultResumeRewindSeconds)
    {
        var position = ResolveRequestPosition(overridePosition, detailPosition);
        return position is { } p ? ApplyResumeRewind(p, IsExplicitStartOver(overridePosition), rewindSeconds) : null;
    }
}
