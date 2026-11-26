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
/// A pick becomes the series' or movie's track preference once a plan plays it (PlayerTrackPreferences).
/// </summary>
public sealed partial class PlayerPage
{
    private PlaybackSelectorKind? _trackMenuKind;
    private Button? _trackMenuOpener;
    private readonly PlayerTrackPreferences _trackPicks = new();

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

    /// <summary>
    /// The Detail lists without their Auto rows: in the player a pick names a track (or Off). Subtitles end with a row per
    /// in-band closed caption channel the player has found (PlaybackClosedCaptions.WithEntries, PlayerPage.Captions.cs).
    /// </summary>
    private IReadOnlyList<PlaybackSelectorOption> TrackOptions(PlaybackSelectorKind kind)
    {
        if (PlayingSelection(out var detail) is not { } selection || detail is null) return [];
        // The Detail page's "Start without subtitles" describes a start, not a running player.
        var options = PlaybackFormatting.SelectorOptions(kind, detail, selection).Where(option => option.TrackIndex is not null)
            .Select(option => option.TrackIndex == -1 ? option with { Detail = "No subtitles" } : option).ToList();
        return kind == PlaybackSelectorKind.Subtitles && _attempt is { } attempt ? attempt.Captions.WithEntries(options) : options;
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
        // The checked row too: picking what plays switches nothing, but it is the viewer's choice now (ChangeTracksAsync).
        if (kind is null || option.TrackIndex is not { } index) return;
        // A subtitle row may be in-band closed captions, which the player draws itself (PlayerPage.Captions.cs).
        _ = kind == PlaybackSelectorKind.Audio ? ChangeTracksAsync(index, null) : PickSubtitleAsync(index);
    }

