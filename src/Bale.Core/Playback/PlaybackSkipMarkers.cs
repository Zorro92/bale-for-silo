using Bale.Core.Settings;

namespace Bale.Core.Playback;

/// <summary>Which skip pill shows in the player's bottom-end slot (TvPlayerScreen.kt TvPlayerOverlays).</summary>
public enum SkipPillKind
{
    /// <summary>The intro prompt's ask offer (TvIntroAutoSkipBanner.kt, introSkip_pill_skip).</summary>
    SkipIntro,
    /// <summary>The intro prompt's always undo, under its "Intro skipped" caption (introSkip_pill_undo).</summary>
    WatchIntro,
    /// <summary>The manual recap pill (TvMarkerSkipPill, marker_skipRecap).</summary>
    SkipRecap,
    /// <summary>The manual credits pill (TvMarkerSkipPill, marker_skipCredits).</summary>
    SkipCredits,
}

/// <summary>The skip pill on screen.</summary>
/// <param name="Label">The action's words.</param>
/// <param name="Caption">The small line above the pill (only the undo's "Intro skipped"), or null.</param>
/// <param name="Progress">The countdown fill, 0 to 1; 0 for the untimed marker pill.</param>
/// <param name="Timed">The intro prompt's pills run a timer; the marker pill shows as long as its range plays.</param>
/// <param name="TakesFocus">
/// The intro pill takes focus as it appears (TvIntroAutoSkipBanner.kt autoFocus); the marker pill never does
/// (TvMarkerSkipPill autoFocus = false).
/// </param>
/// <param name="Run">Changes with each fresh intro offer, so the page can focus a new pill once.</param>
public sealed record SkipPill(SkipPillKind Kind, string Label, string? Caption, double Progress, bool Timed, bool TakesFocus, int Run);

/// <summary>A seek the skip markers ask for: the pill's action or an automatic skip.</summary>
public readonly record struct SkipMarkerSeek(double Target, SkipPillKind Kind, bool Automatic);

/// <summary>What the player knows at one tick (<see cref="PlaybackSkipMarkers.Update"/>).</summary>
public readonly record struct SkipMarkerFacts
{
    /// <summary>Source seconds: the playhead, or the target of a seek still in flight (where playback is heading).</summary>
    public double Position { get; init; }
    public MarkerTimeline? Markers { get; init; }
    /// <summary>The playing file (the attempt keys its own decisions; a new attempt resets them); null offers nothing.</summary>
    public string? FileKey { get; init; }
    public IntroSkipMode Mode { get; init; }
    /// <summary><c>playback.auto_skip_credits</c>.</summary>
    public bool AutoSkipCredits { get; init; }
    public PromptActivity Activity { get; init; }
    /// <summary>
    /// The mount presented its first frame and has not failed. Before that nothing is offered or skipped (silo-apple
    /// markerPlaybackHasStarted): the start's progress reporting must not race a seek.
    /// </summary>
    public bool Presented { get; init; }
    /// <summary>A mount is loading (a reload, not a rebuffer): the marker pill hides (TvPlayerScreen.kt playbackUnavailable).</summary>
    public bool Loading { get; init; }
    public long NowMs { get; init; }
}

/// <summary>
/// The player's skip markers (silo-android TvPlayerViewModel.kt / TvPlayerScreen.kt, with tvOS for the credits setting):
/// <list type="bullet">
/// <item>The intro: <see cref="IntroSkipPrompt"/>, driven by <c>playback.intro_skip_mode</c>.</item>
/// <item>Recap and credits: a manual pill while the playhead is inside an occurrence (MarkerSkipTarget.kt
/// markerSkipTarget, recap first when ranges overlap), only while no intro pill shows (tvManualMarkerSkipTarget) and
/// never during a reload. It has no timer and never takes focus.</item>
/// <item><c>playback.auto_skip_credits</c>: Android TV stores the switch but its player never reads it; this follows
/// silo-apple PlayerViewModel.swift CreditsAutoSkipPolicy: entering a credits occurrence skips it once, and a manual
/// Skip Credits marks its occurrence too, so neither skips it again.</item>
/// </list>
/// Credits never move Up Next (Android <c>e580e1811</c>: "credits drive only Skip Credits"); a skip whose target is the
/// end of the file ends the stream, which raises Up Next as any end does.
/// </summary>
public sealed class PlaybackSkipMarkers(long promptMs = IntroSkipPrompt.PromptMs, long pauseGraceMs = IntroSkipPrompt.PauseGraceMs)
{
    public const string SkipIntroLabel = "Skip Intro";
    public const string WatchIntroLabel = "Watch Intro";
    public const string IntroSkippedCaption = "Intro skipped";
    public const string SkipCreditsLabel = "Skip Credits";
    public const string SkipRecapLabel = "Skip Recap";

