using Bale.Core.Detail;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.System;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox;

/// <summary>
/// The Trailers &amp; More cards (TvDetailTrailersSection.kt:116-117, TvItemDetailScreen.kt:1448-1466; the decision is
/// Core's <see cref="TrailerLaunch"/>). A local extra plays in Bale's player as its own playback, from its start, and Back
/// lands on its card. A YouTube trailer goes to the app that opens youtube.com links, with Android's toast when none
/// does. A trailer's link is never logged.
/// </summary>
public sealed partial class DetailPage
{
    private const string PlaybackDetailsFailed = "Couldn't load playback details. Try again.";

    private string? _returnTrailerKey;
    private bool _openingTrailer;

    private async void OnTrailerClick(object sender, ItemClickEventArgs e)
    {
        try
        {
            if (e.ClickedItem is not TrailerView { Card: var card }) return;
            switch (TrailerLaunch.For(card))
            {
                case ExtraPlayback extra:
                    await OpenExtraAsync(card, extra);
                    break;
                case TrailerLink link:
                    await OpenTrailerLinkAsync(link.Link);
                    break;
                default:
                    // Only an extra whose content id isn't a server-relative one gets here; tell the viewer as a failed load.
                    Log.Info("Detail trailer: the card names nothing to play");
                    if (_active) ExitNotice.ShowShort(PlaybackDetailsFailed);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Detail trailer type={ex.GetType().Name} hr=0x{unchecked((uint)ex.HResult):X8}");
        }
    }

    /// <summary>
    /// onSelectLocal: onPlay(extra.contentId, extra.fileId, versionChosen = false, …, "extra", 0.0). The player takes the
    /// extra's own catalog detail (its versions and tracks), never the title's, so it loads first, as Play's does. An
    /// extra is no episode: it gets no Up Next, no series-page return and no saved track preference, and its progress is
    /// the extra's own on the server, never the title's (PlayerPage.UpNext.cs, PlayerTrackPreferences).
    /// </summary>
    private async Task OpenExtraAsync(TrailerCard card, ExtraPlayback extra)
    {
        if (_openingPlayback || !_active || _detail is not { } owner || Session.Client is not { } client) return;
        _openingPlayback = true;
        var profile = client.ProfileId;
        try
        {
            var playable = await client.Detail.GetAsync(extra.ContentId);
            if (!_active || _detail?.ContentId != owner.ContentId || !ReferenceEquals(client, Session.Client)
                || profile != client.ProfileId) return;
            if (TrailerLaunch.Selection(playable, extra) is not { } selection)
            {
                Log.Info("Detail extra handoff: no playable leaf");
                ExitNotice.ShowShort(PlaybackDetailsFailed);
                return;
            }

            _returnCastIndex = null;
            _returnSimilarId = null;
            _returnCollectionsKey = null;
            _returnPlaybackFocus = null;
            // Back from the player lands on this card, as on the Cast, More Like This and collection rails
            // (RestoreSectionFocus) and as tvOS's focus engine returns it (TVItemDetailView.swift playTrailer). Android TV's
            // default focus takes Play instead (TvItemDetailScreen.kt:600-643); docs/parity-backlog.md has the open question.
            _returnTrailerKey = card.Key;
            NavigateWithReturn(typeof(PlayerPage),
                new PlayerRequest(playable, LaunchSelection(playable, selection, pendingAudio: null), TrailerLaunch.StartPosition));
        }
        catch (Exception ex)
        {
            Log.Info($"Detail extra handoff type={ex.GetType().Name} hr=0x{unchecked((uint)ex.HResult):X8}");
            if (_active) ExitNotice.ShowShort(PlaybackDetailsFailed);
        }
        finally { _openingPlayback = false; }
    }

    /// <summary>
    /// YoutubeTrailerLauncher.kt: the public watch page goes to the app that opens it (on Xbox the YouTube app takes
    /// youtube.com links; without it Windows offers the browser, as Android falls back to one), and the short toast
    /// shell_trailer_noApp says so when nothing does. Bale stays on this page with focus on the card.
    /// </summary>
    private async Task OpenTrailerLinkAsync(Uri link)
    {
        if (_openingTrailer || !_active) return;
        _openingTrailer = true;
        try
        {
            if (TrailerLaunch.IsAllowedLink(link) && await Launcher.LaunchUriAsync(link)) return;
            Log.Info("Detail trailer: no app opened the trailer");
            if (_active) ExitNotice.ShowShort(TrailerLaunch.NoAppMessage);
        }
        catch (Exception ex)
        {
            Log.Info($"Detail trailer launch type={ex.GetType().Name} hr=0x{unchecked((uint)ex.HResult):X8}");
            if (_active) ExitNotice.ShowShort(TrailerLaunch.NoAppMessage);
        }
        finally { _openingTrailer = false; }
    }
}
