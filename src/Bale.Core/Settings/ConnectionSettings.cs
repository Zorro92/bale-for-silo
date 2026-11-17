using System.Globalization;
using Bale.Core.Playback;

namespace Bale.Core.Settings;

/// <summary>What became of the last Test speed (silo-android ConnectionSpeedMessage).</summary>
public enum ConnectionSpeedNoticeKind
{
    /// <summary>HTTP 429: "Try again in N seconds", counting down until the wait ends.</summary>
    TryAgain,
    /// <summary>Media was playing or loading.</summary>
    StopPlayback,
    /// <summary>The test ran and measured nothing.</summary>
    Failed,
    /// <summary>The test measured, but the server did not store the result: the row keeps the server's reading.</summary>
    NotSaved,
}

/// <summary>The answer to the last Test speed, shown under the row until the next test or read.</summary>
public sealed record ConnectionSpeedNotice(ConnectionSpeedNoticeKind Kind, DateTimeOffset? RetryUntil = null)
{
    /// <summary>
    /// The notice for one Test speed: media active, nothing measured (no schedule, a failed or cancelled
    /// transfer), a 429, or a result the server did not keep. A kept result needs no notice: the row shows it.
    /// </summary>
    public static ConnectionSpeedNotice? ForTest(bool mediaActive, ConnectionSpeedTestOutcome? outcome, ConnectionSpeedReport? report,
        DateTimeOffset now)
    {
        if (mediaActive) return new(ConnectionSpeedNoticeKind.StopPlayback);
        if (outcome is null) return new(ConnectionSpeedNoticeKind.Failed);
        if (outcome.RetryAfter is { } wait) return new(ConnectionSpeedNoticeKind.TryAgain, now + wait);
        if (outcome.Result is null) return new(ConnectionSpeedNoticeKind.Failed);
        return report == ConnectionSpeedReport.Accepted ? null : new(ConnectionSpeedNoticeKind.NotSaved);
    }

    /// <summary>The line under the row; null once a 429's wait has passed (the notice then hides).</summary>
    public string? Text(DateTimeOffset now) => Kind switch
    {
        ConnectionSpeedNoticeKind.TryAgain => RetryUntil is { } until && until > now
            ? ConnectionSettings.Count((int)Math.Ceiling((until - now).TotalSeconds), "Try again in {0} second", "Try again in {0} seconds")
            : null,
        ConnectionSpeedNoticeKind.StopPlayback => "Stop playback before testing, then try again.",
        ConnectionSpeedNoticeKind.NotSaved => "Couldn’t save the speed test. Try again.",
        _ => "Couldn’t measure the connection speed. Try again.",
    };
}

/// <summary>
/// Settings → Playback's Streaming and Connection groups as the Android TV app draws them
/// (TvSettingsScreen.kt "Streaming", TvConnectionSettings.kt; the buffering-quality design's
/// "Reporting and display"). Null lines and false flags hide their rows.
/// </summary>
/// <param name="ModeShown">The server defines <c>playback.buffering_quality_mode</c>: the mode row and its footer.</param>
/// <param name="ModeValue">The row's value: the effective mode's label.</param>
/// <param name="LearnedLimit">"Limited to 44 Mbps after buffering on Oct 17"; null without a limit or in Never.</param>
/// <param name="ConnectionShown">The server measures speed (connection_speed_v1 with a usable probe).</param>
/// <param name="Skipped">A metered link: an info row and the skipped note replace the speed, Test speed and the budget.</param>
/// <param name="TestValue">"Test speed", or "Testing…" while a test runs.</param>
/// <param name="SpeedLine">"48 Mbps · measured 3 minutes ago on Wi-Fi" or "Not measured yet".</param>
/// <param name="Notice">The last Test speed's answer.</param>
/// <param name="Budget">"Auto plays videos up to 35 Mbps without converting them."</param>
public sealed record ConnectionSettingsView(bool ModeShown, string ModeValue, string? LearnedLimit, bool ConnectionShown, bool Skipped,
    string TestValue, string? SpeedLine, string? Notice, string? Budget)
{
    /// <summary>The Streaming group has a row to show.</summary>
    public bool StreamingShown => ModeShown || LearnedLimit is not null;
}

