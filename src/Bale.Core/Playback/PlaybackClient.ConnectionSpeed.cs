using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Connection;

namespace Bale.Core.Playback;

/// <summary>The outcome of one speed test: a result, nothing measured, or a 429 hold.</summary>
public sealed record ConnectionSpeedTestOutcome(ConnectionSpeedResult? Result, TimeSpan? RetryAfter = null);

/// <summary>How the server took a probe report: kept, refused (never keep it), or not delivered (may keep it).</summary>
public enum ConnectionSpeedReport { Accepted, Refused, TransportFailure }

/// <summary>What Bale knows about this server's speed test on the current connection.</summary>
public sealed class ConnectionSpeedState
{
    private readonly Lock _gate = new();
    private ConnectionSpeedSchedule? _schedule;
    private ConnectionStateV2? _connection;
    private DateTimeOffset? _heldUntil;

    public ConnectionSpeedSchedule? Schedule { get { lock (_gate) return _schedule; } }
    public ConnectionStateV2? Connection { get { lock (_gate) return _connection; } }
    public DateTimeOffset? HeldUntil { get { lock (_gate) return _heldUntil; } }
    /// <summary>Set by the platform: on a metered link no estimate is sent and no automatic test runs.</summary>
    public bool Metered { get; set; }

    internal void Update(ConnectionSpeedSchedule? schedule, ConnectionStateV2? connection)
    {
        lock (_gate)
        {
            _schedule = schedule;
            if (connection is not null) _connection = connection;
        }
    }

    internal void Hold(DateTimeOffset until)
    {
        lock (_gate) _heldUntil = _heldUntil is { } held && held > until ? held : until;
    }

    /// <summary>A failed or unknown read changes nothing; a profile/account change discards the reading.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _schedule = null;
            _connection = null;
        }
    }

    /// <summary>The estimate a playback start sends, or null (unknown, stale, metered).</summary>
    public int? StartEstimate(DateTimeOffset now) => Metered ? null : ConnectionSpeedPolicy.Estimate(Connection, Schedule, now);
}

public sealed partial class PlaybackClient
{
    /// <summary>The speed-test state for this server; the platform drives tests, starts read the estimate.</summary>
    public ConnectionSpeedState Speed { get; } = new();

