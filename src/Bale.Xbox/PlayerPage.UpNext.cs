using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox;

/// <summary>
/// Up Next for episodes (TvPlayerViewModel.kt): the next episode is resolved in the background once
/// playback starts; crossing the credits (or reaching the end) raises the card, and its countdown
/// plays the next episode in a fresh player that replaces this one, so Back still returns to Detail.
/// </summary>
public sealed partial class PlayerPage
{
    private readonly PlaybackUpNextState _upNext = new();
    private EpisodeCard? _nextEpisode;
    private bool _nextLoadStarted;
    private bool _advancing;
    private double _lastUpNextPosition = double.NaN;

    private bool UpNextVisible => UpNextCard.Visibility == Visibility.Visible;

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

    /// <summary>OnTick: start resolving the next episode, raise the card at the credits, run its countdown.</summary>
    private void UpdateUpNext()
    {
        try
        {
            if (!_active || _leaving || _advancing || _attempt is not { Media: { Started: true } } attempt) return;
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
            var credits = attempt.Request.Detail.Versions.FirstOrDefault(version => version.FileId == fileId)?.Credits?.Start;
            if (!double.IsNaN(_lastUpNextPosition)
                && _upNext.OnPosition(_lastUpNextPosition, position, _duration, credits, _seeking || _skipProcessing))
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

    /// <summary>playback.auto_play_next as the server resolves it now (another device may have changed it).</summary>
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

    /// <summary>A fresh player for the next episode replaces this one (its Back entry goes with it).</summary>
    private async Task AdvanceToNextAsync()
    {
        if (_advancing || _nextEpisode is not { } next || _attempt is not { } attempt) return;
        _advancing = true;
        var frame = Frame;
        try
        {
            Log.Info("player: up next advance");
            var detail = await attempt.Client.Detail.GetAsync(next.ContentId);
            _leaving = true;
            UpNextCard.Visibility = Visibility.Collapsed;
            await ShutdownAsync();
            if (!ReferenceEquals(frame?.Content, this)) return;
            if (frame.Navigate(typeof(PlayerPage), new PlayerRequest(detail, PlaybackSelection.Auto)) && frame.BackStack.Count > 0)
                frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
        }
        catch (Exception ex)
        {
            LogFailure("up next advance", ex);
            if (ReferenceEquals(frame?.Content, this) && frame.CanGoBack) frame.GoBack();
        }
    }
}
