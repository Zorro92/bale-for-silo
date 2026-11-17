using System.Globalization;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Bale.Xbox;

/// <summary>
/// Transport input (TvPlayerRemoteKeyAction.kt): Play/Pause, Stop and skips from the controller,
/// keyboard and media remote, whether they arrive as keys or through the system media controls.
/// </summary>
public sealed partial class PlayerPage
{
    private readonly PlaybackTransportDedup _transportDedup = new();

    /// <summary>App's key hook: true when the player acted on or swallowed the key.</summary>
    internal bool HandlePlayerKey(VirtualKey key, bool keyDown, bool firstDown)
    {
        try
        {
            if (!_active || _leaving) return false;
            // No on-screen controls yet (player-controls.md step 2): only the error panel owns focus.
            var action = PlaybackRemoteKeys.Action((int)key, keyDown, firstDown,
                cleanPlayback: ErrorPanel.Visibility != Visibility.Visible, modifier: ModifierHeld());
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
                    // A on clean playback: pause (step 2 also shows the controls), or resume a pause
                    // whose controls have hidden, without bringing them back.
                    var paused = _attempt?.Media?.Player?.PlaybackSession.PlaybackState == MediaPlaybackState.Paused;
                    _ = RunPlaybackControlAsync(paused ? PlaybackControlRequest.Play : PlaybackControlRequest.Pause, "select");
                    break;
                case PlaybackKeyAction.RevealControls:
                    // Step 2 shows the controls here; until then the press is only swallowed.
                    break;
                case PlaybackKeyAction.PreviousChapter:
                case PlaybackKeyAction.NextChapter:
                    QueueChapter(action == PlaybackKeyAction.NextChapter);
                    break;
                case PlaybackKeyAction.Stop:
                    Log.Info("player: transport key=" + ((int)key).ToString(CultureInfo.InvariantCulture) + " stop");
                    OnBack();
                    break;
                case PlaybackKeyAction.SkipBack:
                case PlaybackKeyAction.SkipForward:
                    QueueSkip(action == PlaybackKeyAction.SkipForward);
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
}