    /// <summary>
    /// The capability response's schedule, or null without connection_speed_v1. The probe must be on this
    /// server's origin: the authenticated client never sends credentials to another host.
    /// </summary>
    public ConnectionSpeedSchedule? Schedule(PlaybackCapabilities capabilities)
    {
        if (!ConnectionSpeed.Offered(capabilities.Features) || string.IsNullOrWhiteSpace(capabilities.BandwidthProbeUrl)) return null;
        if (!Uri.TryCreate(Server, capabilities.BandwidthProbeUrl, out var probe)
            || !string.Equals(probe.Scheme, Server.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(probe.Host, Server.Host, StringComparison.OrdinalIgnoreCase) || probe.Port != Server.Port)
            return null;
        var maxAge = capabilities.BandwidthProbeMaxAgeSeconds is long seconds and > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromDays(7);
        int? target = capabilities.BandwidthProbeTargetKbps is long t and > 0 ? (int)Math.Min(t, ConnectionSpeedMeter.MaxKbps) : null;
        return new(probe, target, capabilities.BandwidthProbeAutomatic ?? true, maxAge);
    }

    /// <summary>Reads capabilities and the connection state, keeping both in <see cref="Speed"/>.</summary>
    public async Task<ConnectionStateV2?> RefreshConnectionAsync(CancellationToken cancellationToken = default)
    {
        var scope = _captureScope();
        var capabilities = await GetCapabilitiesAsync(scope, cancellationToken).ConfigureAwait(false);
        var schedule = Schedule(capabilities);
        if (schedule is null)
        {
            Speed.Update(null, null);
            return null;
        }
        using var request = Request(HttpMethod.Get, new Uri(Server, "/api/v2/playback/connection"), scope);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        scope.Request.CheckCurrent();
        if (!response.IsSuccessStatusCode)
        {
            Speed.Update(schedule, null);
            return null;
        }
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var state = await JsonSerializer.DeserializeAsync(body, ConnectionSpeedJson.Default.ConnectionStateV2, cancellationToken)
            .ConfigureAwait(false);
        Speed.Update(schedule, state);
        return state;
    }

    /// <summary>
    /// Runs one speed test against the schedule's probe on one connection. The clock starts at the first
    /// body byte; a missing first byte after 10 s, a dropped connection or a short body measures nothing.
    /// </summary>
    public async Task<ConnectionSpeedTestOutcome> RunSpeedTestAsync(ConnectionSpeedSchedule schedule, ConnectionSpeedTestMode mode,
        CancellationToken cancellationToken)
    {
        var scope = _captureScope();
        var meter = new ConnectionSpeedMeter(mode, mode == ConnectionSpeedTestMode.Quick ? schedule.TargetKbps : null);
        var uri = WithSize(schedule.ProbeUrl, meter.RequestedBytes);
        using var firstByte = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        firstByte.CancelAfter(ConnectionSpeedMeter.FirstByteTimeout);
        HttpResponseMessage response;
        try
        {
            using var request = Request(HttpMethod.Get, uri, scope);
            request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, firstByte.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(null);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter is { } header
                    ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null) : null;
                return new(null, retry is { } wait ? (wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait) : ConnectionSpeed.DefaultRetryAfter);
            }
            if (!response.IsSuccessStatusCode) return new(null);
            var expected = response.Content.Headers.ContentLength;
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(firstByte.Token).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                var read = await body.ReadAsync(buffer, firstByte.Token).ConfigureAwait(false);
                if (read == 0) return new(null);
                var clock = Stopwatch.StartNew();
                long received = read;
                meter.Add(0, read);
                // The limit closes silent windows too: a stalled transfer stops on the client's clock.
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(TimeSpan.FromMilliseconds(meter.LimitMs));
                while (true)
                {
                    int count;
                    try { count = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return new(meter.Result(meter.StopAt(meter.LimitMs) ?? meter.LimitMs));
                    }
                    var now = clock.ElapsedMilliseconds;
                    if (count == 0)
                    {
                        // A clean end; a body short of its Content-Length is a failed transfer.
                        if (expected is { } length && received < length) return new(null);
                        return new(meter.Result(meter.StopAt(now) ?? now, now));
                    }
                    if (meter.StopAt(now) is { } stop && stop <= now) return new(meter.Result(stop));
                    meter.Add(now, count);
                    received += count;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return new(null);
            }
        }
    }

    /// <summary>Reports a result. A manual Test speed result clears the server's playback sample.</summary>
    public async Task<ConnectionSpeedReport> ReportSpeedTestAsync(ConnectionProbeReportV2 report, CancellationToken cancellationToken)
    {
        var scope = _captureScope();
        try
        {
            using var request = Request(HttpMethod.Post, new Uri(Server, "/api/v2/playback/bandwidth-probe/results"), scope);
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(report, ConnectionSpeedJson.Default.ConnectionProbeReportV2));
            request.Content.Headers.ContentType = new("application/json");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            scope.Request.CheckCurrent();
            if (response.IsSuccessStatusCode) return ConnectionSpeedReport.Accepted;
            return (int)response.StatusCode >= 500 ? ConnectionSpeedReport.TransportFailure : ConnectionSpeedReport.Refused;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ConnectionSpeedReport.TransportFailure;
        }
    }

    /// <summary>DELETE /api/v2/playback/connection/learned-limit (Settings → Reset).</summary>
    public async Task<bool> ResetLearnedLimitAsync(CancellationToken cancellationToken)
    {
        var scope = _captureScope();
        using var request = Request(HttpMethod.Delete, new Uri(Server, "/api/v2/playback/connection/learned-limit"), scope);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        scope.Request.CheckCurrent();
        return response.IsSuccessStatusCode;
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri uri, PlaybackRequestScope scope)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.ParseAdd("application/json");
        // Kiota 2.1.2 HttpRequestMessageExtensions.GetRequestOption<T> uses the type's FullName.
        request.Options.Set(new HttpRequestOptionsKey<ProfileRequestOption>(typeof(ProfileRequestOption).FullName!), scope.Request);
        return request;
    }

    private static Uri WithSize(Uri probe, long size)
    {
        var builder = new UriBuilder(probe);
        var query = builder.Query.TrimStart('?');
        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Where(part => !part.StartsWith("size=", StringComparison.Ordinal));
        builder.Query = string.Join('&', parts.Append("size=" + size.ToString(CultureInfo.InvariantCulture)));
        return builder.Uri;
    }
}
