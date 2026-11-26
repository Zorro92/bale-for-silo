using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox;

/// <summary>
/// Up Next for episodes (TvPlayerViewModel.kt): the next episode is resolved in the background once
/// playback starts; crossing <c>playback.next_up_prompt_seconds</c> before the end of the file (or reaching
/// the end) raises the card, and its countdown plays the next episode in a fresh player that replaces this
/// one, so Back still returns to Detail. The credits marker only records the series-page return
/// (Android e580e1811).
/// </summary>
public sealed partial class PlayerPage
{
    private readonly PlaybackUpNextState _upNext = new();
    private EpisodeCard? _nextEpisode;
    private bool _nextLoadStarted;
    private bool _advancing;
    private double _lastUpNextPosition = double.NaN;

    private bool UpNextVisible => UpNextCard.Visibility == Visibility.Visible;

    /// <summary>Leaving for the series page: land on this episode, or on Up Next's once this one is finished.</summary>
    private void RecordSeriesReturn()
    {
        try
        {
            var detail = _attempt?.Request.Detail ?? _request?.Detail;
            SeriesReturn.Record(detail?.Type == "episode"
                ? SeriesPlaybackReturn.For(detail.SeriesId, detail.ContentId, detail.SeasonNumber, _upNext.Finished, _nextEpisode)
                : null);
        }
        catch (Exception ex) { LogFailure("series return", ex); }
    }

    private async Task LoadNextEpisodeAsync(Attempt attempt)
    {
        try
        {
            var detail = attempt.Request.Detail;
            if (detail.Type != "episode" || detail.SeriesId is not { Length: > 0 } seriesId
                || detail.SeasonNumber is not { } season || detail.EpisodeNumber is not { } episode) return;
            var feed = attempt.Client.Detail;
            var next = PlaybackUpNext.NextInSeason(await feed.EpisodesAsync(seriesId, season), episode);
            if (next is null && PlaybackUpNext.NextSeason(await feed.SeasonsAsync(seriesId), season) is { } following)
                next = PlaybackUpNext.First(await feed.EpisodesAsync(seriesId, following));
            if (!_active || _leaving || !ReferenceEquals(_attempt, attempt)) return;
            _nextEpisode = next;
            UpdateUpNextButton();
        }
        catch (Exception ex) { LogFailure("up next", ex); }
    }

    /// <summary>OnTick: start resolving the next episode, raise the card at the prompt point, run its countdown.</summary>
    private void UpdateUpNext()
    {
        try
        {
            if (!_active || _leaving || _advancing || _attempt is not { Media: { Started: true } } attempt) return;
#if DEBUG_SERVER
            if (attempt.DebugUrl is not null) return;
#endif
            if (!_nextLoadStarted)
            {
                _nextLoadStarted = true;
                ApplyAutoPlay(attempt.Client.Settings.AutoPlayNext.Value);
                _ = LoadNextEpisodeAsync(attempt);
                _ = RefreshAutoPlayAsync(attempt);
            }
            if (_nextEpisode is null) return;
            var position = SourcePosition(attempt);
            var fileId = attempt.Plan?.Source?.MediaFileId ?? attempt.Start?.FileId;
            // MarkerTimeline.kt finalCreditsStart: the closing credits, not a credits range before a mid-credits scene.
            var credits = PlayingMarkers(attempt, fileId).FinalCreditsStart();
            // Read every sample, as TvPlayerViewModel.kt reads nextUpPromptSeconds.value: the refresh below may land
            // (or another device may change it) while this episode plays.
            var promptSeconds = attempt.Client.Settings.NextUpPromptSeconds.Value;
            if (!double.IsNaN(_lastUpNextPosition)
                && _upNext.OnPosition(_lastUpNextPosition, position, _duration, credits, promptSeconds, _seeking || _skipProcessing))
                ShowUpNextCard();
            _lastUpNextPosition = position;
            if (_upNext.Tick(Environment.TickCount64, position, _duration))
            {
                _ = AdvanceToNextAsync();
                return;
            }
            UpdateUpNextCountdown();
        }
        catch (Exception ex) { LogFailure("up next tick", ex); }
    }

    /// <summary>MediaEnded with a next episode: advance, show the card, or leave as before.</summary>
    private bool HandleEndedWithUpNext()
    {
        if (_nextEpisode is null || _advancing) return false;
        switch (_upNext.OnEnded(Environment.TickCount64))
        {
            case UpNextEnd.Advance:
                _ = AdvanceToNextAsync();
                return true;
            case UpNextEnd.Show:
                ShowUpNextCard();
                return true;
            default:
                return false;
        }
    }

    private void ShowUpNextCard()
    {
        if (_nextEpisode is not { } next) return;
        Log.Info("player: up next shown");
        HideControls();
        UpNextTitle.Text = $"{EpisodeCode.Format(next.SeasonNumber, next.EpisodeNumber)} · {next.Title}";
        UpNextStill.Source = next.StillUrl is { } still ? new BitmapImage(still) : null;
        UpdateUpNextCountdown();
        UpNextCard.Visibility = Visibility.Visible;
        FocusRetry.Until(UpNextPlayButton, () => !UpNextVisible || UpNextPlayButton.Focus(FocusState.Keyboard));
    }

