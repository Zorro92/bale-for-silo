using System.Globalization;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

/// <summary>
/// Transport input (TvPlayerRemoteKeyAction.kt): Play/Pause, Stop and skips from the controller,
/// keyboard and media remote, whether they arrive as keys or through the system media controls.
/// </summary>
public sealed partial class PlayerPage
{
    private readonly PlaybackTransportDedup _transportDedup = new();
    private readonly PlaybackControlsState _controls = new();
    private readonly PlaybackScrub _scrub = new();
    private readonly PlaybackHoldScan _holdScan = new();
    private VirtualKey? _holdKey;
    private long _lastControlsUpdate;

    /// <summary>App's key hook: true when the player acted on or swallowed the key.</summary>
    internal bool HandlePlayerKey(VirtualKey key, bool keyDown, bool firstDown)
    {
        try
        {
            if (!_active || _leaving) return false;
            // Any key keeps visible controls up; with them (or the error panel) up the D-pad and A
            // belong to focus navigation.
            if (keyDown) _controls.Input(Environment.TickCount64);
            // Releasing a held skip key ends its scan (a scan commits as one seek).
            if (!keyDown && _holdKey == key) EndHold(commit: true);
            var action = PlaybackRemoteKeys.Action((int)key, keyDown, firstDown,
                cleanPlayback: ErrorPanel.Visibility != Visibility.Visible && !_controls.Visible, modifier: ModifierHeld());
            switch (action)
            {
                case PlaybackKeyAction.None:
                    return false;
                case PlaybackKeyAction.PlayPause:
                    // Which route a remote's key takes on the console is logged once per press.
                    Log.Info("player: transport key=" + ((int)key).ToString(CultureInfo.InvariantCulture) + " play-pause");
                    if (_transportDedup.AcceptKey(Environment.TickCount64)) _ = RunPlaybackControlAsync(PlaybackControlRequest.Toggle, "key");
                    break;
                case PlaybackKeyAction.Select:
                    // A on clean playback (owner's map): pause and show the controls, or resume a pause
                    // whose controls have hidden, without bringing them back.
                    var paused = _attempt?.Media?.Player?.PlaybackSession.PlaybackState == MediaPlaybackState.Paused;
                    _ = RunPlaybackControlAsync(paused ? PlaybackControlRequest.Play : PlaybackControlRequest.Pause, "select");
                    if (!paused) ShowControls();
                    break;
                case PlaybackKeyAction.RevealControls:
                    // The first D-pad press only brings up the controls.
                    ShowControls();
                    break;
                case PlaybackKeyAction.PreviousChapter:
                case PlaybackKeyAction.NextChapter:
                    QueueChapter(action == PlaybackKeyAction.NextChapter);
                    break;
                case PlaybackKeyAction.Stop:
                    Log.Info("player: transport key=" + ((int)key).ToString(CultureInfo.InvariantCulture) + " stop");
                    OnBack(closeControls: false);
                    break;
                case PlaybackKeyAction.SkipBack:
                case PlaybackKeyAction.SkipForward:
                    // The press skips at once; held past a second it scans steadily (owner's map).
                    var forward = action == PlaybackKeyAction.SkipForward;
                    QueueSkip(forward);
                    _holdKey = key;
                    _holdScan.Press(forward, Environment.TickCount64);
                    break;
            }
            return true;
        }
        catch (Exception ex)
        {
            LogFailure("transport input", ex);
            return true;
        }
    }

