using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

/// <summary>
/// Mid-playback audio and subtitle picks (TvPlayerViewModel.kt selectAudioOption / selectSubtitleOption):
/// the Detail page's track lists for the playing file, and a track_change replan of the same session at
/// the current position, remounted like a seek reanchor. A refused change keeps the current playback.
/// </summary>
public sealed partial class PlayerPage
{
    private PlaybackSelectorKind? _trackMenuKind;
    private Button? _trackMenuOpener;

    private void InitializeTracks()
    {
        TrackMenu.Chosen += OnTrackChosen;
        TrackMenu.Dismissed += CloseTrackMenu;
    }

    /// <summary>What plays now, as a Detail selection of the playing file: the plan's tracks.</summary>
    private PlaybackSelection? PlayingSelection(out ItemDetail? detail)
    {
        detail = null;
        if (_attempt is not { Plan: { } plan } attempt) return null;
        detail = attempt.Request.Detail;
        var fileId = plan.Source?.MediaFileId ?? attempt.Start?.FileId;
        var audio = plan.SelectedTracks?.Audio?.Index ?? attempt.Start?.AudioTrackIndex;
        var subtitle = plan.SelectedTracks?.Subtitle?.Index ?? attempt.Start?.SubtitleTrackIndex ?? -1;
        return new PlaybackSelection(fileId, audio is { } a ? checked((int)a) : null, checked((int)subtitle));
    }

    /// <summary>The Detail lists without their Auto rows: in the player a pick names a track (or Off).</summary>
    private IReadOnlyList<PlaybackSelectorOption> TrackOptions(PlaybackSelectorKind kind)
    {
        if (PlayingSelection(out var detail) is not { } selection || detail is null) return [];
        // The Detail page's "Start without subtitles" describes a start, not a running player.
        return PlaybackFormatting.SelectorOptions(kind, detail, selection).Where(option => option.TrackIndex is not null)
            .Select(option => option.TrackIndex == -1 ? option with { Detail = "No subtitles" } : option).ToList();
    }