    private readonly IntroSkipPrompt _intro = new(promptMs, pauseGraceMs);
    private readonly HashSet<string> _creditsSkipped = new(StringComparer.Ordinal);

    /// <summary>The pill to show, or null. The page still hides it while a menu, Up Next or the error screen covers it.</summary>
    public SkipPill? Pill { get; private set; }

    /// <summary>Re-evaluates the pills; returns an automatic skip the caller must seek to (always's intro, or credits).</summary>
    public SkipMarkerSeek? Update(SkipMarkerFacts facts)
    {
        var markers = facts.Markers ?? MarkerTimeline.Empty;
        // A mount still coming up is a stall for the timer: a running pill freezes after the grace window, nothing starts.
        var activity = facts.Presented ? facts.Activity : PromptActivity.Stalled;
        var skip = _intro.Update(facts.Position, markers.OccurrenceAt(MarkerKind.Intro, facts.Position), facts.FileKey,
            facts.Mode, activity, facts.NowMs);
        if (_intro.Pill is { } intro)
        {
            Pill = IntroPill(intro);
            return skip is { } introEnd ? new SkipMarkerSeek(introEnd, SkipPillKind.SkipIntro, true) : null;
        }
        var eligible = facts.Presented && !facts.Loading && facts.FileKey is not null;
        if (eligible && facts.AutoSkipCredits && facts.Activity == PromptActivity.Playing
            && markers.Active(MarkerKind.Credits, facts.Position) is { } credits && _creditsSkipped.Add(CreditsKey(facts.FileKey!, credits)))
        {
            Pill = null;
            return new SkipMarkerSeek(credits.End, SkipPillKind.SkipCredits, true);
        }
        Pill = eligible && Target(markers, facts.Position) is { } target ? MarkerPill(target.Kind) : null;
        return null;
    }

    /// <summary>
    /// The pill's action: the intro prompt's (its end, or its start for the undo), else the marker pill's, re-read at the
    /// press (TvPlayerScreen.kt handleManualMarkerSelect). Null when no pill shows.
    /// </summary>
    public SkipMarkerSeek? Select(SkipMarkerFacts facts)
    {
        var kind = Pill?.Kind;
        if (_intro.Select() is { } intro)
        {
            Pill = null;
            return new SkipMarkerSeek(intro, kind ?? SkipPillKind.SkipIntro, false);
        }
        if (kind is not (SkipPillKind.SkipRecap or SkipPillKind.SkipCredits) || facts.FileKey is not { } file
            || Target(facts.Markers ?? MarkerTimeline.Empty, facts.Position) is not { } target) return null;
        if (target.Kind == SkipPillKind.SkipCredits) _creditsSkipped.Add(CreditsKey(file, target.Range));
        Pill = null;
        return new SkipMarkerSeek(target.Range.End, target.Kind, false);
    }

    /// <summary>
    /// Back while the intro pill shows: hide it and resolve the intro (consumed); false otherwise, so the press goes on.
    /// The marker pill takes no Back (TvPlayerScreen.kt's BackHandler only knows the intro and buffering pills).
    /// </summary>
    public bool Back()
    {
        if (!_intro.Dismiss()) return false;
        Pill = null;
        return true;
    }

    /// <summary>A new attempt: every intro and credits decision starts over.</summary>
    public void Reset()
    {
        _intro.Reset();
        _creditsSkipped.Clear();
        Pill = null;
    }

    /// <summary>MarkerSkipTarget.kt markerSkipTarget: the recap or credits occurrence playing, recap first.</summary>
    private static (SkipPillKind Kind, MarkerRange Range)? Target(MarkerTimeline markers, double position) =>
        markers.Active(MarkerKind.Recap, position) is { } recap ? (SkipPillKind.SkipRecap, recap)
        : markers.Active(MarkerKind.Credits, position) is { } credits ? (SkipPillKind.SkipCredits, credits)
        : null;

    /// <summary>Names the occurrence, not the session (tvOS currentCreditsSkipKey), within this attempt's file.</summary>
    private static string CreditsKey(string file, MarkerRange range) => $"{file}:credits@{range.Start:R}-{range.End:R}";

    private static SkipPill IntroPill(IntroPromptPill pill) => pill.Kind == IntroPromptKind.Undo
        ? new SkipPill(SkipPillKind.WatchIntro, WatchIntroLabel, IntroSkippedCaption, pill.Progress, true, true, pill.Run)
        : new SkipPill(SkipPillKind.SkipIntro, SkipIntroLabel, null, pill.Progress, true, true, pill.Run);

    private static SkipPill MarkerPill(SkipPillKind kind) =>
        new(kind, kind == SkipPillKind.SkipRecap ? SkipRecapLabel : SkipCreditsLabel, null, 0, false, false, 0);
}
