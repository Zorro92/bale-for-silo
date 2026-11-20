#if DEBUG_SERVER
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.System;

namespace Bale.Xbox.Services;

internal static partial class DebugMemoryProbe
{
    private static DebugMemprobeMemory Memory() => new(MemoryManager.AppMemoryUsage,
        MemoryManager.AppMemoryUsageLimit, MemoryManager.AppMemoryUsageLevel.ToString());

    private sealed partial class CpuMemory : IDisposable
    {
        // Reserve the small tracking array before pressure, so an allocation is always owned before touch.
        private readonly List<nint> _blocks = new(4096);
        public ulong Bytes { get; private set; }
        public unsafe void Allocate(ulong bytes)
        {
            var pointer = NativeMemory.Alloc((nuint)bytes);
            if (pointer == null) throw new OutOfMemoryException();
            _blocks.Add((nint)pointer);
            Bytes += bytes;
            for (nuint offset = 0; offset < (nuint)bytes; offset += 4096)
                ((byte*)pointer)[offset] = 0xA5;
        }
        public unsafe void Dispose()
        {
            foreach (var block in _blocks) NativeMemory.Free((void*)block);
            _blocks.Clear();
            Bytes = 0;
        }
    }

    public static string Run(DebugMemprobeOptions options)
    {
        var report = new StringBuilder();
        var limitChanges = 0;
        var increased = 0;
        void Line(string line)
        {
            report.Append(line).Append('\n');
            // Log each completed step immediately, so a terminated app leaves the last safe reading.
            Log.Info(line);
        }
        string Events() => $"limit_changes={Volatile.Read(ref limitChanges)} usage_increased={Volatile.Read(ref increased)}";
        void LimitChanging(object? sender, AppMemoryUsageLimitChangingEventArgs args) => Interlocked.Increment(ref limitChanges);
        void UsageIncreased(object? sender, object args) => Interlocked.Increment(ref increased);
        var limitSubscribed = false;
        var usageSubscribed = false;
        var stop = "max";
        DebugMemprobeGpu? gpu = null;
        using var cpu = new CpuMemory();
        try
        {
            Line(DebugMemprobeReport.Header(options, Memory()));
            MemoryManager.AppMemoryUsageLimitChanging += LimitChanging;
            limitSubscribed = true;
            MemoryManager.AppMemoryUsageIncreased += UsageIncreased;
            usageSubscribed = true;

            // Mixed holds the CPU blocks until AFTER the entire D3D11 sequence, using the same guard.
            var cpuTarget = options.Mode == "cpu" ? options.MaxBytes : options.CpuBytes;
            var index = 0;
            while (cpu.Bytes < cpuTarget)
            {
                if (DebugMemprobeReport.NearLimit(Memory(), options.StepBytes)) { stop = "app-limit"; break; }
                var timer = Stopwatch.StartNew();
                try { cpu.Allocate(Math.Min(options.StepBytes, cpuTarget - cpu.Bytes)); }
                catch (OutOfMemoryException ex)
                {
                    Line(DebugMemprobeReport.CpuStep("cpu", ++index, cpu.Bytes, Memory(), timer.Elapsed.TotalMilliseconds) + $" allocation_hr={DebugMemprobeReport.Hr(ex.HResult)} {Events()}");
                    stop = "cpu-allocation";
                    break;
                }
                Line(DebugMemprobeReport.CpuStep("cpu", ++index, cpu.Bytes, Memory(), timer.Elapsed.TotalMilliseconds) + " " + Events());
            }
            if (options.Mode != "cpu" && stop == "max")
            {
                // max includes the two fixed 64 MiB timing textures. Check before their creation too.
                if (DebugMemprobeReport.NearLimit(Memory(), Math.Max(options.StepBytes, DebugMemprobeOptions.GpuWorkingBytes)))
                    stop = "app-limit";
                else
                {
                    gpu = new DebugMemprobeGpu(options.Mode == "d3d12");
                    ulong allocated = 0;
                    double firstCopy = double.NaN;
                    index = 0;
                    do
                    {
                        if (DebugMemprobeReport.NearLimit(Memory(), options.StepBytes)) { stop = "app-limit"; break; }
                        var timer = Stopwatch.StartNew();
                        // First row is a warmed baseline at 128 MiB, then grow by step to max exactly.
                        var bytes = allocated == 0 ? 0 : Math.Min(options.StepBytes, options.MaxBytes - allocated);
                        var hr = gpu.Step(bytes, out var sample);
                        allocated = sample.Bytes;
                        Line($"step={index++} phase=gpu cpu_mib={DebugMemprobeReport.MiB(cpu.Bytes)} {sample} {Memory()} step_ms={DebugMemprobeReport.Milliseconds(timer.Elapsed.TotalMilliseconds)} hr={DebugMemprobeReport.Hr(hr)} stage={sample.Stage} {Events()}");
                        if (sample.RemovedHr < 0) { stop = "device-removed"; break; }
                        if (hr < 0) { stop = $"gpu-failure stage={sample.Stage} hr={DebugMemprobeReport.Hr(hr)}"; break; }
                        if (DebugMemprobeReport.CopyJump(firstCopy, sample.CopyMs)) { stop = "copy-jump>5x"; break; }
                        if (double.IsNaN(firstCopy)) firstCopy = sample.CopyMs;
                    } while (allocated < options.MaxBytes);
                }
            }
        }
        catch (OutOfMemoryException ex) { stop = $"allocation hr={DebugMemprobeReport.Hr(ex.HResult)}"; }
        catch (Exception ex) { stop = $"error hr={DebugMemprobeReport.Hr(ex.HResult)}"; }
        finally
        {
            try { Line($"STOP reason={stop} {Events()}"); }
            finally
            {
                try { gpu?.Dispose(); }
                finally
                {
                    cpu.Dispose();
                    if (limitSubscribed) MemoryManager.AppMemoryUsageLimitChanging -= LimitChanging;
                    if (usageSubscribed) MemoryManager.AppMemoryUsageIncreased -= UsageIncreased;
                    try { Line(DebugMemprobeReport.Freed(Memory())); }
                    catch (Exception ex) { Line($"freed cpu_mib=0 gpu_mib=0 app_memory_hr={DebugMemprobeReport.Hr(ex.HResult)}"); }
                }
            }
        }
        return report.ToString();
    }
}
#endif
