#if DEBUG_SERVER
using System.Globalization;
using System.Text;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>`bale-debug.sh speed [check|test]`: the connection speed state, a check, or Test speed.</summary>
internal static class DebugConnectionSpeed
{
    internal static string Describe()
    {
        if (Session.Client is not { } client) return "no server\n";
        var speed = client.Playback.Speed;
        var now = DateTimeOffset.UtcNow;
        var builder = new StringBuilder();
        if (speed.Schedule is not { } schedule) builder.Append("Schedule: none (no connection_speed_v1 or not read yet)\n");
        else builder.Append(CultureInfo.InvariantCulture, $"Schedule: target={schedule.TargetKbps?.ToString(CultureInfo.InvariantCulture) ?? "none"} kbps automatic={schedule.Automatic} max_age={schedule.MaxAge.TotalDays:0.#} d\n");
        var state = speed.Connection;
        builder.Append(CultureInfo.InvariantCulture, $"Connection: {state?.Connection ?? "unknown"} budget={state?.AutoBudgetKbps?.ToString(CultureInfo.InvariantCulture) ?? "none"} kbps\n");
        if (state?.Probe is { } probe)
            builder.Append(CultureInfo.InvariantCulture, $"Probe: {probe.Kbps} kbps lower_bound={probe.LowerBound} transport={probe.Transport ?? "?"} age={(now - probe.MeasuredAt).TotalMinutes:0} min\n");
        if (state?.PlaybackSample is { } sample)
            builder.Append(CultureInfo.InvariantCulture, $"Playback sample: {sample.Kbps} kbps age={(now - sample.MeasuredAt).TotalMinutes:0} min\n");
        if (state?.LearnedLimit is { } limit) builder.Append(CultureInfo.InvariantCulture, $"Learned limit: {limit.Kbps} kbps\n");
        builder.Append(CultureInfo.InvariantCulture, $"Metered: {speed.Metered} held_until={speed.HeldUntil?.ToString("u", CultureInfo.InvariantCulture) ?? "none"}\n");
        builder.Append(CultureInfo.InvariantCulture, $"Start estimate: {speed.StartEstimate(now)?.ToString(CultureInfo.InvariantCulture) ?? "none"} kbps\n");
        builder.Append("Last test: ").Append(ConnectionSpeedService.LastOutcome).Append('\n');
        return builder.ToString();
    }

    internal static async Task<string> TestAsync(string? mode)
    {
        if (mode == "check")
        {
            ConnectionSpeedService.Check("debug");
            await Task.Delay(TimeSpan.FromSeconds(9));
            return Describe();
        }
        if (mode != "test") return "mode must be check or test\n";
        var outcome = await ConnectionSpeedService.TestNowAsync();
        return (outcome is null ? "Test speed not run (media active or no schedule)\n" : "") + Describe();
    }
}
#endif
