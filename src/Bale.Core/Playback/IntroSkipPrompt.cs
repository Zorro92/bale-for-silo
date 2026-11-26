using Bale.Core.Settings;

namespace Bale.Core.Playback;

/// <summary>How playback moves, as far as the intro pill's timer is concerned.</summary>
public enum PromptActivity
{
    /// <summary>The first frame is up and the clock runs.</summary>
    Playing,
    /// <summary>
    /// Playback wants to run but cannot: loading, a seek, a rebuffer. Freezes a running timer only after
    /// <see cref="IntroSkipPrompt.PauseGraceMs"/> (TvPlayerViewModel.kt settlingFalseEdges); never starts an offer.
    /// </summary>
    Stalled,
    /// <summary>The viewer paused (or the stream ended): freezes the timer at once.</summary>
    Paused,
}

/// <summary>Which intro pill shows.</summary>
public enum IntroPromptKind
{
    /// <summary><c>ask</c>: "Skip Intro". Select seeks to the intro's end.</summary>
    Skip,
    /// <summary><c>always</c>: the intro was already skipped. "Watch Intro" seeks back to its start.</summary>
    Undo,
}

/// <summary>The intro pill on screen.</summary>
/// <param name="Run">Bumps with each fresh offer, so the page can tell a new pill (which takes focus) from a tick.</param>
/// <param name="RemainingMs">Time left on the timer as of the update.</param>
/// <param name="Running">False while a pause (or a long stall) freezes it.</param>
public sealed record IntroPromptPill(IntroPromptKind Kind, int Run, long RemainingMs, bool Running, long TotalMs)
{
    /// <summary>The fill: 0 when the timer starts, 1 exactly as it runs out (NoticeCountdownFill.kt).</summary>
    public double Progress => TotalMs <= 0 ? 1 : Math.Clamp(1 - RemainingMs / (double)TotalMs, 0, 1);
}

/// <summary>
/// The intro-skip pill's state machine: silo-android shared/.../domain/player/IntroAutoSkipController.kt (the TV app's),
/// which implements the <c>never</c> / <c>ask</c> / <c>always</c> tables of silo-server
/// docs/design/2026-08-16-intro-skip-mode.md ("Prompt behaviour"). Polled: the player calls <see cref="Update"/> on
/// every tick with the playhead, the intro occurrence for it (<see cref="MarkerTimeline.OccurrenceAt"/>), the mode and
/// the playback activity, and acts on the pill through <see cref="Select"/> and <see cref="Dismiss"/>.
/// <para>
/// The only seek it asks for itself is <c>always</c>'s immediate skip, which <see cref="Update"/> returns. Everything the
/// viewer triggers is returned by <see cref="Select"/>. The timer is wall-clock (the caller's <c>nowMs</c>), and the pill
/// exposes the time left, so the fill and the action cannot drift.
/// </para>
/// </summary>
public sealed class IntroSkipPrompt(long promptMs = IntroSkipPrompt.PromptMs, long pauseGraceMs = IntroSkipPrompt.PauseGraceMs)
{
    /// <summary>The spec's <c>INTRO_PROMPT_SECONDS</c> (IntroAutoSkipController.DEFAULT_COUNTDOWN_SECONDS).</summary>
    public const long PromptMs = 5_000;

    /// <summary>The spec's <c>PLAYBACK_PAUSE_GRACE_MS</c> (TvPlayerViewModel.kt): a shorter stall is a rebuffer.</summary>
    public const long PauseGraceMs = 1_500;

    private readonly HashSet<string> _resolved = new(StringComparer.Ordinal);
    // The intro whose ask offer timed out while the playhead is still inside it: not resolved (scrubbing back in
    // re-offers), but not re-offered on the spot either.
    private string? _expiredKey;
    private string? _activeKey;
    private MarkerRange _activeRange;
    private IntroPromptKind _activeKind;
    private IntroSkipMode? _lastMode;
    private long _remainingMs;
    private long? _runningSince;
    private long? _stallSince;
    private int _run;

    /// <summary>The pill to show, or null.</summary>
    public IntroPromptPill? Pill { get; private set; }

