namespace Bale.Core.Playback;

public sealed partial class PlaybackClient
{
    /// <summary>
    /// The playing file's effective markers (<c>GET /api/v2/markers/files/{file_id}</c>, getFileMarkers): every occurrence
    /// from <c>marker_segments</c>. The catalog detail Bale starts from carries only the first occurrence of each kind, and
    /// the server fills on-demand online markers only for a read that names the file, as the TV apps' watch read does
    /// (TvPlayerViewModel.kt reconcileMarkers; silo-server playback_lazy_markers.go, watch_state_service.go).
    /// </summary>
    public async Task<MarkerTimeline> FileMarkersAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        var scope = _captureScope();
        var markers = await _api.Api.V2.Markers.Files[fileId].GetAsync(config => config.Options.Add(scope.Request), cancellationToken)
            .ConfigureAwait(false);
        scope.Request.CheckCurrent();
        return markers is null ? throw new InvalidDataException("The server returned no markers.") : MarkerTimeline.From(markers);
    }
}