    /// <param name="remember">
    /// False for a step of another pick rather than the viewer's own choice (closed captions turning a server subtitle off
    /// first): no preference waits for the plan it brings.
    /// </param>
    private async Task ChangeTracksAsync(int? audioIndex, int? subtitleIndex, bool remember = true)
    {
        // Never queue behind an open, replan or seek; the viewer can pick again.
        if (!_lifecycle.Wait(0))
        {
            Log.Info("player: track change busy");
            return;
        }
        _seeking = true;
        _remounting = true;
        // A seek made while the change is in flight is kept for the stream it brings (PlayerPage.Seek.cs, TakeReplanSeek).
        BeginReplanSeeks();
        PlayerTrackPick? pick = null;
        // Once a plan is adopted the pick waits for a plan that plays it; before that, a change that ends is refused.
        var adopted = false;
        try
        {
            if (_attempt is not { Media: { Opened: true, FailureQueued: false, Player: { } player } media } attempt
                || !IsCurrent(attempt, media)) return;
#if DEBUG_SERVER
            if (attempt.DebugUrl is not null) return;
#endif
            pick = !remember ? null
                : audioIndex is { } audio ? new PlayerTrackPick(PlayerTrackKind.Audio, audio, attempt.Start!.FileId)
                : subtitleIndex is { } subtitle ? new PlayerTrackPick(PlayerTrackKind.Subtitle, subtitle, attempt.Start!.FileId) : null;
            if (pick is not null)
            {
                _trackPicks.Picked(pick);
                // TvPlayerViewModel.kt rememberSeriesSubtitleChoice / confirmDesiredAudio: a pick of what already plays has
                // nothing to switch or commit and is saved at once.
                if (attempt.Plan is { } playing && PlayerTrackPreferences.Plays(pick, playing, attempt.Start!.FileId))
                {
                    adopted = true;
                    SaveTrackPicks(attempt);
                    return;
                }
            }
            if (TrySwitchAudioLocally(attempt, media, audioIndex, subtitleIndex))
            {
                adopted = true;
                return;
            }
            var paused = player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused;
            // silo-server 4a73f48/d22e55f: a seek still waiting, not the clock it has not moved yet.
            var position = ReplanSeekPosition(attempt);
            var request = PlaybackReplan.ForTrackChange(attempt.Start!, attempt.Plan!, attempt.PlanAttemptId, attempt.AttemptedPlanKeys,
                attempt.Replanned ? 2 : 1, position, audioIndex, subtitleIndex);
            // After a lowering, a track change keeps asking for it (the kept step-down estimate, no source).
            request = PlaybackReplan.WithClientReplan(request, ClientBufferingReplan("track_change"));
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

            if (TryAdoptSubtitleInPlace(attempt, media, decision, request, audioIndex))
            {
                adopted = true;
                return;
            }
            Pause(attempt);
            DisposeSubtitles();
            DisposeMount(attempt);
            attempt.Plan = decision.PlaybackPlan!;
            // Saved when the replacement opens, if the answer plays the pick (OnMediaOpened, SaveTrackPicks).
            adopted = true;
            attempt.ServerFeatures = decision.ServerFeatures ?? [];
            attempt.Start = PlaybackReplan.WithTracks(attempt.Start!, request.SelectedTracks);
            attempt.PlanAttemptId = Guid.NewGuid().ToString();
            // The viewer's newest seek made while the replan was in flight, when the new plan can start there.
            var resume = TakeReplanSeek(attempt.Plan, position);
            attempt.Position = _sourcePosition = PlaybackProgressive.MountSourceStart(attempt.Plan, resume);
            AdoptBufferingPlan(attempt, controllerInitiated: false, request.BandwidthEstimateKbps);
            _state = "loading";
            SetLoading(true);
            var timeline = PlaybackTimeline.From(attempt.Plan.Timeline);
            await MountPlanAsync(attempt, PlaybackProgressive.MountPlayerStart(attempt.Plan,
                timeline.PlayerForSource(resume) ?? timeline.PlayerStartSeconds), paused);
            await OpenedReplacementAsync(attempt);
            UpdateTrackValues();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A server end leaves the player (PlayerPage.ServerEnd.cs); any other failure keeps playback unchanged.
            if (_attempt is not { } current || !LeftForServerEnd(current, ex)) LogFailure("track change", ex);
        }
        finally
        {
            // A refused, failed or abandoned change never makes its pick the viewer's choice; a newer pick waits on.
            if (pick is not null && !adopted) _trackPicks.Refused(pick);
            _seeking = false;
            _remounting = false;
            _lifecycle.Release();
            // After the release: a seek the replacement did not take runs on whatever plays now.
            EndReplanSeeks();
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
        // TvPlayerViewModel.kt confirmDesiredAudio: the player is on the picked track, so now it is the viewer's choice.
        SaveTrackPicks(attempt);
        UpdateTrackValues();
#if DEBUG_SERVER
        RefreshStats(attempt);
#endif
        return true;
    }

    /// <summary>
    /// A subtitle pick whose replan answer keeps the playing stream (PlaybackReplan.KeepsTransport): adopt the
    /// answer and swap only Bale's overlay; the video keeps playing (InPlacePlanAdoption.kt / silo-apple's
    /// report replan). An embedded image track the FFmpeg sink must draw is bound when the mount opens, so such
    /// an answer still remounts.
    /// </summary>
    private bool TryAdoptSubtitleInPlace(Attempt attempt, Mount media, Bale.Core.Api.Models.PlaybackDecision decision,
        PlaybackReplanRequestV3 request, int? audioIndex)
    {
        if (audioIndex is not null || decision.PlaybackPlan is not { } answer) return false;
        if (PlaybackReplan.TransportChange(attempt.Plan!, answer) is { } change)
        {
            // A fixed field name, never a value.
            Log.Info("player: subtitle answer changes " + change + "; remounting");
            return false;
        }
        // The FFmpeg sink's token is fixed when the mount opens; a fresh bind would never receive.
        if (attempt.Client.Playback.ResolveEmbeddedPgs(answer) is null
            && attempt.Client.Playback.ResolveEmbeddedSubtitles(answer) is not null) return false;
        attempt.Plan = answer;
        attempt.ServerFeatures = decision.ServerFeatures ?? [];
        attempt.Start = PlaybackReplan.WithTracks(attempt.Start!, request.SelectedTracks);
        attempt.PlanAttemptId = Guid.NewGuid().ToString();
        // The overlay stays; only its track changes (ClearSubtitles also unbinds a demuxer image track).
        ClearSubtitles();
        StartSubtitles(attempt, media);
        // A server subtitle the answer plays ends closed captions; an Off a CC pick asked for draws them on the cleared overlay.
        AdoptCaptions(attempt, media);
        PushSubtitleClock(force: true);
        Log.Info("player: subtitle switched in place");
        SaveTrackPicks(attempt);
        UpdateTrackValues();
        return true;
    }

    /// <summary>
    /// A plan now plays (a mount opened, a subtitle answer was adopted in place, or the original switched audio locally):
    /// the waiting picks it plays become the series' or movie's track preference (PlayerTrackPreferences), and the Detail
    /// page underneath reads its choices again on return (TrackChoiceReturn). Never fails playback.
    /// </summary>
    private void SaveTrackPicks(Attempt attempt)
    {
        try
        {
            if (!_trackPicks.Waiting || attempt.Plan is not { } plan) return;
            foreach (var played in _trackPicks.Settle(attempt.Request.Detail, plan, attempt.Start?.FileId))
            {
                // Fixed words only, never a track's name or language.
                var kind = played.Kind == PlayerTrackKind.Audio ? "audio" : "subtitle";
                if (played.Write is not { } write)
                {
                    Log.Info($"player: {kind} pick plays; no series, movie or catalog track to remember it for");
                    continue;
                }
                TrackChoiceReturn.Record(write);
                _ = SaveTrackPreferenceAsync(attempt.Client.Detail, write, kind);
            }
        }
        catch (Exception ex) { LogFailure("track preference", ex); }
    }

    /// <summary>
    /// Not on the attempt's token: the write outlives the player (silo-roku 3ae62aa TRACK_PREFS_OWNER), so Back right after
    /// a pick does not cancel it. A failure costs the remembered track, never playback.
    /// </summary>
    private static async Task SaveTrackPreferenceAsync(DetailFeed feed, PlayerTrackPreferenceWrite write, string kind)
    {
        try
        {
            await feed.SaveTrackPreferenceAsync(write);
            Log.Info($"player: {kind} preference saved");
        }
        catch (Exception ex) { LogFailure(kind + " preference", ex); }
    }
}
