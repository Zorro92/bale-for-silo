#if DEBUG_SERVER
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Bale.Xbox.Services;

/// <summary>
/// Debug exclusive-mode output test (docs/audio-passthrough.md, "Exclusive output"): opens bale_gpu's WASAPI output
/// with the console's accepted format, plays a tone on front L/R for N seconds, feeding it like the fork's feeder
/// will and syncing it to a wall-clock video position, and reports what the device did. The output is always
/// destroyed. Runs on a dedicated thread (DebugServer.AudioOut.cs), never the UI thread: create waits for the
/// activation. mmdevapi.dll is loaded here like DebugAudioProbe does, avrt.dll too so the render thread can join
/// MMCSS; bale_gpu imports neither.
/// </summary>
internal static class DebugAudioOutTest
{
    /// <summary>The ring the test asks for: the default 500 ms.</summary>
    private const uint RingMs = 500;
    private const int SyncsPerSecond = 10;
    private const int SleepMs = 5;

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe int bale_audio_out_create(nint mmdevapi, BaleAudioFormat* format, uint ringMs, uint* id, BaleAudioOutOpenInfo* info);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe uint bale_audio_out_write(uint id, byte* data, uint bytes, long pts100ns);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern int bale_audio_out_start(uint id);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern int bale_audio_out_pause(uint id);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern int bale_audio_out_sync(uint id, long videoPosition100ns, long qpcTicks);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe int bale_audio_out_stats(uint id, BaleAudioOutStats* stats);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern int bale_audio_out_destroy(uint id);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe uint bale_audio_out_init_variants(nint mmdevapi, byte* report, uint reportCapacity);

    private const int VariantsReportCapacity = 32 * 1024;

    // UWP-allowed (api-ms-win-core-libraryloader); a borrowed handle is never freed. Pointer-only signature:
    // DisableRuntimeMarshalling=true, Native AOT and CA1420.
    [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", ExactSpelling = true)]
    private static extern unsafe nint GetModuleHandleW(char* moduleName);

    internal static unsafe string Run(DebugAudioOutOptions options)
    {
        var report = new StringBuilder(DebugAudioOutReport.Header(options));
        nint mmdevapi = 0, avrt = 0;
        var borrowed = false;
        uint id = 0;
        try
        {
            // As DebugAudioProbe: a bare-name load finds nothing in the UWP package, so System32, else the copy Media
            // Foundation's audio renderer already loaded. bale_gpu keeps its own reference while the output lives.
            if (!NativeLibrary.TryLoad("mmdevapi.dll", typeof(DebugAudioOutTest).Assembly, DllImportSearchPath.System32, out mmdevapi))
            {
                fixed (char* name = "mmdevapi.dll") mmdevapi = GetModuleHandleW(name);
                borrowed = mmdevapi != 0;
            }
            // MMCSS for the render thread only if avrt.dll can be loaded here; bale_gpu only looks for a loaded copy.
            if (!NativeLibrary.TryLoad("avrt.dll", typeof(DebugAudioOutTest).Assembly, DllImportSearchPath.System32, out avrt))
                avrt = 0;
            var format = DebugAudioOutTone.Format;
            var info = default(BaleAudioOutOpenInfo);
            // A native failure ends the app without reaching a catch: leave a breadcrumb first.
            Log.Info("Debug audio-out-test: before bale_audio_out_create");
            var hr = bale_audio_out_create(mmdevapi, &format, RingMs, &id, &info);
            report.Append(DebugAudioOutReport.Open(hr, id, info));
            if (hr < 0 || id == 0)
            {
                id = 0;
                report.Append(DebugAudioOutReport.NotPlayed($"{DebugAudioOutReport.Hr(hr)} at {DebugAudioOutReport.Step(info.Step)}"));
                return report.ToString();
            }
            Play(id, options, report);
        }
        catch (Exception ex)
        {
            // Includes EntryPointNotFoundException for an older bale_gpu.dll; type and HRESULT only.
            report.Append($"Audio output test: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})\n")
                .Append(DebugAudioOutReport.NotPlayed("exception"));
        }
        finally
        {
            if (id != 0)
            {
                Log.Info("Debug audio-out-test: before bale_audio_out_destroy");
                report.Append("Destroy: ").Append(DebugAudioOutReport.Hr(bale_audio_out_destroy(id))).Append('\n');
            }
            if (mmdevapi != 0 && !borrowed) NativeLibrary.Free(mmdevapi);
            if (avrt != 0) NativeLibrary.Free(avrt);
        }
        return report.ToString();
    }

    /// <summary>
    /// The Initialize variants (docs/audio-passthrough.md, "Initialize variants"): bale_gpu activates a fresh client per
    /// row, asks IsFormatSupported and calls Initialize, then releases it without Start, so nothing plays.
    /// </summary>
    internal static unsafe string RunVariants()
    {
        nint mmdevapi = 0;
        var borrowed = false;
        try
        {
            // As DebugAudioProbe: System32, else the copy Media Foundation's audio renderer already loaded.
            if (!NativeLibrary.TryLoad("mmdevapi.dll", typeof(DebugAudioOutTest).Assembly, DllImportSearchPath.System32, out mmdevapi))
            {
                fixed (char* name = "mmdevapi.dll") mmdevapi = GetModuleHandleW(name);
                borrowed = mmdevapi != 0;
            }
            // A native failure ends the app without reaching a catch: leave a breadcrumb first.
            Log.Info("Debug audio-out-test: before bale_audio_out_init_variants");
            var buffer = new byte[VariantsReportCapacity];
            uint written;
            fixed (byte* report = buffer)
                written = bale_audio_out_init_variants(mmdevapi, report, (uint)buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length));
        }
        catch (Exception ex)
        {
            // Includes EntryPointNotFoundException for an older bale_gpu.dll; type and HRESULT only.
            return $"Audio Initialize variants: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})\n"
                + $"{DebugAudioOutReport.SummaryPrefix}not run (exception)\n";
        }
        finally
        {
            if (mmdevapi != 0 && !borrowed) NativeLibrary.Free(mmdevapi);
        }
    }

