#if DEBUG_SERVER
using System.Runtime.InteropServices;

namespace Bale.Xbox.Services;

/// <summary>Fresh current-process counters without the diagnostic-report cache or other-process access.</summary>
internal static class DebugProcessCpu
{
    internal static TimeSpan? Read(out string? failure)
    {
        failure = null;
        try
        {
            // -1 is GetCurrentProcess's pseudo-handle. FILETIME and TimeSpan ticks both use 100 ns units.
            if (GetProcessTimes(new IntPtr(-1), out _, out _, out var kernel, out var user) != 0)
                return DebugFfmpegPlaytest.CpuTime(kernel, user);
            failure = DebugFfmpegPlaytest.Failure("GetProcessTimes unavailable", Marshal.GetHRForLastWin32Error());
        }
        catch (Exception ex) { failure = DebugFfmpegPlaytest.Failure("GetProcessTimes unavailable", ex.HResult); }
        return null;
    }

    // Blittable arguments/return value keep the P/Invoke compatible with Native AOT and disabled marshalling.
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
}
#endif
