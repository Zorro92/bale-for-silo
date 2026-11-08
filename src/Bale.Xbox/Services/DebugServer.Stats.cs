#if DEBUG_SERVER
namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    // App lifetime, not player lifetime: commands also configure the next player when none is open.
    internal static DebugStatsRowFilter StatsRowFilter { get; } = new();

    private static Task<(string Status, string Body)> StatsAsync() => OnStatsUiAsync(() =>
        CurrentPage(out _) is PlayerPage player
            ? ("200 OK", StatsRowFilter.DescribeStats(player.DebugStatsRows))
            : ("409 Conflict", "no active player page\n"));

    private static Task<(string Status, string Body)> FrameTraceAsync(bool reset = false) => OnStatsUiAsync(() =>
    {
        if (CurrentPage(out _) is not PlayerPage player)
            return ("409 Conflict", "no active player page\n");
        if (reset) player.ResetDebugLateFrameTrace();
        return ("200 OK", player.DebugLateFrameTrace);
    });

    private static Task<(string Status, string Body)> StatsRowsAsync(string? action, string? labels) => OnStatsUiAsync(() =>
    {
        var player = CurrentPage(out _) as PlayerPage;
        IReadOnlyList<(string Label, string Value)> rows = player?.DebugStatsRows ?? [];
        if (!StatsRowFilter.TryApply(action, labels, rows.Select(row => row.Label), out var unknown))
            return ("400 Bad Request", DebugStatsRowFilter.Usage + "\n");
        if (action != "list") player?.RefreshDebugStatsRows();
        var body = player is null ? "no active player page\n" : StatsRowFilter.DescribeLabels(rows);
        if (action != "list")
            body = "Overlay rows updated (memory only; resets on app restart)\n" + body
                + "Unknown labels: " + (unknown.Count == 0 ? "none" : string.Join(", ", unknown)) + "\n";
        return ("200 OK", body);
    });

    private static async Task<(string Status, string Body)> OnStatsUiAsync(Func<(string Status, string Body)> action)
    {
        try
        {
            // The general debug dispatcher formats exception messages, which may contain media URLs.
            return await OnFfmpegUiAsync(action);
        }
        catch (Exception ex)
        {
            var failure = $"debug stats failed hr=0x{unchecked((uint)ex.HResult):X8}\n";
            Log.Info(failure.TrimEnd());
            return ("500 Internal Server Error", failure);
        }
    }
}
#endif
