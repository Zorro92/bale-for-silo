using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bale.Core.Native;

// The start-budget handle ABI (native/bale-core/src/abi_start_budget.rs): the clock and a bit set in, a byte out.

internal sealed class StartBudgetHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() { BaleCoreStartBudget.bale_core_start_budget_destroy(handle); return true; }
}

internal static class BaleCoreStartBudget
{
    private const string Library = "bale_core";

    [DllImport(Library, ExactSpelling = true)] public static extern StartBudgetHandle bale_core_start_budget_create(long budgetMs, long now);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_start_budget_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)] public static extern byte bale_core_start_budget_update(StartBudgetHandle handle, long now, uint bits);
    [DllImport(Library, ExactSpelling = true)] public static extern long bale_core_start_budget_remaining(StartBudgetHandle handle, long now);
}
