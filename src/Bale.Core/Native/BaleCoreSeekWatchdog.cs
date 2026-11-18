using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bale.Core.Native;

// The seek-watchdog handle ABI (native/bale-core/src/abi_seek_watchdog.rs): the clock, a bit set and the player clock in,
// a byte out.

internal sealed class SeekWatchdogHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() { BaleCoreSeekWatchdog.bale_core_seek_watchdog_destroy(handle); return true; }
}

internal static class BaleCoreSeekWatchdog
{
    private const string Library = "bale_core";

    [DllImport(Library, ExactSpelling = true)] public static extern SeekWatchdogHandle bale_core_seek_watchdog_create();
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_seek_watchdog_destroy(nint handle);
    [DllImport(Library, ExactSpelling = true)] public static extern void bale_core_seek_watchdog_begin(SeekWatchdogHandle handle, long now, long budgetMs, double fromSeconds, double targetSeconds);
    [DllImport(Library, ExactSpelling = true)] public static extern byte bale_core_seek_watchdog_update(SeekWatchdogHandle handle, long now, uint bits, double clockSeconds);
    [DllImport(Library, ExactSpelling = true)] public static extern long bale_core_seek_watchdog_remaining(SeekWatchdogHandle handle, long now);
    [DllImport(Library, ExactSpelling = true)] public static extern byte bale_core_seek_watchdog_remount_spent(SeekWatchdogHandle handle);
}
