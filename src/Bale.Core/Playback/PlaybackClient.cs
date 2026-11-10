using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bale.Core.Api;
using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace Bale.Core.Playback;

/// <summary>
/// Stable playback installation identity, persisted by the platform. It must match installation_id
/// from /playback/capabilities, not a newly generated device ID (network/apiv2/PlaybackV2Api.kt).
/// </summary>
public interface IPlaybackInstallationId
{
    string InstallationId { get; }
}

internal sealed record PlaybackRequestScope(string? AccountId, string ProfileId, ProfileRequestOption Request);

/// <summary>
/// Solo v2 video exchanges, following shared/src/commonMain/kotlin/org/siloserver/silo/network/apiv2/PlaybackV2Api.kt.
/// Uses the same authenticated adapter, profile proofs and bearer refresh path as the other Core feeds.
/// </summary>
public sealed class PlaybackClient
{
    private readonly SiloApiClient _api;
    private readonly IRequestAdapter _adapter;
    private readonly HttpClient _http;
    private readonly Func<PlaybackRequestScope> _captureScope;
    internal Uri Server { get; }

    internal PlaybackClient(SiloApiClient api, IRequestAdapter adapter, HttpClient http, Uri server, Func<PlaybackRequestScope> captureScope)
    {
        _api = api;
        _adapter = adapter;
        _http = http;
        Server = server;
        _captureScope = captureScope;
    }

    /// <summary>Capture this plan's credential owner once; later reads must never adopt another viewer.</summary>
    public PlaybackMedia OpenMedia(PlaybackPlan plan)
    {
        var scope = _captureScope();
        var uri = PlaybackMediaUrl.Resolve(plan.Stream?.Url, Server)
            ?? throw new InvalidDataException("The playback plan has no playable stream.");
        return new(_http, Server, scope, plan, uri);
    }

    /// <summary>The sidecar Bale's overlay should draw for this plan, or null (off, burned in, unusable).</summary>
    public PlaybackSubtitleSource? ResolveSubtitles(PlaybackPlan plan) => PlaybackSubtitleResolver.Resolve(plan, Server);

    /// <summary>The in-container image track Bale's overlay should draw for this plan, or null.</summary>
    public PlaybackEmbeddedSubtitle? ResolveEmbeddedSubtitles(PlaybackPlan plan) => PlaybackSubtitleResolver.ResolveEmbedded(plan);

    public Task<PlaybackCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        GetCapabilitiesAsync(_captureScope(), cancellationToken);

    internal async Task<PlaybackCapabilities> GetCapabilitiesAsync(PlaybackRequestScope scope, CancellationToken cancellationToken)
    {
        var response = await _api.Api.V2.Playback.Capabilities.GetAsync(config => config.Options.Add(scope.Request), cancellationToken)
            .ConfigureAwait(false);
        scope.Request.CheckCurrent();
        return response ?? throw new InvalidDataException("The server returned no playback capabilities.");
    }