    private static bool ModifierHeld()
    {
        var window = Window.Current?.CoreWindow;
        if (window is null) return false;
        return Down(VirtualKey.Control) || Down(VirtualKey.Menu) || Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows);
        bool Down(VirtualKey key) => (window.GetKeyState(key) & CoreVirtualKeyStates.Down) != 0;
    }

    private async Task RunPlaybackControlAsync(PlaybackControlRequest request, string route)
    {
        var (status, body) = await SetPlaybackAsync(request);
        if (!status.StartsWith("200", StringComparison.Ordinal))
            Log.Info($"player: transport {route} {request} not applied: {status} {body.Trim()}");
    }

    /// <summary>
    /// The system media controls (media remote, the Guide's media tile). MediaPlayer's command manager
    /// keeps owning them, so their status follows playback; Bale handles the commands so they take the
    /// same path as the keys: Play/Pause with state reporting, next/previous and fast-forward/rewind skip.
    /// </summary>
    private void AttachSystemControls(MediaPlayer player)
    {
        var commands = player.CommandManager;
        commands.IsEnabled = true;
        foreach (var behavior in new[] { commands.NextBehavior, commands.PreviousBehavior, commands.FastForwardBehavior, commands.RewindBehavior })
            behavior.EnablingRule = MediaCommandEnablingRule.Always;
        commands.PlayReceived += (_, args) => { args.Handled = true; SystemControl(player, PlaybackControlRequest.Play, null); };
        commands.PauseReceived += (_, args) => { args.Handled = true; SystemControl(player, PlaybackControlRequest.Pause, null); };
        commands.NextReceived += (_, args) => { args.Handled = true; SystemControl(player, null, true); };
        commands.FastForwardReceived += (_, args) => { args.Handled = true; SystemControl(player, null, true); };
        commands.PreviousReceived += (_, args) => { args.Handled = true; SystemControl(player, null, false); };
        commands.RewindReceived += (_, args) => { args.Handled = true; SystemControl(player, null, false); };
    }

    private void SystemControl(MediaPlayer source, PlaybackControlRequest? request, bool? skipForward) => _ = DispatchAsync(() =>
    {
        if (!_active || _leaving || _attempt is not { Media.Player: { } player } || !ReferenceEquals(player, source)) return;
        if (request is { } control)
        {
            Log.Info("player: transport system=" + control);
            if (_transportDedup.AcceptSystem(Environment.TickCount64)) _ = RunPlaybackControlAsync(control, "system");
        }
        else if (skipForward is { } forward)
        {
            Log.Info("player: transport system=" + (forward ? "forward" : "back"));
            QueueSkip(forward);
        }
    });

    /// <summary>Bumpers: the previous or next chapter of the playing file, through the skip queue.</summary>
    private void QueueChapter(bool forward)
    {
        try
        {
            if (!_active || _leaving || _suspended || _attempt is not { Attached: true } attempt
                || !_skipProcessing && attempt.Media is not { Opened: true, Started: true, FailureQueued: false }) return;
            var fileId = attempt.Plan?.Source?.MediaFileId ?? attempt.Start?.FileId;
            var chapters = attempt.Request.Detail.Versions.FirstOrDefault(version => version.FileId == fileId)?.Chapters ?? [];
            var origin = _skipTarget ?? SourcePosition(attempt);
            if (PlaybackChapters.Target(chapters.Select(chapter => chapter.StartSeconds), origin, forward) is not { } target) return;
            if (_duration is > 0 and { } end && double.IsFinite(end)) target = Math.Min(target, end);
            var index = chapters.Select(chapter => chapter.StartSeconds).OfType<double>().Order().ToList().FindIndex(start => Math.Abs(start - target) < 0.001);
            var chapter = chapters.FirstOrDefault(candidate => candidate.StartSeconds is { } start && Math.Abs(start - target) < 0.001);
            _skipLabel = string.IsNullOrWhiteSpace(chapter?.Title) ? index >= 0 ? $"Chapter {index + 1}" : "Start" : chapter!.Title!.Trim();
            _pendingSkipTarget = _skipTarget = target;
            ShowSkipFeedback((int)Math.Round(target - origin), target);
            if (!_skipProcessing) _ = ProcessSkipsAsync();
        }
        catch (Exception ex) { LogFailure("chapter input", ex); }
    }

    // ---- On-screen controls (TvPlayerScreen.kt TvPlayerIdleOverlay) ----

    private void InitializeControls()
    {
        foreach (var button in new[] { SkipBackButton, PlayPauseButton, SkipForwardButton, AudioButton, SubtitlesButton, CloseButton })
        {
            StyleTransportButton(button, false);
            button.GotFocus += (_, _) => StyleTransportButton(button, true);
            button.LostFocus += (_, _) => StyleTransportButton(button, false);
            button.XYFocusUp = ScrubberButton;
        }
        ScrubberButton.XYFocusDown = PlayPauseButton;
        ScrubberButton.XYFocusUp = ScrubberButton;
        SkipBackButton.XYFocusLeft = SkipBackButton;
        CloseButton.XYFocusLeft = SubtitlesButton;
        CloseButton.XYFocusRight = CloseButton;
        LinkTrackButtons();
    }

    /// <summary>Left/Right across the gap between the two groups (TvPlayerTransportCluster.kt), Audio hidden or not.</summary>
    private void LinkTrackButtons()
    {
        var audio = AudioButton.Visibility == Visibility.Visible;
        SkipForwardButton.XYFocusRight = audio ? AudioButton : SubtitlesButton;
        AudioButton.XYFocusLeft = SkipForwardButton;
        AudioButton.XYFocusRight = SubtitlesButton;
        SubtitlesButton.XYFocusLeft = audio ? AudioButton : SkipForwardButton;
        SubtitlesButton.XYFocusRight = CloseButton;
    }

    private static readonly Windows.UI.Xaml.Media.SolidColorBrush TransportInk = new(Windows.UI.Color.FromArgb(255, 11, 11, 12));
    private static readonly Windows.UI.Xaml.Media.SolidColorBrush TransportPaper = new(Windows.UI.Color.FromArgb(255, 237, 237, 237));

    /// <summary>
    /// TvPlayerTransportCluster.kt: Paper disc and Ink glyph on focus (the template recolours text; Paths
    /// don't inherit Foreground, so they are recoloured here), and a labelled button widens into a pill.
    /// </summary>
    private static void StyleTransportButton(Windows.UI.Xaml.Controls.Button button, bool focused)
    {
        var ink = focused ? TransportInk : TransportPaper;
        if (button.Content is Windows.UI.Xaml.Controls.StackPanel labelled)
        {
            VisualStateManager.GoToState(button, focused ? "Expanded" : "Collapsed", useTransitions: true);
            foreach (var child in labelled.Children)
            {
                if (child is Windows.UI.Xaml.Controls.TextBlock label) label.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
                else if (child is Windows.UI.Xaml.Controls.Viewbox { Child: Windows.UI.Xaml.Shapes.Path path }) path.Fill = ink;
            }
        }
        else if (button.Content is Windows.UI.Xaml.Controls.Panel glyph)
        {
            foreach (var child in glyph.Children)
                if (child is Windows.UI.Xaml.Controls.Viewbox { Child: Windows.UI.Xaml.Shapes.Path path }) path.Fill = ink;
        }
    }

    /// <summary>Show the controls with Play/Pause focused (TvIdleOverlayFocusTarget.Transport).</summary>
    private void ShowControls()
    {
        if (!_active || _leaving || _attempt is not { Request.Detail: var detail }) return;
        var wasVisible = _controls.Visible;
        _controls.Show(Environment.TickCount64);
        if (!wasVisible)
        {
            ControlsEyebrow.Text = PlaybackControlsText.Eyebrow(detail) ?? string.Empty;
            ControlsEyebrow.Visibility = ControlsEyebrow.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ControlsTitle.Text = PlaybackControlsText.Title(detail);
            TransportControls.Visibility = Visibility.Visible;
            HideTransport();
            DrawChapterGaps();
            UpdateTrackValues();
            LinkTrackButtons();
        }
        UpdateControls(force: true);
        if (!wasVisible) FocusRetry.Until(PlayPauseButton, () => !_controls.Visible || PlayPauseButton.Focus(FocusState.Keyboard));
    }

    private void HideControls()
    {
        if (!_controls.Visible) return;
        _scrub.Cancel();
        _controls.Hide();
        TransportControls.Visibility = Visibility.Collapsed;
        // Clean playback: the page holds focus so the next key reaches the player again.
        Focus(FocusState.Programmatic);
    }

    /// <summary>Back: a scrub preview first, then the controls, then the player (TvPlayerScreen.kt).</summary>
    private bool BackClosesControls()
    {
        if (TrackMenuOpen)
        {
            CloseTrackMenu();
            return true;
        }
        if (!_controls.Visible) return false;
        if (_scrub.Active)
        {
            _scrub.Cancel();
            UpdateControls(force: true);
        }
        else HideControls();
        return true;
    }

    /// <summary>OnTick: times and bar a few times a second, and the 5 s hide.</summary>
    private void UpdateControls(bool force = false)
    {
        try
        {
            if (!_controls.Visible) return;
            var now = Environment.TickCount64;
            if (_controls.ShouldHide(now, _scrub.Active || TrackMenuOpen))
            {
                HideControls();
                return;
            }
            if (!force && now - _lastControlsUpdate < 250) return;
            _lastControlsUpdate = now;
            if (_attempt is not { } attempt) return;
            var session = attempt.Media?.Player?.PlaybackSession;
            var playing = session?.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Buffering or MediaPlaybackState.Opening;
            PauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            PlayGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(PlayPauseButton, playing ? "Pause" : "Play");
            var position = _holdScan.Target ?? _scrub.Preview ?? _skipTarget ?? SourcePosition(attempt);
            var duration = _duration is > 0 and { } end && double.IsFinite(end) ? end : (double?)null;
            var hours = duration >= 3600;
            ControlsElapsed.Text = PlaybackTransportFeedback.Clock(position, forceHours: hours);
            var remaining = duration is { } total ? PlaybackTransportFeedback.Remaining(position, total) : "—";
            if (duration is { } known && !_scrub.Active)
            {
                var left = Math.Max(0, known - position);
                var speed = session?.PlaybackRate ?? 1;
                var wall = speed > 0 && double.IsFinite(speed) ? left / speed : left;
                var clock = DateTime.Now;
                if (wall > 0 && wall < (DateTime.MaxValue - clock).TotalSeconds) remaining += " · Ends " + clock.AddSeconds(wall).ToString("t");
            }
            ControlsRemaining.Text = remaining;
            var width = ScrubberTrack.ActualWidth;
            var fraction = PlaybackTransportFeedback.Progress(position, duration);
            ScrubberPlayed.Width = width * fraction;
            ScrubberPuckShift.X = width * fraction - ScrubberPuck.Width / 2;
        }
        catch (Exception ex) { LogFailure("controls", ex); }
    }

    /// <summary>OnTick: advance a held trigger's scan and show where it will land.</summary>
    private void UpdateHoldScan()
    {
        try
        {
            if (!_holdScan.Held) return;
            if (!_active || _leaving || _suspended || _attempt is not { } attempt)
            {
                EndHold(commit: false);
                return;
            }
            var started = _holdScan.Target is null;
            if (_holdScan.Tick(Environment.TickCount64, _skipTarget ?? SourcePosition(attempt), _duration) is not { } target) return;
            if (started) Log.Info("player: hold scan " + (_holdScan.Direction > 0 ? "forward" : "back"));
            _skipLabel = _holdScan.Direction > 0 ? "Fast-forward" : "Rewind";
            ShowSkipFeedback(0, target);
            _controls.Input(Environment.TickCount64);
            UpdateControls(force: true);
        }
        catch (Exception ex) { LogFailure("hold scan", ex); }
    }

    private void EndHold(bool commit)
    {
        _holdKey = null;
        if (_holdScan.Release() is { } target && commit) QueueSeekTo(target);
    }

    private void OnScrubberSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChapterGaps();
        UpdateControls(force: true);
    }

    /// <summary>The playing file's chapter starts as gaps in the bar (none at 0 or at the end).</summary>
    private void DrawChapterGaps()
    {
        try
        {
            ScrubberChapters.Children.Clear();
            if (_attempt is not { } attempt || _duration is not (> 0 and { } duration) || !double.IsFinite(duration)) return;
            var fileId = attempt.Plan?.Source?.MediaFileId ?? attempt.Start?.FileId;
            var chapters = attempt.Request.Detail.Versions.FirstOrDefault(version => version.FileId == fileId)?.Chapters ?? [];
            var width = ScrubberTrack.ActualWidth;
            var height = ScrubberTrack.ActualHeight;
            foreach (var start in chapters.Select(chapter => chapter.StartSeconds).OfType<double>())
            {
                if (!double.IsFinite(start) || start <= 1 || start >= duration - 1) continue;
                var gap = new Windows.UI.Xaml.Shapes.Rectangle
                {
                    Width = 2, Height = height,
                    Fill = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xE0, 0, 0, 0)),
                };
                Windows.UI.Xaml.Controls.Canvas.SetLeft(gap, width * start / duration - 1);
                ScrubberChapters.Children.Add(gap);
            }
        }
        catch (Exception ex) { LogFailure("chapter gaps", ex); }
    }

    /// <summary>TvPlayerScrubber.kt: 4 -> 5 (focused) -> 7 (scrubbing) track, 15 -> 21 puck.</summary>
    private void OnScrubberFocus(object sender, RoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement() == ScrubberButton;
        if (!focused) _scrub.Cancel();
        ScrubberTrack.Height = _scrub.Active ? 7 : focused ? 5 : 4;
        ScrubberPuck.Width = ScrubberPuck.Height = focused ? 21 : 15;
        UpdateControls(force: true);
    }

    /// <summary>
    /// TvPlayerTransportCluster.kt onMoveUpToScrubber / TvPlayerScrubber.kt onMoveDownToTransport: Up from
    /// the buttons and Down from the bar move focus explicitly instead of trusting directional search.
    /// </summary>
    private void OnControlsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
