using System.Globalization;
using System.Text.Json.Serialization;

namespace Bale.Core.Playback;

/// <summary>
/// silo-server's connection speed test (docs/design/2026-10-17-buffering-quality.md, "Connection speed test"
/// and "Reporting and display"; docs/playback-api.md "Bandwidth probe"). Wire shapes and the client rules
/// for when a reading is current, when to test and what a playback start sends.
/// </summary>
public static class ConnectionSpeed
{
    public const string BufferingQualityFeature = "buffering_quality_v1";
    public const string ConnectionSpeedFeature = "connection_speed_v1";
    public const string ProbeSource = "probe";
    /// <summary>A missing or unreadable Retry-After on a 429 holds automatic tests this long.</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>The server offers the speed test, and estimates need buffering_quality_v1 on the start.</summary>
    public static bool Offered(IReadOnlyList<string>? serverFeatures) =>
        serverFeatures?.Contains(ConnectionSpeedFeature) == true && serverFeatures.Contains(BufferingQualityFeature);

    /// <summary>"Retry-After" in seconds or as an HTTP date; missing or unreadable counts as 30 s, under 1 as 1 s.</summary>
    public static TimeSpan RetryAfter(string? header, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header)) return DefaultRetryAfter;
        if (long.TryParse(header.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, int.MaxValue));
        if (DateTimeOffset.TryParse(header, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            var wait = at - now;
            return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
        }
        return DefaultRetryAfter;
    }
}

/// <summary>The capability response's speed-test fields (with connection_speed_v1).</summary>
public sealed record ConnectionSpeedSchedule(Uri ProbeUrl, int? TargetKbps, bool Automatic, TimeSpan MaxAge);

/// <summary>GET /api/v2/playback/connection: this device's state on the connection it is using now.</summary>
public sealed record ConnectionStateV2
{
    public string? Connection { get; init; }
    public ConnectionProbeV2? Probe { get; init; }
    public ConnectionSampleV2? PlaybackSample { get; init; }
    public ConnectionLearnedLimitV2? LearnedLimit { get; init; }
    public int? AutoBudgetKbps { get; init; }
}

public sealed record ConnectionProbeV2(int Kbps, bool LowerBound, DateTimeOffset MeasuredAt, string? Transport);
public sealed record ConnectionSampleV2(int Kbps, DateTimeOffset MeasuredAt);
public sealed record ConnectionLearnedLimitV2(int Kbps, DateTimeOffset ObservedAt, DateTimeOffset? ExpiresAt);

/// <summary>POST /api/v2/playback/bandwidth-probe/results.</summary>
public sealed record ConnectionProbeReportV2(int Kbps, long Bytes, int DurationMs, string Transport, bool LowerBound, bool Manual);

/// <summary>The client rules for a reading ("When to test").</summary>
public static class ConnectionSpeedPolicy
{
    /// <summary>A reading is current while it is younger than the server's bandwidth_probe_max_age_seconds.</summary>
    public static bool Current(DateTimeOffset measuredAt, ConnectionSpeedSchedule schedule, DateTimeOffset now) =>
        now - measuredAt < schedule.MaxAge && measuredAt <= now + TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether the current reading proves the link, so no automatic test is due. A settled probe proves it;
    /// a lower bound (a quick test that reached its target, or a playback sample) only while it is at least
    /// the current target. Without a target any current reading proves it.
    /// </summary>
    public static bool ProvesLink(ConnectionStateV2 state, ConnectionSpeedSchedule schedule, DateTimeOffset now)
    {
        var probe = state.Probe is { } p && Current(p.MeasuredAt, schedule, now) ? p : null;
        var sample = state.PlaybackSample is { } s && Current(s.MeasuredAt, schedule, now) ? s : null;
        if (probe is null && sample is null) return false;
        if (schedule.TargetKbps is not { } target) return true;
        if (probe is { LowerBound: false }) return true;
        return Math.Max(probe?.Kbps ?? 0, sample?.Kbps ?? 0) >= target;
    }

    /// <summary>A playback start sends the highest current reading (probe or playback sample), as "probe".</summary>
    public static int? Estimate(ConnectionStateV2? state, ConnectionSpeedSchedule? schedule, DateTimeOffset now)
    {
        if (state is null || schedule is null) return null;
        int? best = null;
        if (state.Probe is { } probe && Current(probe.MeasuredAt, schedule, now)) best = probe.Kbps;
        if (state.PlaybackSample is { } sample && Current(sample.MeasuredAt, schedule, now))
            best = Math.Max(best ?? 0, sample.Kbps);
        return best is >= ConnectionSpeedMeter.MinKbps and <= ConnectionSpeedMeter.MaxKbps ? best : null;
    }

    /// <summary>An automatic quick test is due: allowed by the server, nothing current proves the link, no 429 hold.</summary>
    public static bool AutomaticTestDue(ConnectionStateV2 state, ConnectionSpeedSchedule schedule, DateTimeOffset now,
        DateTimeOffset? heldUntil) =>
        schedule.Automatic && !ProvesLink(state, schedule, now) && (heldUntil is not { } until || now >= until);

    /// <summary>
    /// Settings' rate text: Mbps rounded down, one decimal below 10 Mbps, whole from 10, kbps below 1 Mbps,
    /// no digit grouping. Rounding down keeps lower bounds and budgets from overstating the link.
    /// </summary>
    public static string FormatRate(int kbps)
    {
        if (kbps < 1000) return kbps.ToString(CultureInfo.InvariantCulture) + " kbps";
        if (kbps < 10_000) return (Math.Floor(kbps / 100.0) / 10).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps";
        return (kbps / 1000).ToString(CultureInfo.InvariantCulture) + " Mbps";
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConnectionStateV2))]
[JsonSerializable(typeof(ConnectionProbeReportV2))]
internal sealed partial class ConnectionSpeedJson : JsonSerializerContext;