    public async Task<PlaybackDecision> StartAsync(PlaybackStartRequestV3 request, IPlaybackInstallationId installation,
        CancellationToken cancellationToken = default)
    {
        var scope = _captureScope();
        if (request.ProfileId != scope.ProfileId) throw new InvalidOperationException("The playback profile changed.");
        var installationId = InstallationId(installation);
        var capabilities = await GetCapabilitiesAsync(scope, cancellationToken).ConfigureAwait(false);
        // repository/SequencedPlayback.kt admits only v3 + sequenced progress; never fall back to v1.
        if (capabilities.Allowed != true || capabilities.State != PlaybackCapabilities_state.Available
            || capabilities.ProtocolVersions?.Contains(PlaybackProtocol.Version) != true
            || capabilities.Features?.Contains(PlaybackProtocol.SequencedProgressFeature) != true)
            throw new InvalidOperationException("Sequenced video playback is unavailable for this profile.");
        if (capabilities.InstallationId != installationId)
            throw new InvalidOperationException("The playback installation does not match the server capabilities.");

        return await PostAsync("/api/v2/playback/start", request with
            {
                InstallationId = installationId,
                ClientFeatures = PlaybackProtocol.NegotiatedStartFeatures(request, capabilities.Features),
            },
            PlaybackJson.Default.PlaybackStartRequestV3, scope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// loginId must be persisted with the saved login, and replaced on a new sign-in. Load Account first
    /// after restoring tokens, as repository/SequencedPlayback.kt does before admitting playback.
    /// </summary>
    public PlaybackProgressReporter CreateProgressReporter(IPlaybackInstallationId installation, IPlaybackJournal journal,
        string loginId, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginId);
        var scope = _captureScope();
        if (string.IsNullOrWhiteSpace(scope.AccountId))
            throw new InvalidOperationException("Load the signed-in account before creating a playback reporter.");
        var owner = new PlaybackJournalOwner(Server.AbsoluteUri, loginId, scope.AccountId, scope.ProfileId, InstallationId(installation));
        return new(this, journal, owner, scope, time ?? TimeProvider.System);
    }

    public Task<PlaybackDecision> ReplanAsync(string sessionId, PlaybackReplanRequestV3 request, IPlaybackInstallationId installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return PostAsync($"/api/v2/playback/{Uri.EscapeDataString(sessionId)}/replan",
            request with { InstallationId = InstallationId(installation) }, PlaybackJson.Default.PlaybackReplanRequestV3,
            _captureScope(), cancellationToken);
    }

    private async Task<PlaybackDecision> PostAsync<T>(string path, T body, JsonTypeInfo<T> type,
        PlaybackRequestScope scope, CancellationToken cancellationToken)
    {
        // Kiota loses DeliveryCapabilityV3 inside the deliveries dictionary (AdditionalData only).
        // Keep the fallback body typed and AOT-safe, but retain the existing authenticated HttpClient.
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body, type));
        var request = new RequestInformation(Method.POST, "{+baseurl}" + path,
            new Dictionary<string, object> { ["baseurl"] = _adapter.BaseUrl! });
        request.Headers.TryAdd("Accept", "application/json");
        request.SetStreamContent(stream, "application/json");
        request.AddRequestOptions(new IRequestOption[] { scope.Request, new RetryHandlerOption { MaxRetry = 0 }, new SingleAttemptRequestOption() });
        var response = await _adapter.SendAsync(request, PlaybackDecision.CreateFromDiscriminatorValue,
            new Dictionary<string, ParsableFactory<IParsable>>
            {
                ["4XX"] = Problem.CreateFromDiscriminatorValue,
                ["5XX"] = Problem.CreateFromDiscriminatorValue,
            }, cancellationToken).ConfigureAwait(false);
        scope.Request.CheckCurrent();
        return response ?? throw new InvalidDataException("The server returned no playback decision.");
    }

    internal async Task<PlaybackMutation> ProgressAsync(string sessionId, PlaybackProgressV2 progress, PlaybackRequestScope scope,
        CancellationToken cancellationToken)
    {
        var response = await _api.Api.V2.Playback[sessionId].Progress.PostAsync(new PlaybackProgressBody
        {
            InstallationId = progress.InstallationId, Sequence = progress.Sequence,
            Position = progress.Position, IsPaused = progress.IsPaused,
        }, config =>
        {
            config.Options.Add(scope.Request);
            config.Options.Add(new RetryHandlerOption { MaxRetry = 0 });
        }, cancellationToken).ConfigureAwait(false);
        scope.Request.CheckCurrent();
        return response ?? throw new InvalidDataException("The server returned no playback progress receipt.");
    }

    internal async Task<PlaybackMutation> StopAsync(string sessionId, PlaybackStopV2 stop, PlaybackRequestScope scope,
        CancellationToken cancellationToken)
    {
        var response = await _api.Api.V2.Playback[sessionId].DeleteAsync(new PlaybackStopBody
        {
            InstallationId = stop.InstallationId, StopId = stop.StopId, Sequence = stop.Sequence,
            Position = stop.Position, IsPaused = stop.IsPaused,
        }, config =>
        {
            config.Options.Add(scope.Request);
            config.Options.Add(new RetryHandlerOption { MaxRetry = 0 });
            config.Options.Add(new SingleAttemptRequestOption());
        }, cancellationToken).ConfigureAwait(false);
        scope.Request.CheckCurrent();
        return response ?? throw new InvalidDataException("The server returned no playback stop receipt.");
    }

    internal static string InstallationId(IPlaybackInstallationId installation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installation.InstallationId);
        return installation.InstallationId;
    }
}
