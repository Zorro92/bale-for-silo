using System.Globalization;
using Bale.Core.Playback;
using Bale.Core.Settings;
using Bale.Xbox.Services;
using Windows.Foundation;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox;

/// <summary>
/// Skip Intro and Skip Credits (silo-android TvPlayerViewModel.kt, TvPlayerScreen.kt, TvIntroAutoSkipBanner.kt; the
/// credits setting from silo-apple PlayerViewModel.swift). Core's <see cref="PlaybackSkipMarkers"/> decides from the
/// playing file's markers, <c>playback.intro_skip_mode</c> and <c>playback.auto_skip_credits</c>; this page feeds it the
/// playhead and playback state every tick, seeks through the skip queue (the same path as the triggers and the
/// scrubber), and draws the one pill in the bottom-end slot:
/// <list type="bullet">
/// <item>The intro pill takes focus as it appears, unless a scrub preview or a held trigger owns the input
/// (mayTakeFocus). With the controls hidden, A / Enter press it wherever focus is (the root key handler) and Back
/// dismisses it; with them up, it is a button reached by Up from the bar, and Down goes back to the bar.</item>
/// <item>The recap and credits pill shows while its range plays, never takes focus, and takes A / Enter the same way
/// with the controls hidden; Back passes it by.</item>
/// <item>A pill that hides with focus hands it to the bar with the controls up, else to the page.</item>
/// <item>Options, a track menu, Up Next and the error screen cover it (TvPlayerOverlays: !hudOpen &amp;&amp; !showNextUp);
/// its timer keeps running underneath.</item>
/// </list>
/// </summary>
public sealed partial class PlayerPage
{
    private static readonly SolidColorBrush SkipFillFocused = new(Windows.UI.Color.FromArgb(0x66, 255, 255, 255));
    private static readonly SolidColorBrush SkipFillResting = new(Windows.UI.Color.FromArgb(0x24, 255, 255, 255));

    private readonly PlaybackSkipMarkers _skipMarkers = new();
    private readonly RectangleGeometry _skipFillClip = new();
    private MarkerTimeline _markers = MarkerTimeline.Empty;
    private Attempt? _markersAttempt;
    private string? _markersFile;
    private string _markersSource = "none";
    private (SkipPillKind Kind, int Run)? _skipPillShown;
    private float _skipPillLift = float.NaN;

    private bool SkipPillVisible => SkipPillHost.Visibility == Visibility.Visible;

    private bool SkipPillHasFocus() => ReferenceEquals(FocusManager.GetFocusedElement(), SkipPillButton);

