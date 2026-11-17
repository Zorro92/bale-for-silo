using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace Bale.Core.Playback;

/// <summary>
/// <c>POST /api/v2/playback/route-events</c> (silo-server apiv2 PlaybackRouteEventBody): a diagnostic event about one
/// plan of a playback attempt. Diagnostics never control playback; the server dedupes on (attempt, event_id).
/// </summary>
public sealed record PlaybackRouteEventV2(string InstallationId, string EventId, int ProtocolVersion, string PlaybackAttemptId,
    string Event, IReadOnlyDictionary<string, string> Diagnostics)
{
    public string? SessionId { get; init; }
    public string? PlanId { get; init; }
    public string? PlanAttemptId { get; init; }
    public string? PlanAttemptKey { get; init; }
    public IReadOnlyList<string>? AppliedQuirkIds { get; init; }
    public string? QuirkRegistryRevision { get; init; }
}

/// <summary>The server identity of one adopted plan, captured when it was adopted, for its <c>plan_summary</c>.</summary>
public sealed record PlanSummaryContext(string PlaybackAttemptId, string? SessionId, string? PlanId, string? PlanAttemptId,
    string? PlanAttemptKey, IReadOnlyList<string> ServerFeatures)
{
    public IReadOnlyList<string> AppliedQuirkIds { get; init; } = [];
    public string? QuirkRegistryRevision { get; init; }

    /// <summary>The identity of <paramref name="plan"/> as the page adopted it.</summary>
    public static PlanSummaryContext From(string playbackAttemptId, string? sessionId, Api.Models.PlaybackPlan plan,
        string planAttemptId, IReadOnlyList<string>? serverFeatures) =>
        new(playbackAttemptId, sessionId, plan.PlanId, planAttemptId, plan.PlanAttemptKey, serverFeatures ?? [])
        {
            AppliedQuirkIds = plan.AppliedQuirks?.Select(quirk => quirk.Id).OfType<string>().Where(id => id.Length > 0).Take(16).ToArray() ?? [],
            QuirkRegistryRevision = plan.AppliedQuirks?.Select(quirk => quirk.RegistryRevision).FirstOrDefault(revision => !string.IsNullOrEmpty(revision)),
        };
}

public static class PlaybackRouteEvents
{
    public const string PlanSummary = "plan_summary";

    /// <summary>
    /// The <c>plan_summary</c> route event for a plan that stopped being the playing plan (buffering-quality design,
    /// Telemetry; silo-apple planSummaryRouteEvent), or null when the server does not advertise
    /// <c>playback_plan_summary_v1</c> or the plan has no attempt identity.
    /// </summary>
    public static PlaybackRouteEventV2? ForPlanSummary(BufferingPlanSummary summary, PlanSummaryContext context, string installationId,
        string eventId)
    {
        if (!context.ServerFeatures.Contains(PlaybackProtocol.PlanSummaryFeature)
            || context.PlaybackAttemptId is not { Length: >= 8 and <= 128 }
            || string.IsNullOrWhiteSpace(installationId) || string.IsNullOrWhiteSpace(eventId)) return null;
        return new(installationId, eventId, PlaybackProtocol.Version, context.PlaybackAttemptId, PlanSummary,
            BufferingQualityWire.Diagnostics(summary))
        {
            SessionId = Bounded(context.SessionId),
            PlanId = Bounded(context.PlanId),
            PlanAttemptId = Bounded(context.PlanAttemptId),
            PlanAttemptKey = Bounded(context.PlanAttemptKey),
            AppliedQuirkIds = context.AppliedQuirkIds.Count > 0 ? context.AppliedQuirkIds : null,
            QuirkRegistryRevision = Bounded(context.QuirkRegistryRevision),
        };

        // The schema's maxLength; an id that does not fit is left out rather than refused.
        static string? Bounded(string? value) => value is { Length: > 0 and <= 128 } ? value : null;
    }

    internal static byte[] Serialize(PlaybackRouteEventV2 routeEvent) =>
        JsonSerializer.SerializeToUtf8Bytes(routeEvent, PlaybackRouteEventJson.Default.PlaybackRouteEventV2);
}

public sealed partial class PlaybackClient
{
    /// <summary>
    /// Posts one route event. Best effort: a failure is reported, never thrown for network or HTTP errors, and nothing
    /// retries (a later send would have to omit throughput_kbps, which the server files under the sender's connection).
    /// </summary>
    public async Task<bool> ReportRouteEventAsync(PlaybackRouteEventV2 routeEvent, CancellationToken cancellationToken = default)
    {
        var scope = _captureScope();
        try
        {
            using var request = Request(HttpMethod.Post, new Uri(Server, "/api/v2/playback/route-events"), scope);
            // A 429 means drop (PlaybackRouteEventBody): never let the retry handler resend it.
            request.Options.Set(new HttpRequestOptionsKey<RetryHandlerOption>(typeof(RetryHandlerOption).FullName!),
                new RetryHandlerOption { MaxRetry = 0 });
            request.Content = new ByteArrayContent(PlaybackRouteEvents.Serialize(routeEvent));
            request.Content.Headers.ContentType = new("application/json");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            scope.Request.CheckCurrent();
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlaybackRouteEventV2))]
internal sealed partial class PlaybackRouteEventJson : JsonSerializerContext;