    /// <summary>
    /// Re-evaluates the pill. Returns a position when <c>always</c> has just skipped an intro; the caller must seek there.
    /// </summary>
    /// <param name="position">The playhead in source seconds (a seek in flight: its target).</param>
    /// <param name="intro">The intro occurrence for the playhead (<see cref="MarkerTimeline.OccurrenceAt"/>), or null.</param>
    /// <param name="keyPrefix">Names the playback the intros belong to (attempt and file); null disables the pill.</param>
    public double? Update(double position, MarkerRange? intro, string? keyPrefix, IntroSkipMode mode, PromptActivity activity, long nowMs)
    {
        var (active, until) = Settle(activity, nowMs);
        // Each intro is keyed by its range, not only by the caller's prefix: an episode may have several.
        var key = keyPrefix is not null && intro is { } range ? $"{keyPrefix}@{range.Start:R}-{range.End:R}" : null;

        // A mode change re-evaluates from scratch: ask -> never takes the offer down, never -> always skips the intro the
        // viewer is sitting in.
        if (mode != _lastMode)
        {
            _lastMode = mode;
            _expiredKey = null;
            ClearPrompt();
        }

        // The undo pill is pinned to the intro it skipped, not to the playhead: the skip itself moved playback out of
        // the range. Only its timer, Select, Back, a different intro, a reset or a mode change take it down.
        if (_activeKey is not null && _activeKind == IntroPromptKind.Undo && key == _activeKey)
        {
            Gate(active, until, nowMs);
            return null;
        }

        if (key is null || intro is not { } inside || !double.IsFinite(position) || !inside.Contains(position))
        {
            // Leaving the range clears the timed-out marker, so seeking back in re-offers with a full timer. It does not
            // clear the resolved intros.
            _expiredKey = null;
            ClearPrompt();
            return null;
        }

        if (_activeKey is not null && _activeKey != key)
        {
            _expiredKey = null;
            ClearPrompt();
        }

        if (mode == IntroSkipMode.Never || _resolved.Contains(key) || key == _expiredKey)
        {
            ClearPrompt();
            return null;
        }

        if (_activeKey is null)
        {
            // Held until playback actually runs, so the pill and its fill start together (the spec's Timing; a seek or a
            // reload is no start, so an always skip never lands on a mount that is still coming up).
            if (activity != PromptActivity.Playing) return null;
            _activeKey = key;
            _activeRange = inside;
            _activeKind = mode == IntroSkipMode.Always ? IntroPromptKind.Undo : IntroPromptKind.Skip;
            _remainingMs = promptMs;
            _runningSince = nowMs;
            _run++;
            Publish();
            return _activeKind == IntroPromptKind.Undo ? inside.End : null;
        }

        // The offer is already up for this intro; only the pause gate and the timer can move.
        Gate(active, until, nowMs);
        return null;
    }

    /// <summary>
    /// The pill's action (Select while it is focused, or the player's root Select while the controls are hidden).
    /// Resolves the intro, hides the pill, and returns where the caller must seek: the intro's end for the ask offer, its
    /// start for the always undo. Null when no pill is up, so a stray press does nothing.
    /// </summary>
    public double? Select()
    {
        if (_activeKey is not { } key) return null;
        var target = _activeKind == IntroPromptKind.Skip ? _activeRange.End : _activeRange.Start;
        _resolved.Add(key);
        _expiredKey = null;
        ClearPrompt();
        return target;
    }

    /// <summary>
    /// Back while the pill shows: hide it and resolve the intro without moving playback. True when a pill was
    /// dismissed, so the caller consumes the press only then and a second Back behaves normally.
    /// </summary>
    public bool Dismiss()
    {
        if (_activeKey is not { } key) return false;
        _resolved.Add(key);
        _expiredKey = null;
        ClearPrompt();
        return true;
    }

    /// <summary>Forgets every decision, for when playback moves to other content (a new attempt).</summary>
    public void Reset()
    {
        _resolved.Clear();
        _expiredKey = null;
        _lastMode = null;
        _stallSince = null;
        ClearPrompt();
    }

    /// <summary>
    /// Whether the timer runs, and until when it ran: a pause stops it at once, a stall at the end of the grace window
    /// (TvPlayerViewModel.kt: <c>isPlaying &amp;&amp; !isLoading</c> through settlingFalseEdges, with <c>isPaused</c> as
    /// the deliberate edge). A pill only exists once playback ran, so a stall always follows a start here.
    /// </summary>
    private (bool Active, long Until) Settle(PromptActivity activity, long nowMs)
    {
        switch (activity)
        {
            case PromptActivity.Playing:
                _stallSince = null;
                return (true, nowMs);
            case PromptActivity.Paused:
                _stallSince = null;
                return (false, nowMs);
            default:
                _stallSince ??= nowMs;
                var frozenAt = _stallSince.Value + pauseGraceMs;
                return nowMs < frozenAt ? (true, nowMs) : (false, frozenAt);
        }
    }

    /// <summary>
    /// Runs the timer down to <paramref name="until"/>, then keeps it running while active and frozen otherwise; the
    /// timer running out expires the pill.
    /// </summary>
    private void Gate(bool active, long until, long nowMs)
    {
        if (_activeKey is null) return;
        if (_runningSince is { } since)
        {
            _remainingMs -= Math.Max(0, until - since);
            _runningSince = active ? nowMs : null;
        }
        else if (active) _runningSince = nowMs;
        if (_remainingMs <= 0)
        {
            Expire();
            return;
        }
        Publish();
    }

    /// <summary>
    /// The timer ran out. The two pills differ here and only here: the ask offer withdraws without deciding anything,
    /// while the always undo resolves the intro (the viewer was told it was skipped and let it go).
    /// </summary>
    private void Expire()
    {
        if (_activeKey is { } key)
        {
            if (_activeKind == IntroPromptKind.Undo) _resolved.Add(key);
            else _expiredKey = key;
        }
        ClearPrompt();
    }

    private void Publish() =>
        Pill = new IntroPromptPill(_activeKind, _run, Math.Max(0, _remainingMs), _runningSince is not null, promptMs);

    /// <summary>Takes the pill down and drops its anchor, deciding nothing.</summary>
    private void ClearPrompt()
    {
        _activeKey = null;
        _runningSince = null;
        _remainingMs = 0;
        Pill = null;
    }
}