    private static unsafe void Play(uint id, DebugAudioOutOptions options, StringBuilder report)
    {
        uint Write(ReadOnlySpan<byte> data, long pts)
        {
            fixed (byte* bytes = data) return bale_audio_out_write(id, bytes, (uint)data.Length, pts);
        }
        BaleAudioOutStats Stats()
        {
            BaleAudioOutStats stats;
            var hr = bale_audio_out_stats(id, &stats);
            return hr < 0 ? default : stats;
        }

        var feeder = new DebugAudioOutFeeder(options.ToneHz);
        // Fill the ring before starting, as a player prerolls.
        var prefill = feeder.Feed(Write);
        report.Append($"Prefill: {prefill} frames ({prefill * 1000 / DebugAudioOutTone.SampleRate} ms) queued before start\n");
        var start = Stopwatch.GetTimestamp();
        var started = bale_audio_out_start(id);
        report.Append("Start: ").Append(DebugAudioOutReport.Hr(started)).Append('\n');
        if (started < 0)
        {
            report.Append(DebugAudioOutReport.NotPlayed($"{DebugAudioOutReport.Hr(started)} at start"));
            return;
        }
        var ticks = Stopwatch.Frequency;
        var end = options.Seconds * ticks;
        var nextSync = 0L;
        var nextSecond = ticks;
        var syncFailures = 0;
        var lastSyncError = 0;
        BaleAudioOutStats? first = null;
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = now - start;
            if (elapsed >= end) break;
            feeder.Feed(Write);
            if (feeder.Closed)
            {
                report.Append($"Write: closed after {elapsed * 1000 / ticks} ms (device lost or failed)\n");
                break;
            }
            if (elapsed >= nextSync)
            {
                // The "video" runs on the wall clock from Start; the QPC value is the same instant's.
                var sync = bale_audio_out_sync(id, elapsed * 10_000_000 / ticks, now);
                if (sync < 0)
                {
                    syncFailures++;
                    lastSyncError = sync;
                }
                nextSync += ticks / SyncsPerSecond;
            }
            if (elapsed >= nextSecond)
            {
                var stats = Stats();
                if (first is null && stats.DeviceQpc100ns > 0) first = stats;
                report.Append(DebugAudioOutReport.Second((double)elapsed / ticks, stats));
                nextSecond += ticks;
            }
            Thread.Sleep(SleepMs);
        }
        var last = Stats();
        var wall = (Stopwatch.GetTimestamp() - start) * 10_000_000 / ticks;
        var drift = first is { } reading ? DebugAudioOutReport.DriftPpm(reading, last) : null;
        if (syncFailures > 0)
            report.Append($"Sync: {syncFailures} calls failed, last {DebugAudioOutReport.Hr(lastSyncError)}\n");
        report.Append("Pause: ").Append(DebugAudioOutReport.Hr(bale_audio_out_pause(id))).Append('\n')
            .Append(DebugAudioOutReport.Result(last, drift, wall, feeder.FramesTaken, feeder.FullAnswers))
            .Append(DebugAudioOutReport.Summary(last, drift));
    }
}
#endif
