#if DEBUG_SERVER
using System.Globalization;

namespace Bale.Xbox.Services;

// Pure request/report/stop rules, shared with the Linux tests. All sizes are binary MiB.
internal sealed record DebugMemprobeOptions(string Mode, int StepMiB, int MaxMiB, int CpuMiB)
{
    public const ulong MiB = 1024 * 1024;
    public const ulong GpuWorkingBytes = 128 * MiB;
    public const string Usage = "usage: GET /memprobe?mode=cpu|d3d11|d3d12|mixed&step=100&max=3400[&cpu=1000] (MiB; step/max 1-4096, step<=max; GPU max>=128; cpu 0-4096, mixed only)\n";
    public ulong StepBytes => (ulong)StepMiB * MiB;
    public ulong MaxBytes => (ulong)MaxMiB * MiB;
    public ulong CpuBytes => (ulong)CpuMiB * MiB;

    public static bool TryParse(IReadOnlyDictionary<string, string> query, out DebugMemprobeOptions options)
    {
        options = new("cpu", 100, 3400, 0);
        if (query.Keys.Any(key => key is not ("mode" or "step" or "max" or "cpu"))
            || !query.TryGetValue("mode", out var mode) || mode is not ("cpu" or "d3d11" or "d3d12" or "mixed")
            || !Size(query.GetValueOrDefault("step") ?? "100", 1, out var step)
            || !Size(query.GetValueOrDefault("max") ?? "3400", 1, out var max)
            || step > max || (mode != "cpu" && max < 128)
            || (mode != "mixed" && query.ContainsKey("cpu"))
            || !Size(query.GetValueOrDefault("cpu") ?? (mode == "mixed" ? "1000" : "0"), 0, out var cpu))
            return false;
        options = new(mode, step, max, cpu);
        return true;
    }

    private static bool Size(string text, int minimum, out int size) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out size) && text.Length is >= 1 and <= 4 && size >= minimum && size <= 4096;
}

internal readonly record struct DebugMemprobeMemory(ulong Usage, ulong Limit, string Level)
{
    public override string ToString() => $"app_mib={DebugMemprobeReport.MiB(Usage)} limit_mib={DebugMemprobeReport.MiB(Limit)} level={Level}";
}

internal static class DebugMemprobeReport
{
    public static string MiB(ulong bytes) => (bytes / (double)DebugMemprobeOptions.MiB).ToString("0.0", CultureInfo.InvariantCulture);
    public static string Milliseconds(double ms) => double.IsFinite(ms) && ms >= 0 ? ms.ToString("0.000", CultureInfo.InvariantCulture) : "unavailable";
    public static string Hr(int hr) => $"0x{unchecked((uint)hr):X8}";
    public static bool NearLimit(DebugMemprobeMemory memory, ulong step) => memory.Usage >= memory.Limit || step >= memory.Limit - memory.Usage;
    public static bool CopyJump(double first, double current) => double.IsFinite(first) && first > 0 && double.IsFinite(current) && current > first * 5;
    public static string Header(DebugMemprobeOptions options, DebugMemprobeMemory memory) =>
        $"memprobe mode={options.Mode} step_mib={options.StepMiB} max_mib={options.MaxMiB} cpu_mib={options.CpuMiB} copy_mib={(options.Mode == "cpu" ? 0 : 64)} working_mib={(options.Mode == "cpu" ? 0 : 128)} ledger=excluded {memory}";
    public static string CpuStep(string phase, int index, ulong bytes, DebugMemprobeMemory memory, double ms) =>
        $"step={index} phase={phase} cpu_mib={MiB(bytes)} {memory} step_ms={Milliseconds(ms)}";
    public static string Freed(DebugMemprobeMemory memory) => $"freed cpu_mib=0 gpu_mib=0 {memory}";
}
#endif