    private void InitializeSkipPill()
    {
        SkipPillFill.Clip = _skipFillClip;
        SkipPillHost.Opacity = 0;
        // TvIntroAutoSkipBanner.kt: a 200 ms fade in, no exit animation. Plain elements, not a ControlTemplate.
        SkipPillHost.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) };
        SkipPillHost.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(220) };
    }

    /// <summary>A new attempt: every intro and credits decision starts over, and the markers are read again.</summary>
    private void ResetSkipMarkers()
    {
        _skipMarkers.Reset();
        _markers = MarkerTimeline.Empty;
        _markersAttempt = null;
        _markersFile = null;
        _markersSource = "none";
        HideSkipPill();
    }

    /// <summary>
    /// The playing file's markers: the catalog version's (first occurrence of each kind) at once, then the file's own read
    /// (every occurrence, and on-demand online markers the server fills for the file it names).
    /// </summary>
    private MarkerTimeline PlayingMarkers(Attempt attempt, string? fileId)
    {
        if (ReferenceEquals(_markersAttempt, attempt) && _markersFile == fileId) return _markers;
        _markersAttempt = attempt;
        _markersFile = fileId;
        _markers = MarkerTimeline.From(attempt.Request.Detail.Versions.FirstOrDefault(version => version.FileId == fileId));
        _markersSource = "detail";
        if (fileId is { Length: > 0 } file) _ = ReadMarkersAsync(attempt, file);
        return _markers;
    }

    private async Task ReadMarkersAsync(Attempt attempt, string fileId)
    {
        try
        {
            var markers = await attempt.Client.Playback.FileMarkersAsync(fileId, attempt.Cancellation.Token);
            if (!ReferenceEquals(_markersAttempt, attempt) || _markersFile != fileId) return;
            _markers = markers;
            _markersSource = "file";
            // Counts only, never times or titles.
            Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"player: markers intro={markers.Count(MarkerKind.Intro)} recap={markers.Count(MarkerKind.Recap)} credits={markers.Count(MarkerKind.Credits)}"));
        }
        catch (OperationCanceledException) when (attempt.Cancellation.IsCancellationRequested) { }
        catch (Exception ex) { LogFailure("markers read", ex); }
    }

    /// <summary>OnTick: feed the skip markers, seek for an automatic skip, draw the pill.</summary>
    private void UpdateSkipMarkers()
    {
        try
        {
            if (!_active || _leaving || _suspended || _attempt is not { } attempt)
            {
                HideSkipPill();
                return;
            }
#if DEBUG_SERVER
            if (attempt.DebugUrl is not null)
            {
                HideSkipPill();
                return;
            }
#endif
            if (_skipMarkers.Update(SkipFacts(attempt)) is { } skip) SkipTo(skip);
            ShowSkipPill();
        }
        catch (Exception ex) { LogFailure("skip markers", ex); }
    }

    private SkipMarkerFacts SkipFacts(Attempt attempt)
    {
        var fileId = attempt.Plan?.Source?.MediaFileId ?? attempt.Start?.FileId;
        var presented = attempt.Media is { Opened: true, Started: true, FailureQueued: false, Player: not null }
            && ErrorPanel.Visibility != Visibility.Visible;
        var state = presented ? attempt.Media!.Player!.PlaybackSession.PlaybackState : MediaPlaybackState.None;
        // The viewer's pause (or the end) freezes the intro timer at once; a seek, a reload or a rebuffer only after its
        // grace window, and none of them starts an offer.
        var activity = _bufferingEnded || state == MediaPlaybackState.Paused ? PromptActivity.Paused
            : state == MediaPlaybackState.Playing && !_seeking && !_skipProcessing ? PromptActivity.Playing
            : PromptActivity.Stalled;
        var settings = attempt.Client.Settings;
        return new SkipMarkerFacts
        {
            // Where playback is heading: a seek in flight counts at its target, as TvPlayerViewModel.kt pre-writes it.
            Position = _skipTarget ?? SourcePosition(attempt),
            Markers = PlayingMarkers(attempt, fileId),
            FileKey = fileId,
            Mode = settings.IntroSkip.Value,
            AutoSkipCredits = settings.AutoSkipCredits.Value,
            Activity = activity,
            Presented = presented,
            Loading = _state == "loading",
            NowMs = Environment.TickCount64,
        };
    }

    /// <summary>
    /// A real seek through the skip queue (QueueSeekTo): it coalesces with a seek in flight and shows no skip chip, as
    /// TvPlayerViewModel.kt's seekWithoutViewerAction / seekImmediate don't. A target at the end of the file ends the
    /// stream there, which brings Up Next or leaves, as any end does.
    /// </summary>
    private void SkipTo(SkipMarkerSeek skip)
    {
        var target = _duration is > 0 and { } end && double.IsFinite(end) ? Math.Min(skip.Target, end) : skip.Target;
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"player: skip {skip.Kind}{(skip.Automatic ? " auto" : string.Empty)} target={target:0.###}"));
        QueueSeekTo(target);
    }

    private void ShowSkipPill()
    {
        // TvPlayerOverlays composes no pill under the HUD (Options here) or Up Next, so one coming back is a new pill that
        // takes focus again.
        if (_skipMarkers.Pill is not { } pill || OptionsOpen || UpNextVisible || ErrorPanel.Visibility == Visibility.Visible)
        {
            HideSkipPill();
            return;
        }
        // The track menu (TvQuickSubtitlePicker's place) covers it without taking it down there: hidden here so the two
        // don't overlap, but the same pill comes back without claiming focus again.
        if (TrackMenuOpen)
        {
            CollapseSkipPill();
            return;
        }
        var appeared = _skipPillShown != (pill.Kind, pill.Run);
        if (appeared)
        {
            _skipPillShown = (pill.Kind, pill.Run);
            SkipPillButton.Content = pill.Label;
            AutomationProperties.SetName(SkipPillButton, pill.Label);
            SkipPillCaption.Text = pill.Caption ?? string.Empty;
            SkipPillCaption.Visibility = pill.Caption is null ? Visibility.Collapsed : Visibility.Visible;
        }
        if (!SkipPillVisible)
        {
            SkipPillHost.Visibility = Visibility.Visible;
            SkipPillHost.Opacity = 1;
        }
        UpdateSkipPillLift();
        var fill = new Rect(0, 0, pill.Timed ? Math.Max(0, SkipPillButton.ActualWidth * pill.Progress) : 0, Math.Max(0, SkipPillButton.ActualHeight));
        if (_skipFillClip.Rect != fill) _skipFillClip.Rect = fill;
        if (!appeared) return;
        // TvIntroAutoSkipBanner.kt: the intro pill takes focus as it appears, unless a scrub or a held trigger owns the
        // input (mayTakeFocus). The marker pill never takes focus, nor keeps focus the intro pill had in this slot.
        if (pill.TakesFocus && !_scrub.Active && !_holdScan.Held)
            FocusRetry.Until(SkipPillButton, () => !SkipPillVisible || SkipPillButton.Focus(FocusState.Keyboard));
        else if (!pill.TakesFocus && SkipPillHasFocus()) RefocusAfterSkipPill();
    }

    private void HideSkipPill()
    {
        _skipPillShown = null;
        CollapseSkipPill();
    }

    /// <summary>Off screen at once (no exit animation); the pill's identity stays for <see cref="ShowSkipPill"/>.</summary>
    private void CollapseSkipPill()
    {
        if (!SkipPillVisible) return;
        var hadFocus = SkipPillHasFocus();
        SkipPillHost.Visibility = Visibility.Collapsed;
        SkipPillHost.Opacity = 0;
        if (hadFocus) RefocusAfterSkipPill();
    }

    /// <summary>
    /// TvPlayerScreen.kt refocusAfterPillHides: a pill takes its focus with it when it hides. The bar with the controls up,
    /// otherwise the page (or a buffering pill on screen), which must own focus while the controls are hidden. Options,
    /// a track menu and Up Next place their own focus.
    /// </summary>
    private void RefocusAfterSkipPill()
    {
        if (OptionsOpen || TrackMenuOpen || UpNextVisible || ErrorPanel.Visibility == Visibility.Visible) return;
        if (_controls.Visible) ScrubberButton.Focus(FocusState.Keyboard);
        else if (!FocusBufferingPill()) Focus(FocusState.Programmatic);
    }

    /// <summary>TvPlayerOverlays: 56 above the bottom, raised to 200 (clear of the title, bar and buttons) while the controls show.</summary>
    private void UpdateSkipPillLift()
    {
        var target = _controls.Visible ? -(200f - 56f) : 0f;
        if (target == _skipPillLift) return;
        _skipPillLift = target;
        SkipPillHost.Translation = new System.Numerics.Vector3(0, target, 0);
    }

    /// <summary>
    /// The pill's action: the intro offer seeks to its end, the undo back to its start, the marker pill to its occurrence's
    /// end. True when a pill took the press.
    /// </summary>
    private bool SelectSkipPill()
    {
        try
        {
            if (!SkipPillVisible || !_active || _leaving || _attempt is not { } attempt
                || _skipMarkers.Select(SkipFacts(attempt)) is not { } skip) return false;
            SkipTo(skip);
            // The pill hides with the press; focus it had goes to the bar (controls up) or the page.
            HideSkipPill();
            return true;
        }
        catch (Exception ex)
        {
            LogFailure("skip pill", ex);
            return false;
        }
    }

    /// <summary>
    /// Back on a visible intro pill: hidden, the intro resolved, playback where it is; the press is consumed and a second
    /// Back behaves normally. The marker pill takes no Back.
    /// </summary>
    private bool BackDismissesSkipPill()
    {
        if (!SkipPillVisible || !_skipMarkers.Back()) return false;
        Log.Info("player: skip intro dismissed");
        HideSkipPill();
        return true;
    }

    private void OnSkipPillClick(object sender, RoutedEventArgs e) => SelectSkipPill();

    /// <summary>TvIntroPromptPill: lit when focused (white ring and label, white 40% fill), dimmed otherwise (14% fill).</summary>
    private void OnSkipPillFocus(object sender, RoutedEventArgs e) =>
        SkipPillFill.Background = SkipPillHasFocus() ? SkipFillFocused : SkipFillResting;

    /// <summary>
    /// With the controls up: Down goes back to the bar (TvPlayerScreen.kt "where Down from the pill goes"); Up, Left and
    /// Right stay on the pill (the triggers still skip, so the pill never blocks seeking). With the controls hidden the
    /// root key handler takes the D-pad first and brings them up.
    /// </summary>
    private void OnSkipPillKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            switch (e.Key)
            {
                case VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown:
                    e.Handled = true;
                    if (_controls.Visible) ScrubberButton.Focus(FocusState.Keyboard);
                    else ShowControls();
                    break;
                case VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp
                    or VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft
                    or VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight:
                    e.Handled = true;
                    break;
            }
        }
        catch (Exception ex) { LogFailure("skip pill input", ex); }
    }

#if DEBUG_SERVER
    /// <summary>For <c>bale-debug.sh where</c>: the markers in use, the settings and the pill (counts and kinds only).</summary>
    internal string DebugSkipMarkers
    {
        get
        {
            var settings = _attempt?.Client.Settings;
            return string.Create(CultureInfo.InvariantCulture,
                $"source={_markersSource} intro={_markers.Count(MarkerKind.Intro)} recap={_markers.Count(MarkerKind.Recap)} credits={_markers.Count(MarkerKind.Credits)}"
                + $" mode={IntroSkipModes.Wire(settings?.IntroSkip.Value ?? IntroSkipModes.Default)} auto_credits={(settings?.AutoSkipCredits.Value == true ? "on" : "off")}"
                + $" pill={(_skipMarkers.Pill is { } pill ? pill.Kind.ToString() + (SkipPillVisible ? string.Empty : " (covered)") : "none")}"
                + $" focused={(SkipPillHasFocus() ? "yes" : "no")}");
        }
    }
#endif
}
