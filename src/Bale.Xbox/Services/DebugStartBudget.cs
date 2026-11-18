#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only start-budget test switches (<c>bale-debug.sh start-budget</c>, PlayerPage.StartBudget.cs): a shorter
/// budget for every new mount, and a one-shot hang for the next mount only, so its failure replan mounts normally.
/// "open" never finishes opening (the mount's open holds the lifecycle until the budget stops it); "ready" opens but
/// never reports MediaOpened (the backend waits outside the lifecycle). Release builds use the 45 s default.
/// </summary>
internal static class DebugStartBudget
{
    private static long _budgetMs = PlaybackStartBudget.BudgetMs;
    private static string? _hang;

    internal static long BudgetMs => Volatile.Read(ref _budgetMs);

    internal static string Hang => Volatile.Read(ref _hang) ?? "none";

    /// <summary>1–120 seconds, or default (45).</summary>
    internal static bool TrySetBudget(string? value)
    {
        if (value == "default")
        {
            Volatile.Write(ref _budgetMs, PlaybackStartBudget.BudgetMs);
            return true;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 120) return false;
        Volatile.Write(ref _budgetMs, seconds * 1000L);
        return true;
    }

    internal static bool TrySetHang(string? value)
    {
        if (value is not ("none" or "open" or "ready")) return false;
        Volatile.Write(ref _hang, value == "none" ? null : value);
        return true;
    }

    /// <summary>The hang for the mount being created, cleared so the next mount opens normally.</summary>
    internal static string? TakeHang() => Interlocked.Exchange(ref _hang, null);

    /// <summary>Waits until <paramref name="token"/> is cancelled: the start budget, Back or suspension.</summary>
    internal static async Task HangAsync(CancellationToken token)
    {
        Log.Info("player: debug start-budget hang: open waits");
        await Task.Delay(Timeout.Infinite, token);
    }

    internal static string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"start-budget={BudgetMs / 1000}s{(BudgetMs == PlaybackStartBudget.BudgetMs ? " (default)" : "")} hang={Hang}\n");
}
#endif
