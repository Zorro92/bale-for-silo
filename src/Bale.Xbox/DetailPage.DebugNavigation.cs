#if DEBUG_SERVER
using Bale.Core.Detail;
using Bale.Xbox.Services;
using Microsoft.Kiota.Abstractions;

namespace Bale.Xbox;

public sealed partial class DetailPage
{
    private string _debugLoadState = "loading";
    private bool _debugExactStart;

    internal static string DebugHistoryParameters(object? parameter) => parameter is DetailReturnEntry saved
        ? $"content={DebugNavigationHistory.Identity(saved.Detail.ContentId)} episode={DebugNavigationHistory.Identity(saved.PlayEpisodeId ?? "none")}" : string.Empty;

    internal string DebugNavigationState => _debugLoadState != "loaded" ? _debugLoadState
        : _seasonsLoading || _episodesLoading || _nextUpPlaybackLoading || _audioSelectionCapabilities is null ? "loading" : "loaded";

    internal async Task<(string Status, string Body)> DebugSelectPlaybackAsync(DebugNavigationRequest request, CancellationToken cancellation)
    {
        if (!_active || DebugNavigationState != "loaded" || _detail is not { } detail)
            return ("409 Conflict", "Detail is not ready\n");
        if (!request.MatchesTarget(detail.ContentId, PlayTargetId))
            return ("409 Conflict", "requested content is not the Detail Play target\n");
        if (request.FileId is not null)
        {
            if (Session.Client is not { } client || PlayTargetId is not { } target)
                return ("409 Conflict", "Detail Play target is not ready\n");
            var profile = client.ProfileId;
            var generation = _playbackLoadVersion;
            var revision = _trackChoiceRevision;
            bool Current() => _active && DebugNavigationState == "loaded" && _detail?.ContentId == detail.ContentId
                && PlayTargetId == target && generation == _playbackLoadVersion && revision == _trackChoiceRevision
                && ReferenceEquals(client, Session.Client) && profile == client.ProfileId;

            ItemDetail refreshed;
            try
            {
                // GetAsync reuses a five-minute preloaded inventory. The CLI's detail uses LoadAsync;
                // an exact debug pick must see that same fresh inventory, before declaring a file unknown.
                // Display matching only chooses Auto; HDR claims are evaluated by the normal player.
                refreshed = await client.Detail.LoadAsync(target, cancellation);
            }
            catch (ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                cancellation.ThrowIfCancellationRequested();
                return Current()
                    ? ("404 Not Found", "Detail Play target no longer exists on the server\n")
                    : ("409 Conflict", "Detail target, playback choices or profile changed during file refresh\n");
            }
            cancellation.ThrowIfCancellationRequested();
            if (!Current()) return ("409 Conflict", "Detail target, playback choices or profile changed during file refresh\n");
            if (refreshed.ContentId != target || refreshed.PlaysEpisodes)
                return ("409 Conflict", "server detail does not describe the Detail Play target\n");
            if (!request.SelectFile(refreshed, _playbackSelections))
                return ("404 Not Found", "unknown file id for the Detail Play target (checked fresh server detail)\n");

            // Keep summary, selectors and the normal Play handoff on the same inventory, or For()
            // would discard the explicit file against the old snapshot. Preserve concurrent flag edits.
            if (detail.PlaysEpisodes) _nextUpPlaybackDetail = refreshed;
            else _detail = _detail! with
            {
                Versions = refreshed.Versions,
                PlaybackVariants = refreshed.PlaybackVariants,
                LastFileId = refreshed.LastFileId,
                PlaybackPositionSeconds = refreshed.PlaybackPositionSeconds,
                EffectiveSubtitleLanguage = refreshed.EffectiveSubtitleLanguage,
                EffectiveSubtitleMode = refreshed.EffectiveSubtitleMode,
                EffectiveShowForcedSubtitles = refreshed.EffectiveShowForcedSubtitles,
                EffectiveSubtitleTrackSignature = refreshed.EffectiveSubtitleTrackSignature,
            };
        }
        ShowPlaybackSummary();
        // Series redirects may focus an episode card; callers can now press Play deterministically.
        if (!FocusAction(PlayButton)) return ("409 Conflict", "could not focus Detail Play\n");
        return ("200 OK", "");
    }

    internal async Task DebugOpenPlayerAsync(DebugNavigationRequest request)
    {
        if (PlayTargetId is not { } target || PlaybackDetail is null) return;
        _debugExactStart = request.StartSeconds is not null;
        try { await OpenPlayerAsync(target, request.StartSeconds, fromEpisode: false); }
        finally { _debugExactStart = false; }
    }

    internal string DebugSelection => $"Selection: content={PlayTargetId ?? "none"} file={_playbackSelections.For(PlaybackDetail).FileId ?? "auto"}\n";
}
#endif