public static class ConnectionSettings
{
    public const string StreamingTitle = "STREAMING";
    public const string ConnectionTitle = "CONNECTION";
    public const string SpeedLabel = "Connection speed";
    public const string Test = "Test speed";
    public const string Testing = "Testing…";
    public const string Reset = "Reset";
    public const string ResetFailed = "Couldn’t reset the limit. Try again.";
    public const string SkippedMetered = "Speed tests are skipped on metered networks.";
    public const string NotMeasured = "Not measured yet";
    /// <summary>The first option of the mode picker: clears this device's value (settings_useProfile).</summary>
    public const string UseProfileSetting = "Use profile setting";

    /// <summary>
    /// Builds both groups. <paramref name="connection"/> is the last <c>GET /playback/connection</c>;
    /// <paramref name="offered"/> whether the server offers the speed test; times show in <paramref name="zone"/>.
    /// </summary>
    public static ConnectionSettingsView Build(BufferingQualitySetting mode, ConnectionStateV2? connection, bool offered, bool metered,
        bool testing, ConnectionSpeedNotice? notice, DateTimeOffset now, TimeZoneInfo zone)
    {
        var limit = connection?.LearnedLimit;
        // Never ignores the limit, so neither the row nor a budget that counts it shows (ConnectionSpeedUiState.shownIn).
        var never = mode.Value == BufferingQualityMode.Never;
        var budget = metered || (never && limit is not null) ? null : connection?.AutoBudgetKbps;
        return new(
            mode.Supported,
            BufferingQualityModes.Label(mode.Value),
            limit is null || never ? null : LearnedLimitLabel(limit, zone),
            offered,
            metered,
            testing ? Testing : Test,
            metered ? null : SpeedLine(connection, now),
            metered ? null : notice?.Text(now),
            budget is { } kbps ? $"Auto plays videos up to {ConnectionSpeedPolicy.FormatRate(kbps)} without converting them." : null);
    }

    /// <summary>
    /// The latest speed test, else the server's playback sample as a lower bound, else "Not measured yet"
    /// (connectionSpeedLabel). A server time ahead of this clock counts as just now.
    /// </summary>
    public static string SpeedLine(ConnectionStateV2? connection, DateTimeOffset now)
    {
        if (connection?.Probe is { } probe)
        {
            var rate = ConnectionSpeedPolicy.FormatRate(probe.Kbps);
            var ago = Age(probe.MeasuredAt, now);
            var network = Transport(probe.Transport);
            var shown = probe.LowerBound ? "Faster than " + rate : rate;
            return ago is null ? $"{shown} · measured on {network}" : $"{shown} · measured {ago} on {network}";
        }
        if (connection?.PlaybackSample is { } sample)
        {
            var rate = ConnectionSpeedPolicy.FormatRate(sample.Kbps);
            var ago = Age(sample.MeasuredAt, now);
            return ago is null ? $"Faster than {rate} · seen during playback" : $"Faster than {rate} · seen during playback {ago}";
        }
        return NotMeasured;
    }

    /// <summary>"Limited to 44 Mbps after buffering on Oct 17", the day in this device's time zone.</summary>
    public static string LearnedLimitLabel(ConnectionLearnedLimitV2 limit, TimeZoneInfo zone)
    {
        var rate = ConnectionSpeedPolicy.FormatRate(limit.Kbps);
        if (limit.ObservedAt == default) return $"Limited to {rate} after buffering";
        var day = TimeZoneInfo.ConvertTime(limit.ObservedAt, zone).ToString("MMM d", CultureInfo.InvariantCulture);
        return $"Limited to {rate} after buffering on {day}";
    }

    /// <summary>"just now" under a minute, then whole minutes, hours and days, rounded down; null for an unknown time.</summary>
    public static string? Age(DateTimeOffset measuredAt, DateTimeOffset now)
    {
        if (measuredAt == default) return null;
        var minutes = (long)Math.Max(0, (now - measuredAt).TotalMinutes);
        return minutes switch
        {
            < 1 => "just now",
            < 60 => Count((int)minutes, "{0} minute ago", "{0} minutes ago"),
            < 24 * 60 => Count((int)(minutes / 60), "{0} hour ago", "{0} hours ago"),
            _ => Count((int)Math.Min(minutes / (24 * 60), int.MaxValue), "{0} day ago", "{0} days ago"),
        };
    }

    /// <summary>The network a probe ran on: "Wi-Fi", "Ethernet", "cellular", else "this connection".</summary>
    public static string Transport(string? transport) => transport switch
    {
        "wifi" => "Wi-Fi",
        "ethernet" => "Ethernet",
        "cellular" => "cellular",
        _ => "this connection",
    };

    internal static string Count(int count, string one, string other) =>
        string.Format(CultureInfo.InvariantCulture, count == 1 ? one : other, count);
}