    /// <summary>The value a focused Audio / Subtitles pill shows ("English", "Off").</summary>
    private void UpdateTrackValues()
    {
        try
        {
            AudioValue.Text = TrackOptions(PlaybackSelectorKind.Audio).FirstOrDefault(option => option.IsSelected)?.Title ?? string.Empty;
            SubtitlesValue.Text = TrackOptions(PlaybackSelectorKind.Subtitles).FirstOrDefault(option => option.IsSelected)?.Title ?? "Off";
            AudioButton.Visibility = TrackOptions(PlaybackSelectorKind.Audio).Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { LogFailure("track values", ex); }
    }

    private void OnAudioClick(object sender, RoutedEventArgs e) => OpenTrackMenu(PlaybackSelectorKind.Audio, AudioButton, "Audio");

    private void OnSubtitlesClick(object sender, RoutedEventArgs e) => OpenTrackMenu(PlaybackSelectorKind.Subtitles, SubtitlesButton, "Subtitles");

    private void OpenTrackMenu(PlaybackSelectorKind kind, Button opener, string label)
    {
        var options = TrackOptions(kind);
        if (options.Count == 0) return;
        _trackMenuKind = kind;
        _trackMenuOpener = opener;
        TrackMenu.Show(label, options, opener);
    }

    private bool TrackMenuOpen => TrackMenu.IsOpen;

    private void CloseTrackMenu()
    {
        TrackMenu.Hide();
        var opener = _trackMenuOpener;
        _trackMenuKind = null;
        _trackMenuOpener = null;
        _controls.Input(Environment.TickCount64);
        if (opener is not null && _controls.Visible) FocusRetry.Until(opener, () => !_controls.Visible || opener.Focus(FocusState.Keyboard));
    }

    private void OnTrackChosen(PlaybackSelectorOption option)
    {
        var kind = _trackMenuKind;
        CloseTrackMenu();
        if (kind is null || option.IsSelected || option.TrackIndex is not { } index) return;
        _ = kind == PlaybackSelectorKind.Audio ? ChangeTracksAsync(index, null) : ChangeTracksAsync(null, index);
    }

    private async Task ChangeTracksAsync(int? audioIndex, int? subtitleIndex)
    {
        // Never queue behind an open, replan or seek; the viewer can pick again.
        if (!_lifecycle.Wait(0))
        {
            Log.Info("player: track change busy");
            return;
        }
        _seeking = true;
        try
        {
            if (_attempt is not { Media: { Opened: true, FailureQueued: false, Player: { } player } media } attempt
                || !IsCurrent(attempt, media)) return;
            if (TrySwitchAudioLocally(attempt, media, audioIndex, subtitleIndex)) return;
            var paused = player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused;
            var position = SourcePosition(attempt);
            var request = PlaybackReplan.ForTrackChange(attempt.Start!, attempt.Plan!, attempt.PlanAttemptId, attempt.AttemptedPlanKeys,
                attempt.Replanned ? 2 : 1, position, audioIndex, subtitleIndex);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var decision = await attempt.Client.Playback.ReplanAsync(attempt.SessionId!, request, attempt.Services!.Installation, deadline.Token);
            // Outcome is a server enum code; never log the plan or its URL.
            Log.Info($"player: track change outcome={decision.Outcome} audio={audioIndex?.ToString() ?? "-"} subtitle={subtitleIndex?.ToString() ?? "-"}");
            if (!IsCurrent(attempt, media)) return;
            if (!PlaybackMessages.ValidatePlayable(decision).IsPlayable || decision.SessionId != attempt.SessionId)
            {
                Log.Info("player: track change refused; playback continues unchanged");
                return;
            }

            Pause(attempt);
            DisposeSubtitles();
            DisposeMount(attempt);
            attempt.Plan = decision.PlaybackPlan!;
            attempt.ServerFeatures = decision.ServerFeatures ?? [];
            attempt.Start = PlaybackReplan.WithTracks(attempt.Start!, request.SelectedTracks);
            attempt.PlanAttemptId = Guid.NewGuid().ToString();
            attempt.Position = _sourcePosition = PlaybackProgressive.MountSourceStart(attempt.Plan, position);
            _state = "loading";
            SetLoading(true);
            var timeline = PlaybackTimeline.From(attempt.Plan.Timeline);
            await MountPlanAsync(attempt, PlaybackProgressive.MountPlayerStart(attempt.Plan,
                timeline.PlayerForSource(position) ?? timeline.PlayerStartSeconds), paused);
            await OpenedReplacementAsync(attempt);
            UpdateTrackValues();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogFailure("track change", ex); }
        finally
        {
            _seeking = false;
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// TvPlayerViewModel.kt selectAudioOption: a direct play of the original already carries every audio track, so
    /// an audio pick selects it on the player (no replan, no remount, no rebuffer). False when the pick needs the
    /// server: a subtitle change, a remux/transcode, or a track the mount cannot map, which then replans as before.
    /// </summary>
    private bool TrySwitchAudioLocally(Attempt attempt, Mount media, int? audioIndex, int? subtitleIndex)
    {
        if (subtitleIndex is not null || audioIndex is not { } index || media.Player is null
            || !PlaybackReplan.SwitchesAudioLocally(attempt.Plan, index)) return false;
        try
        {
            SelectOriginalAudioTrack(attempt, media, index);
        }
        catch (PlaybackAudioSelectionException)
        {
            Log.Info("player: local audio switch unavailable; replanning");
            return false;
        }
        attempt.Start = PlaybackReplan.WithLocalAudio(attempt.Start!, attempt.Plan!, index);
        Log.Info($"player: audio switched locally audio={index}");
        UpdateTrackValues();
#if DEBUG_SERVER
        RefreshStats(attempt);
#endif
        return true;
    }
}