    /// <summary>
    /// playback.auto_play_next and playback.next_up_prompt_seconds as the server resolves them now (another device
    /// may have changed them); the prompt lead is read from the feed on each tick.
    /// </summary>
    private async Task RefreshAutoPlayAsync(Attempt attempt)
    {
        try
        {
            var setting = await attempt.Client.Settings.RefreshAsync();
            if (_active && !_leaving && ReferenceEquals(_attempt, attempt)) ApplyAutoPlay(setting.Value);
        }
        catch (Exception ex) { LogFailure("auto-play read", ex); }
    }

    /// <summary>Off: the card shows without a countdown and waits (TvPlayerViewModel.kt autoCountdown).</summary>
    private void ApplyAutoPlay(bool value)
    {
        var position = _attempt is { } attempt ? SourcePosition(attempt) : 0;
        _upNext.SetAutoPlay(value, Environment.TickCount64, position, _duration);
        if (UpNextVisible) UpdateUpNextCountdown();
        UpdateAutoPlayRow();
    }

    private void UpdateUpNextCountdown() =>
        UpNextCountdown.Text = _upNext.Countdown is { } seconds ? $"Starts in {seconds}" : string.Empty;

    /// <summary>Hide (or Back): no countdown and no automatic advance for this episode.</summary>
    private void DismissUpNext()
    {
        _upNext.Dismiss();
        UpNextCard.Visibility = Visibility.Collapsed;
        Focus(FocusState.Programmatic);
        // A card shown at the end has nothing left to play: leave, as without Up Next.
        if (_upNext.VideoEnded) OnBack(closeControls: false);
    }

    private void OnUpNextPlayClick(object sender, RoutedEventArgs e) => _ = AdvanceToNextAsync();

    private void OnUpNextHideClick(object sender, RoutedEventArgs e) => DismissUpNext();

    /// <summary>
    /// DetailPage.AutomaticAudio.cs's policy for a start no Detail page chose: settings language, then the active
    /// profile's language; the subtitle settings as the server resolves them (what Settings › Subtitles writes), else
    /// the profile's own fields. A failed read still starts, on the device/English fallback.
    /// </summary>
    private static async Task<PlaybackSelection> NextEpisodeSelectionAsync(Attempt attempt, ItemDetail detail)
    {
        string? language = null;
        SubtitleAutoContext? subtitles = null;
        if (attempt.Client.ProfileId is { } profileId)
        {
            var preferences = attempt.Client.SubtitleSettings.ReadPreferencesAsync();
            Bale.Core.Api.Models.Profile? profile = null;
            try { profile = (await attempt.Client.GetProfilesAsync()).FirstOrDefault(item => item.Id == profileId); }
            catch (Exception ex) { LogFailure("up next track policy", ex); }
            language = profile?.Language;
            subtitles = Bale.Core.Settings.SubtitlePreferences.AutoContextFor(await preferences, profile);
        }
        var selection = PlaybackLaunchTracks.ForAuto(detail,
            PlaybackAudioSelection.EffectivePreferredAudioLanguage(AppSettings.AudioLanguage, language),
            XboxAudioCapabilities.Current, subtitles);
        // Fixed words only, never a track's name or language.
        Log.Info($"player: up next subtitle={(selection.SubtitleIndex is { } index ? index < 0 ? "off" : "track" : "unresolved")}");
        return selection;
    }

    /// <summary>A fresh player for the next episode replaces this one (its Back entry goes with it).</summary>
    private async Task AdvanceToNextAsync()
    {
        if (_advancing || _nextEpisode is not { } next || _attempt is not { } attempt) return;
        _advancing = true;
        var frame = Frame;
        try
        {
            Log.Info("player: up next advance");
            // A track picked during this episode is being saved as the series' preference: let the write land first, so the
            // next episode's detail and start answer with it (tvOS settlePendingWrites; the server resolves an omitted audio
            // track from the saved preference).
            await attempt.Client.Detail.WhenTrackChoicesSavedAsync(DetailFeed.TrackChoiceSettleTimeout);
            var detail = await attempt.Client.Detail.GetAsync(next.ContentId);
            // The next episode starts on the subtitle its Detail page would show for Auto (the series' saved choice,
            // then the profile's settings): a start without a subtitle index means Off.
            var selection = await NextEpisodeSelectionAsync(attempt, detail);
            _leaving = true;
            UpNextCard.Visibility = Visibility.Collapsed;
            await ShutdownAsync();
            if (!ReferenceEquals(frame?.Content, this)) return;
            if (frame.Navigate(typeof(PlayerPage), new PlayerRequest(detail, selection)) && frame.BackStack.Count > 0)
                frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
        }
        catch (Exception ex)
        {
            LogFailure("up next advance", ex);
            if (ReferenceEquals(frame?.Content, this) && frame.CanGoBack) frame.GoBack();
        }
    }
}
