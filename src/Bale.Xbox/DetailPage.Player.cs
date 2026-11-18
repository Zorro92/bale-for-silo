using Bale.Core.Detail;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

public sealed partial class DetailPage
{
    private bool _openingPlayback;
    private PlaybackReturnFocus? _returnPlaybackFocus;

    private sealed record PlaybackReturnFocus(string ContentId, string? EpisodeId, bool StartOver);

    private async Task OpenPlayerAsync(string targetId, double? overridePosition, bool fromEpisode)
    {
        if (_openingPlayback || !_active || _detail is not { } owner || Session.Client is not { } client) return;
        _openingPlayback = true;
        var profile = client.ProfileId;
        try
        {
            ActionStatus.Text = string.Empty;
            // TvVideoPlaybackStarter.kt needs the episode's versions/user-data, never the container's.
            // Reuse the exact selector inventory when loaded, otherwise join its cached detail load.
            var playable = PlaybackDetail is { } current && current.ContentId == targetId
                ? current : await client.Detail.GetAsync(targetId);
            if (!_active || _detail?.ContentId != owner.ContentId || !ReferenceEquals(client, Session.Client)
                || profile != client.ProfileId || (!fromEpisode && PlayTargetId != targetId)) return;
            if (playable.PlaysEpisodes) return;
            var selection = _playbackSelections.For(playable);
            // TvItemDetailScreen.kt: start on exactly what the Subtitles value shows, Auto preview included.
            // An absent V3 subtitle index means Off, so leaving Auto unresolved started without subtitles.
            var version = PlaybackFormatting.SelectDisplayVersion(playable, selection);
            var automaticAudio = PlaybackAudioSelection.AutomaticAudioOrdinal(version, _preferredAudioLanguage,
                _audioSelectionCapabilities, _playbackSelections.PendingAudioChoiceFor(playable));
            selection = selection with
            {
                SubtitleIndex = PlaybackSummaryValues.SubtitleLaunchIndex(playable, selection.FileId, selection.AudioIndex,
                    selection.SubtitleIndex, automaticAudioResolutionKnown: _audioSelectionCapabilities is not null,
                    automaticAudioTrackOrdinal: automaticAudio, subtitleAutoContext: _subtitleAutoContext,
                    selectedEditionId: selection.EditionId),
            };
            _returnCastIndex = null;
            _returnSimilarId = null;
            _returnPlaybackFocus = new(targetId, fromEpisode ? targetId : null, overridePosition == 0.0);
            // ShellPage.OpenDetail already pushed Detail into the root PageFrame. Its own Frame
            // therefore covers all shell chrome and keeps Back on this exact Detail stack entry.
#if DEBUG_SERVER
            var request = new PlayerRequest(playable, selection, overridePosition);
            // Explicit debug start= is a source-time command, like seek, with no resume rewind.
            if (_debugExactStart) request = request with { SuppressResumeRewind = true };
            NavigateWithReturn(typeof(PlayerPage), request);
#else
            NavigateWithReturn(typeof(PlayerPage), new PlayerRequest(playable, selection, overridePosition));
#endif
        }
        catch (Exception ex)
        {
            Log.Info($"Detail player handoff type={ex.GetType().Name} hr=0x{unchecked((uint)ex.HResult):X8}");
            if (_active) ActionStatus.Text = "Couldn't load playback details. Try again.";
        }
        finally { _openingPlayback = false; }
    }

    private async Task RefreshAfterPlaybackAsync(PlaybackReturnFocus playback)
    {
        if (_detail is not { } owner || Session.Client is not { } client) return;
        var profile = client.ProfileId;
        var season = _shownSeason;
        var episodeVersion = _episodeLoadVersion;
        bool Current() => _active && _detail?.ContentId == owner.ContentId && ReferenceEquals(client, Session.Client)
            && client.ProfileId == profile;
        // Android e727c9af5 / tvOS SeriesPlaybackReturn: land on the episode the player last showed, or the next one
        // once that one reached its credits or end. Taken before the first await so return focus already uses it.
        var seriesId = owner.IsSeries ? owner.ContentId : owner.SeriesId;
        var landing = SeriesReturn.Take(seriesId);
        if (!owner.PlaysEpisodes || landing?.EpisodeId == _playEpisode?.ContentId) landing = null;
        if (landing is not null && playback.EpisodeId is not null
            && Episodes.Items.OfType<EpisodeView>().Any(view => view.Episode.ContentId == landing.EpisodeId))
        {
            _returnPlaybackFocus = playback with { EpisodeId = landing.EpisodeId };
        }
        try
        {
            client.Detail.Forget(playback.ContentId);
            client.Detail.Forget(owner.ContentId);
            var refreshed = await client.Detail.GetAsync(owner.ContentId);
            if (!Current() || _busy) return;
            // Keep mounted selectors/rails and newer page picks. Only progress/flags and the Auto
            // last-file hint changed while the player owned this stack entry (TvItemDetailViewModel.kt).
            _detail = _detail! with
            {
                ResumeSeconds = refreshed.ResumeSeconds,
                PlaybackPositionSeconds = refreshed.PlaybackPositionSeconds,
                LastFileId = refreshed.LastFileId,
                LastEditionKey = refreshed.LastEditionKey,
                Flags = refreshed.Flags,
            };
            ShowFlags(_detail.Flags);
            _nextUpPlaybackTargetId = null;
            ShowPlayback();
            if (ReferenceEquals(FocusManager.GetFocusedElement(), StartOverButton)
                && StartOverButton.Visibility != Visibility.Visible) FocusAction(PlayButton);

            if (landing is not null && _tabs.FirstOrDefault(tab => tab.Season?.Number == landing.SeasonNumber) is { } landingTab
                && _episodeLoadVersion == episodeVersion)
            {
                // Selects the episode in its season (loading that season when needed) and re-reads its progress.
                await SelectTabAsync(landingTab, preferredEpisodeId: landing.EpisodeId,
                    focusEntry: playback.EpisodeId is not null, reload: true);
                return;
            }

            if (!owner.PlaysEpisodes || seriesId is null || season is null) return;
            var episodes = await client.Detail.EpisodesAsync(seriesId, season.Value);
            if (!Current() || _busy || _shownSeason != season || _episodeLoadVersion != episodeVersion) return;
            var byId = episodes.ToDictionary(episode => episode.ContentId);
            foreach (var view in Episodes.Items.OfType<EpisodeView>())
            {
                if (byId.TryGetValue(view.Episode.ContentId, out var episode)) view.Update(episode);
            }
            if (Episodes.Items.OfType<EpisodeView>().FirstOrDefault(view => view.Episode.ContentId == _playEpisode?.ContentId) is { } active)
                SetPlayEpisode(active.Episode);
        }
        catch (Exception ex)
        {
            // Returning/focus must not fail just because the progress read is temporarily offline.
            Log.Info($"Detail playback refresh type={ex.GetType().Name} hr=0x{unchecked((uint)ex.HResult):X8}");
        }
    }
}
