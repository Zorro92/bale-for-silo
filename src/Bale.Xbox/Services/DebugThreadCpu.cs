#if DEBUG_SERVER
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Bale.Xbox.Services;

/// <summary>
/// Per-thread CPU of this process over a window, grouped by thread description (dav1d names its
/// workers) or else by the module holding the thread's start address. Only file names are reported.
/// Threads that start or exit inside the window are counted only for the part both snapshots see,
/// so the report also gives the whole-process figure for comparison.
/// </summary>
internal static unsafe partial class DebugThreadCpu
{
    internal const string Usage = "usage: GET /threads?seconds=1..30 (default 10)\n";

    private const uint Th32csSnapThread = 0x4;
    private const uint ThreadQueryInformation = 0x0040;
    private const uint ThreadQueryLimitedInformation = 0x0800;
    private const uint ModuleFromAddress = 0x4;
    private const uint ModuleUnchangedRefcount = 0x2;
    private const int ThreadQuerySetWin32StartAddress = 9;

    private readonly record struct ThreadSample(string Group, long CpuTicks);

    internal static async Task<(string Status, string Body)> MeasureAsync(string? secondsText)
    {
        var seconds = 10;
        if (secondsText is not null
            && (!int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds is < 1 or > 30))
        {
            return ("400 Bad Request", Usage);
        }

        var processBefore = DebugProcessCpu.Read(out var failure);
        var before = await Task.Run(() => Snapshot(out failure)).ConfigureAwait(false);
        if (before is null) return ("500 Internal Server Error", (failure ?? "thread snapshot failed") + "\n");
        var started = Environment.TickCount64;
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        var after = await Task.Run(() => Snapshot(out failure)).ConfigureAwait(false);
        var processAfter = DebugProcessCpu.Read(out _);
        var wallSeconds = (Environment.TickCount64 - started) / 1000.0;
        if (after is null) return ("500 Internal Server Error", (failure ?? "thread snapshot failed") + "\n");

        var groups = new Dictionary<string, (int Threads, long Ticks)>(StringComparer.Ordinal);
        long sum = 0;
        foreach (var (id, sample) in after)
        {
            // A thread id seen in both snapshots is the same thread only if its CPU time did not go backwards.
            var delta = before.TryGetValue(id, out var old) && old.CpuTicks <= sample.CpuTicks
                ? sample.CpuTicks - old.CpuTicks : 0;
            sum += delta;
            groups.TryGetValue(sample.Group, out var group);
            groups[sample.Group] = (group.Threads + 1, group.Ticks + delta);
        }

        string Cores(long ticks) => (ticks / 1e7 / wallSeconds).ToString("0.000", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"window={wallSeconds:0.00}s threads={after.Count} sum={Cores(sum)} cores");
        if (processBefore is { } p0 && processAfter is { } p1)
            text.Append(" process=").Append(Cores((p1 - p0).Ticks)).Append(" cores");
        text.Append('\n');
        foreach (var (name, group) in groups.OrderByDescending(entry => entry.Value.Ticks))
            text.Append(Cores(group.Ticks)).Append(" cores  ").Append(group.Threads).Append(" threads  ").Append(name).Append('\n');
        return ("200 OK", text.ToString());
    }

    private static Dictionary<uint, ThreadSample>? Snapshot(out string? failure)
    {
        failure = null;
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapThread, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            failure = DebugFfmpegPlaytest.Failure("CreateToolhelp32Snapshot unavailable", Marshal.GetHRForLastWin32Error());
            return null;
        }

        try
        {
            var self = GetCurrentProcessId();
            var result = new Dictionary<uint, ThreadSample>();
            var entry = new ThreadEntry32 { Size = (uint)sizeof(ThreadEntry32) };
            for (var more = Thread32First(snapshot, ref entry) != 0; more; more = Thread32Next(snapshot, ref entry) != 0)
            {
                if (entry.OwnerProcessId != self) continue;
                var handle = OpenThread(ThreadQueryInformation | ThreadQueryLimitedInformation, 0, entry.ThreadId);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    if (GetThreadTimes(handle, out _, out _, out var kernel, out var user) == 0) continue;
                    result[entry.ThreadId] = new(GroupOf(handle), kernel + user);
                }
                finally { CloseHandle(handle); }
            }
            return result;
        }
        finally { CloseHandle(snapshot); }
    }

    private static string GroupOf(IntPtr thread)
    {
        if (GetThreadDescription(thread, out var description) >= 0 && description != IntPtr.Zero)
        {
            try
            {
                if (Marshal.PtrToStringUni(description) is { Length: > 0 } name) return "name:" + name;
            }
            finally { LocalFree(description); }
        }

        IntPtr start;
        if (NtQueryInformationThread(thread, ThreadQuerySetWin32StartAddress, &start, IntPtr.Size, null) != 0 || start == IntPtr.Zero)
            return "start:unknown";
        if (GetModuleHandleExW(ModuleFromAddress | ModuleUnchangedRefcount, start, out var module) == 0)
            return "start:unmapped";
        var buffer = stackalloc char[520];
        var length = GetModuleFileNameW(module, buffer, 520);
        if (length == 0) return "start:unnamed-module";
        // File name only: never report package or system paths.
        var path = new string(buffer, 0, (int)length);
        return "start:" + Path.GetFileName(path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int Thread32First(IntPtr snapshot, ref ThreadEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int Thread32Next(IntPtr snapshot, ref ThreadEntry32 entry);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentProcessId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenThread(uint access, int inherit, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadDescription(IntPtr thread, out IntPtr description);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(IntPtr handle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationThread(IntPtr thread, int informationClass, void* information, int length, uint* returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetModuleFileNameW(IntPtr module, char* fileName, uint size);
}
#endif
