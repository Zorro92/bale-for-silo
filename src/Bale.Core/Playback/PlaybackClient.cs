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
public sealed partial class PlaybackClient
{
    private readonly SiloApiClient _api;
    private readonly IRequestAdapter _adapter;
    private readonly HttpClient _http;
    private readonly Func<PlaybackRequestScope> _captureScope;
    private readonly PlaybackMedia.HeaderCapture? _captureHeaders;
    /// <summary>The last start's server-negotiated features (one player plays at a time).</summary>
    private volatile NegotiatedStartFeatures? _negotiated;
    internal Uri Server { get; }

    /// <summary>The profile's buffering-quality mode for this device; SiloClient wires it to its settings feed.</summary>
    internal Func<Settings.BufferingQualityMode> BufferingQualityMode { get; set; } = () => Settings.BufferingQualityModes.Default;

    internal PlaybackClient(SiloApiClient api, IRequestAdapter adapter, HttpClient http, Uri server, Func<PlaybackRequestScope> captureScope,
        PlaybackMedia.HeaderCapture? captureHeaders = null)
    {
        _api = api;
        _adapter = adapter;
        _http = http;
        Server = server;
        _captureScope = captureScope;
        _captureHeaders = captureHeaders;
    }

    /// <summary>Capture this plan's credential owner once; later reads must never adopt another viewer.</summary>
    public PlaybackMedia OpenMedia(PlaybackPlan plan)
    {
        var scope = _captureScope();
        var uri = PlaybackMediaUrl.Resolve(plan.Stream?.Url, Server)
            ?? throw new InvalidDataException("The playback plan has no playable stream.");
        return new(_http, Server, scope, plan, uri, _captureHeaders);
    }

    /// <summary>The sidecar Bale's overlay should draw for this plan, or null (off, burned in, unusable).</summary>
    public PlaybackSubtitleSource? ResolveSubtitles(PlaybackPlan plan) => PlaybackSubtitleResolver.Resolve(plan, Server);

    public PlaybackSubtitleSource? ResolveEmbeddedPgs(PlaybackPlan plan) => PlaybackSubtitleResolver.ResolveEmbeddedPgs(plan, Server);

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

        // Speed test (connection_speed_v1): a start on an unmetered link declares buffering_quality_v1 and
        // sends the highest current reading as a probe estimate. Without one the server fills in its reading.
        var features = PlaybackProtocol.NegotiatedStartFeatures(request, capabilities.Features);
        int? estimate = null;
        if (Speed.EstimatesEnabled && ConnectionSpeed.Offered(capabilities.Features) && !Speed.Metered)
        {
            if (!features.Contains(ConnectionSpeed.BufferingQualityFeature)) features = features.Append(ConnectionSpeed.BufferingQualityFeature).ToArray();
            Speed.Update(Schedule(capabilities), null);
            estimate = Speed.StartEstimate(DateTimeOffset.UtcNow);
        }
        // Replans of this attempt repeat what the start negotiated (ApplePlaybackV3PlanAdapter/PlaybackSessionBridge).
        _negotiated = new(request.PlaybackAttemptId, features.Except(request.ClientFeatures, StringComparer.Ordinal).ToArray())
        {
            BandwidthEstimateKbps = estimate,
        };
        return await PostAsync("/api/v2/playback/start", request with
            {
                InstallationId = installationId,
                ClientFeatures = features,
                BandwidthEstimateKbps = estimate,
                BandwidthEstimateSource = estimate is null ? null : ConnectionSpeed.ProbeSource,
                LearnedLimit = PlaybackProtocol.LearnedLimit(features, BufferingQualityMode(), request.LearnedLimit),
                ClientPlaybackContext = request.ClientPlaybackContext.WithoutInactiveClientTransformations(),
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

    /// <summary>The attempt's start negotiated <paramref name="feature"/> with the server (buffering_quality_v1, subrip_sidecar_v1).</summary>
    public bool Negotiated(string playbackAttemptId, string feature) =>
        _negotiated is { } negotiated && negotiated.PlaybackAttemptId == playbackAttemptId && negotiated.Added.Contains(feature);

    /// <summary>The speed-test estimate the attempt's start sent, or null.</summary>
    public int? StartEstimateKbps(string playbackAttemptId) =>
        _negotiated is { } negotiated && negotiated.PlaybackAttemptId == playbackAttemptId ? negotiated.BandwidthEstimateKbps : null;

    public Task<PlaybackDecision> ReplanAsync(string sessionId, PlaybackReplanRequestV3 request, IPlaybackInstallationId installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var features = PlaybackProtocol.ReplanFeatures(request, _negotiated);
        return PostAsync($"/api/v2/playback/{Uri.EscapeDataString(sessionId)}/replan",
            PlaybackProtocol.BufferingQualityFields(request, features) with
            {
                InstallationId = InstallationId(installation),
                ClientFeatures = features,
                LearnedLimit = PlaybackProtocol.LearnedLimit(features, BufferingQualityMode(), request.LearnedLimit),
                ClientPlaybackContext = request.ClientPlaybackContext.WithoutInactiveClientTransformations(),
            }, PlaybackJson.Default.PlaybackReplanRequestV3,
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
        var response = await _adapter.SendAsync(request, PlaybackDecision.CreateFromDiscriminatorValue, ProblemErrors,
            cancellationToken).ConfigureAwait(false);
        scope.Request.CheckCurrent();
        return response ?? throw new InvalidDataException("The server returned no playback decision.");
    }

    /// <summary>Every error answer as a v2 problem, so its type stays readable.</summary>
    private static readonly Dictionary<string, ParsableFactory<IParsable>> ProblemErrors = new()
    {
        ["4XX"] = Problem.CreateFromDiscriminatorValue,
        ["5XX"] = Problem.CreateFromDiscriminatorValue,
    };

    internal async Task<PlaybackMutation> ProgressAsync(string sessionId, PlaybackProgressV2 progress, PlaybackRequestScope scope,
        CancellationToken cancellationToken)
    {
        // The generated builder maps only the contract's listed codes, and the contract lists no 410 for progress, so a
        // server end (PlaybackServerEnds) would arrive without its problem type. Send its own request with every error
        // read as a problem instead, as start and replan do.
        var request = _api.Api.V2.Playback[sessionId].Progress.ToPostRequestInformation(new PlaybackProgressBody
        {
            InstallationId = progress.InstallationId, Sequence = progress.Sequence,
            Position = progress.Position, IsPaused = progress.IsPaused,
        }, config =>
        {
            config.Options.Add(scope.Request);
            config.Options.Add(new RetryHandlerOption { MaxRetry = 0 });
        });
        var response = await _adapter.SendAsync(request, PlaybackMutation.CreateFromDiscriminatorValue, ProblemErrors,
            cancellationToken).ConfigureAwait(false);
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
