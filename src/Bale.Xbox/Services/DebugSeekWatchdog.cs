#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only seek-watchdog test switches (<c>bale-debug.sh seek-watchdog</c>, PlayerPage.SeekWatchdog.cs): another
/// budget for seeks issued after the change, and a one-shot hang: the next local seek is
/// watched but never reaches the source, as a wrapper that stopped answering. The skip's own 10 s wait times out, the
/// watchdog remounts at the target; arm it again after that remount to see a second stuck seek fail to the replan.
/// Release builds use the 15 s default.
/// </summary>
internal static class DebugSeekWatchdog
{
    private static long _budgetMs = PlaybackSeekWatchdog.BudgetMs;
    private static int _hang;

    internal static long BudgetMs => Volatile.Read(ref _budgetMs);

    internal static string Hang => Volatile.Read(ref _hang) != 0 ? "once" : "none";

    /// <summary>1–120 seconds, or default (15).</summary>
    internal static bool TrySetBudget(string? value)
    {
        if (value == "default")
        {
            Volatile.Write(ref _budgetMs, PlaybackSeekWatchdog.BudgetMs);
            return true;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 120) return false;
        Volatile.Write(ref _budgetMs, seconds * 1000L);
        return true;
    }

    internal static bool TrySetHang(string? value)
    {
        if (value is not ("none" or "once")) return false;
        Volatile.Write(ref _hang, value == "once" ? 1 : 0);
        return true;
    }

    /// <summary>True for the seek being issued when the hang is armed, cleared so the next seek reaches the source.</summary>
    internal static bool TakeHang() => Interlocked.Exchange(ref _hang, 0) != 0;

    internal static string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"seek-watchdog={BudgetMs / 1000}s{(BudgetMs == PlaybackSeekWatchdog.BudgetMs ? " (default)" : "")} hang={Hang}\n");
}
#endif
