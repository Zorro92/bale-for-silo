#if DEBUG_SERVER
using Bale.Xbox.Services;

namespace Bale.Xbox;

public sealed partial class DetailPage
{
    private string _debugLoadState = "loading";
    private bool _debugExactStart;

    internal static string DebugHistoryParameters(object? parameter) => parameter is DetailReturnEntry saved
        ? $"content={DebugNavigationHistory.Identity(saved.Detail.ContentId)} episode={DebugNavigationHistory.Identity(saved.PlayEpisodeId ?? "none")}" : string.Empty;

    internal string DebugNavigationState => _debugLoadState != "loaded" ? _debugLoadState
        : _seasonsLoading || _episodesLoading || _nextUpPlaybackLoading || _audioSelectionCapabilities is null ? "loading" : "loaded";

    internal (string Status, string Body) DebugSelectPlayback(DebugNavigationRequest request)
    {
        if (!_active || DebugNavigationState != "loaded" || _detail is not { } detail)
            return ("409 Conflict", "Detail is not ready\n");
        if (!request.MatchesTarget(detail.ContentId, PlayTargetId))
            return ("409 Conflict", "requested content is not the Detail Play target\n");
        if (request.FileId is not null && (PlaybackDetail is not { } playable || !request.SelectFile(playable, _playbackSelections)))
            return ("404 Not Found", "unknown file id for the Detail Play target\n");
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