#if DEBUG_SERVER
            Log.Info($"player: controls key={(int)e.Key} focus={(FocusManager.GetFocusedElement() as FrameworkElement)?.Name}");
#endif
            var up = e.Key is VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp;
            var down = e.Key is VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown;
            var onBar = FocusManager.GetFocusedElement() == ScrubberButton;
            if (up && !onBar)
            {
                e.Handled = true;
                ScrubberButton.Focus(FocusState.Keyboard);
            }
            else if (up && onBar) e.Handled = true;
            else if (down && onBar)
            {
                e.Handled = true;
                if (_scrub.Active)
                {
                    _scrub.Cancel();
                    UpdateControls(force: true);
                }
                PlayPauseButton.Focus(FocusState.Keyboard);
            }
            else if (onBar) OnScrubberKeyDown(sender, e);
        }
        catch (Exception ex) { LogFailure("controls input", ex); }
    }

    /// <summary>Left/Right move the preview 10 s; Down cancels it and moves to the buttons.</summary>
    private void OnScrubberKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            var forward = e.Key switch
            {
                VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight => true,
                VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft => (bool?)false,
                _ => null,
            };
            if (forward is { } direction && _attempt is { } attempt)
            {
                e.Handled = true;
                _scrub.Nudge(_skipTarget ?? SourcePosition(attempt), _duration, direction);
                ScrubberTrack.Height = 7;
                UpdateControls(force: true);
            }
            else if (e.Key is VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown && _scrub.Active)
            {
                _scrub.Cancel();
                ScrubberTrack.Height = 5;
                UpdateControls(force: true);
            }
        }
        catch (Exception ex) { LogFailure("scrubber input", ex); }
    }

    /// <summary>Select commits the preview as one seek; with no preview it toggles playback.</summary>
    private void OnScrubberClick(object sender, RoutedEventArgs e)
    {
        if (_scrub.Commit() is { } target)
        {
            ScrubberTrack.Height = 5;
            QueueSeekTo(target);
        }
        else _ = RunPlaybackControlAsync(PlaybackControlRequest.Toggle, "scrubber");
        UpdateControls(force: true);
    }

    /// <summary>A committed scrub: one absolute seek through the skip queue.</summary>
    private void QueueSeekTo(double target)
    {
        try
        {
            if (!_active || _leaving || _suspended || _attempt is not { Attached: true } attempt
                || !_skipProcessing && attempt.Media is not { Opened: true, Started: true, FailureQueued: false }) return;
            _skipLabel = null;
            _pendingSkipTarget = _skipTarget = target;
            if (!_skipProcessing) _ = ProcessSkipsAsync();
        }
        catch (Exception ex) { LogFailure("scrub commit", ex); }
    }

    private void OnSkipBackClick(object sender, RoutedEventArgs e) => QueueSkip(false);

    private void OnSkipForwardClick(object sender, RoutedEventArgs e) => QueueSkip(true);

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        _ = RunPlaybackControlAsync(PlaybackControlRequest.Toggle, "button");
        UpdateControls(force: true);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => OnBack(closeControls: false);
}
