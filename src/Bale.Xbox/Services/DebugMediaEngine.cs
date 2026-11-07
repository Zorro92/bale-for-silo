#if DEBUG_SERVER
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Bale.Xbox.Services;

/// <summary>
/// Debug-only IMFMediaEngine spike: plays a public URL muted in frame-server mode (TransferVideoFrame into
/// our own BGRA texture on our own D3D11 device), then reports the engine's own render/drop statistics.
/// Follows Xbox-ATG-Samples UWPSamples/Graphics/VideoTextureUWP (MediaEnginePlayer.cpp). Raw vtable calls,
/// as in DebugServer.Capabilities.cs, because the app disables runtime marshalling. Slot numbers come from
/// the windows-rs metadata (IUnknown occupies 0–2). Never prints the URL or media exception text.
/// </summary>
internal static unsafe class DebugMediaEngine
{
    private const uint MfVersion = 0x00020070; // MF_SDK_VERSION 2 << 16 | MF_API_VERSION 0x70
    private const uint DxgiFormatB8G8R8A8 = 87;

    private static readonly Guid ClsidMediaEngineClassFactory = new("b44392da-499b-446b-a4cb-005fead0e6d5");
    private static readonly Guid IidMediaEngineClassFactory = new("4d645ace-26aa-4688-9be1-df3516990b93");
    private static readonly Guid IidMediaEngineEx = new("83015ead-b1e6-40d0-a98a-37145ffe1ad1");
    private static readonly Guid IidMediaEngineNotify = new("fee7c112-e776-42b5-9bbf-0048524e2bd5");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid IidMultithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    private static readonly Guid MfMediaEngineCallback = new("c60381b8-83a4-41f8-a3d0-de05076849a9");
    private static readonly Guid MfMediaEngineDxgiManager = new("065702da-1094-486d-8617-ee7cc4ee4648");
    private static readonly Guid MfMediaEngineVideoOutputFormat = new("5066893c-8cf9-42bc-8b8a-472212e52726");

    private static readonly (int Id, string Name)[] Statistics =
    [
        (0, "frames rendered"), (1, "frames dropped"), (6, "frames corrupted"), (4, "frames per second"),
        (5, "playback jitter"), (7, "total frame delay"), (2, "bytes downloaded"),
    ];

    // Only one probe runs at a time (the debug server serves one request at a time); the callback writes here.
    private static readonly object EventsLock = new();
    private static readonly List<(uint Event, long Ms)> Events = [];
    private static Stopwatch? _eventClock;
    private static volatile bool _canPlay, _error, _ended, _firstFrame;
    private static nint _notifyVtable;

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint UInt;
        [FieldOffset(8)] public ulong ULong;
        [FieldOffset(8)] public float Float;
        [FieldOffset(8)] public double Double;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyObject { public nint Vtable; public int References; }

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(nint* attributes, uint size);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateDXGIDeviceManager(uint* token, nint* manager);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);
    [DllImport("oleaut32.dll", ExactSpelling = true)] private static extern nint SysAllocString(char* text);
    [DllImport("oleaut32.dll", ExactSpelling = true)] private static extern void SysFreeString(nint text);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern int SetEvent(nint handle);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels,
        uint featureLevelCount, uint sdkVersion, nint* device, int* featureLevel, nint* context);

    internal static bool TryParse(string? url, string? seconds, out Uri uri, out int duration, out string error)
        => DebugPlaytest.TryParse(url, seconds, out uri, out duration, out error, maxDuration: 60);

    internal static string Run(Uri uri, int seconds)
    {
        var report = new StringBuilder("Media engine probe (frame-server mode, muted, own D3D11 device)\n");
        nint device = 0, context = 0, multithread = 0, manager = 0, attributes = 0, factory = 0, engine = 0, engineEx = 0, texture = 0, source = 0;
        var started = false;
        lock (EventsLock) Events.Clear();
        _canPlay = _error = _ended = _firstFrame = false;
        _eventClock = Stopwatch.StartNew();
        var notify = CreateNotify();
        try
        {
            Check(MFStartup(MfVersion, 0), "MFStartup");
            started = true;
            var featureLevel = 0;
            // HARDWARE=1; VIDEO_SUPPORT 0x800 | BGRA_SUPPORT 0x20; SDK 7. Never falls back to WARP.
            Check(D3D11CreateDevice(0, 1, 0, 0x820, 0, 0, 7, &device, &featureLevel, &context), "D3D11CreateDevice");
            report.Append($"D3D11 feature level: 0x{featureLevel:X4}\n");
            Check(QueryInterface(device, IidMultithread, out multithread), "QI ID3D10Multithread");
            ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(multithread, 5))(multithread, 1);

            uint token;
            Check(MFCreateDXGIDeviceManager(&token, &manager), "MFCreateDXGIDeviceManager");
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Slot(manager, 7))(manager, device, token), "ResetDevice");

            Check(MFCreateAttributes(&attributes, 3), "MFCreateAttributes");
            SetUnknown(attributes, MfMediaEngineDxgiManager, manager);
            SetUnknown(attributes, MfMediaEngineCallback, notify);
            var formatKey = MfMediaEngineVideoOutputFormat;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attributes, 21))(attributes, &formatKey, DxgiFormatB8G8R8A8),
                "SetUINT32 output format");

            var clsid = ClsidMediaEngineClassFactory;
            var factoryId = IidMediaEngineClassFactory;
            Check(CoCreateInstance(&clsid, 0, 1, &factoryId, &factory), "CoCreateInstance MediaEngineClassFactory");
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)Slot(factory, 3))(factory, 0, attributes, &engine),
                "CreateInstance");
            Check(QueryInterface(engine, IidMediaEngineEx, out engineEx), "QI IMFMediaEngineEx");
            report.Append("Engine: created\n");

            Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(engine, 35))(engine, 1), "SetMuted");
            fixed (char* text = uri.AbsoluteUri) source = SysAllocString(text);
            if (source == 0) throw new OutOfMemoryException();
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(engine, 6))(engine, source), "SetSource");

            var openClock = Stopwatch.StartNew();
            while (!_canPlay && !_error && openClock.ElapsedMilliseconds < 20000) Thread.Sleep(10);
            report.Append($"Open: {(_canPlay ? "canplay" : _error ? "error" : "timeout")} after {openClock.ElapsedMilliseconds} ms\n");
            if (!_canPlay) return Finish(report, engine);

            uint width = 0, height = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(engine, 40))(engine, &width, &height);
            var hasVideo = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(engine, 38))(engine) != 0;
            var hasAudio = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(engine, 39))(engine) != 0;
            var duration = ((delegate* unmanaged[Stdcall]<nint, double>)Slot(engine, 19))(engine);
            report.Append($"Video: {width}x{height} hasVideo={hasVideo} hasAudio={hasAudio} duration={duration.ToString("0.###", CultureInfo.InvariantCulture)}s\n");
            if (!hasVideo || width == 0 || height == 0) return Finish(report, engine);

            var desc = new Texture2DDesc
            {
                Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = DxgiFormatB8G8R8A8,
                SampleCount = 1, BindFlags = 0x20 | 0x8, // RENDER_TARGET | SHADER_RESOURCE
            };
            Check(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, nint, nint*, int>)Slot(device, 5))(device, &desc, 0, &texture),
                "CreateTexture2D");

            var cpuBefore = DebugProcessCpu.Read(out _);
            var startPosition = CurrentTime(engine);
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(engine, 32))(engine), "Play");
            var wall = Stopwatch.StartNew();
            long ticks = 0, frames = 0, transferFailures = 0, polls = 0;
            var firstTransferHr = 0;
            double transferMs = 0, maxTransferMs = 0;
            var target = new Rect { Right = (int)width, Bottom = (int)height };
            var border = 0u; // MFARGB, transparent black
            while (wall.ElapsedMilliseconds < seconds * 1000L && !_error && !_ended)
            {
                polls++;
                long pts;
                if (((delegate* unmanaged[Stdcall]<nint, long*, int>)Slot(engine, 44))(engine, &pts) == 0)
                {
                    ticks++;
                    var transferStart = Stopwatch.GetTimestamp();
                    var hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, Rect*, uint*, int>)Slot(engine, 43))(engine, texture, 0, &target, &border);
                    var elapsed = Stopwatch.GetElapsedTime(transferStart).TotalMilliseconds;
                    transferMs += elapsed;
                    maxTransferMs = Math.Max(maxTransferMs, elapsed);
                    if (hr == 0) frames++;
                    else if (transferFailures++ == 0) firstTransferHr = hr;
                }
                Thread.Sleep(1);
            }
            var wallSeconds = wall.Elapsed.TotalSeconds;
            var endPosition = CurrentTime(engine);
            var cpuAfter = DebugProcessCpu.Read(out _);

            report.Append($"Poll loop: {polls} polls ({(polls / wallSeconds).ToString("0", CultureInfo.InvariantCulture)}/s; Thread.Sleep(1))\n");
            report.Append($"Stream ticks: {ticks}; transferred frames: {frames}; transfer failures: {transferFailures}"
                + (transferFailures > 0 ? $" (first 0x{unchecked((uint)firstTransferHr):X8})" : string.Empty) + "\n");
            if (ticks > 0)
                report.Append($"TransferVideoFrame: mean {(transferMs / ticks).ToString("0.000", CultureInfo.InvariantCulture)} ms, max {maxTransferMs.ToString("0.000", CultureInfo.InvariantCulture)} ms\n");
            var advanced = endPosition - startPosition;
            report.Append($"Position: start={Format(startPosition)}s end={Format(endPosition)}s wall={wallSeconds.ToString("0.000", CultureInfo.InvariantCulture)}s ratio={(advanced / wallSeconds).ToString("0.000", CultureInfo.InvariantCulture)}\n");
            if (cpuBefore is { } before && cpuAfter is { } after)
                report.Append($"CPU: {((after - before).TotalSeconds / wallSeconds).ToString("0.000", CultureInfo.InvariantCulture)} cores (process)\n");
            AppendStatistics(report, engineEx);
            report.Append(_error ? "Verdict: FAILS - engine error\n"
                : advanced <= 0 ? "Verdict: FAILS - position did not advance\n"
                : frames == 0 ? "Verdict: FAILS - no frames transferred\n"
                : "Verdict: PLAYS\n");
            return Finish(report, engine);
        }
        catch (Exception ex)
        {
            // Step names and HRESULTs only: never the exception message (it may echo media details).
            report.Append($"Error: {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}"
                + (ex is StepException step ? $" at {step.Step}" : string.Empty) + "\n");
            return Finish(report, engine);
        }
        finally
        {
            if (engine != 0)
            {
                try { ((delegate* unmanaged[Stdcall]<nint, int>)Slot(engine, 42))(engine); } catch { }
            }
            if (source != 0) SysFreeString(source);
            Release(texture); Release(engineEx); Release(engine); Release(factory); Release(attributes);
            Release(manager); Release(multithread); Release(context); Release(device); Release(notify);
            if (started) MFShutdown();
        }
    }

    private static string Finish(StringBuilder report, nint engine)
    {
        if (engine != 0) AppendError(report, engine);
        lock (EventsLock)
        {
            var interesting = Events.Where(e => e.Event is not (2 or 18)).Take(40)
                .Select(e => $"{EventName(e.Event)}@{e.Ms}");
            report.Append($"Events (progress/timeupdate omitted): {string.Join(' ', interesting)}\n");
            report.Append($"First frame ready: {_firstFrame}\n");
        }
        return report.ToString();
    }

    private static void AppendError(StringBuilder report, nint engine)
    {
        nint error = 0;
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(engine, 3))(engine, &error) < 0 || error == 0) return;
            var code = ((delegate* unmanaged[Stdcall]<nint, ushort>)Slot(error, 3))(error);
            var extended = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(error, 4))(error);
            if (code != 0 || extended != 0)
                report.Append($"Engine error: code={code} extended=0x{unchecked((uint)extended):X8}\n");
        }
        catch { }
        finally { Release(error); }
    }

    private static void AppendStatistics(StringBuilder report, nint engineEx)
    {
        report.Append("Engine statistics:");
        foreach (var (id, name) in Statistics)
        {
            var value = default(PropVariant);
            var hr = ((delegate* unmanaged[Stdcall]<nint, int, PropVariant*, int>)Slot(engineEx, 46))(engineEx, id, &value);
            // Numeric variants own nothing, so no PropVariantClear is needed.
            var text = hr < 0 ? $"0x{unchecked((uint)hr):X8}" : value.Vt switch
            {
                3 or 19 => value.UInt.ToString(CultureInfo.InvariantCulture),
                20 or 21 => value.ULong.ToString(CultureInfo.InvariantCulture),
                4 => value.Float.ToString("0.###", CultureInfo.InvariantCulture),
                5 => value.Double.ToString("0.###", CultureInfo.InvariantCulture),
                0 => "empty",
                _ => $"vt={value.Vt}",
            };
            report.Append($" {name}={text};");
        }
        report.Append('\n');
    }

    private static double CurrentTime(nint engine) => ((delegate* unmanaged[Stdcall]<nint, double>)Slot(engine, 16))(engine);

    private static string Format(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string EventName(uint id) => id switch
    {
        1 => "loadstart", 3 => "suspend", 4 => "abort", 5 => "ERROR", 6 => "emptied", 7 => "stalled", 8 => "play", 9 => "pause",
        10 => "loadedmetadata", 11 => "loadeddata", 12 => "waiting", 13 => "playing", 14 => "canplay", 15 => "canplaythrough",
        16 => "seeking", 17 => "seeked", 19 => "ended", 20 => "ratechange", 21 => "durationchange", 22 => "volumechange",
        1000 => "formatchange", 1005 => "bufferingstarted", 1006 => "bufferingended", 1008 => "stablestate",
        1009 => "firstframeready", 1010 => "trackschange", 1012 => "resourcelost", 1014 => "streamrenderingerror",
        1015 => "ratesupported", _ => id.ToString(CultureInfo.InvariantCulture),
    };

    // ---- IMFMediaEngineNotify, implemented as a native object with a static vtable ----

    private static nint CreateNotify()
    {
        if (_notifyVtable == 0)
        {
            var vtable = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
            vtable[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&NotifyQueryInterface;
            vtable[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&NotifyAddRef;
            vtable[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&NotifyRelease;
            vtable[3] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, uint, int>)&NotifyEvent;
            _notifyVtable = (nint)vtable; // process lifetime, shared by every probe
        }
        var instance = (NotifyObject*)NativeMemory.Alloc((nuint)sizeof(NotifyObject));
        instance->Vtable = _notifyVtable;
        instance->References = 1;
        return (nint)instance;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int NotifyQueryInterface(nint self, Guid* iid, nint* result)
    {
        if (*iid == IidUnknown || *iid == IidMediaEngineNotify)
        {
            Interlocked.Increment(ref ((NotifyObject*)self)->References);
            *result = self;
            return 0;
        }
        *result = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint NotifyAddRef(nint self) => (uint)Interlocked.Increment(ref ((NotifyObject*)self)->References);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint NotifyRelease(nint self)
    {
        var count = Interlocked.Decrement(ref ((NotifyObject*)self)->References);
        if (count == 0) NativeMemory.Free((void*)self);
        return (uint)count;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int NotifyEvent(nint self, uint id, nuint param1, uint param2)
    {
        try
        {
            // The engine waits on this handle when created with WAITFORSTABLE_STATE; signal it as the sample does.
            if (id == 1008) { SetEvent((nint)param1); return 0; }
            if (id == 14) _canPlay = true;
            else if (id == 5) _error = true;
            else if (id == 19) _ended = true;
            else if (id == 1009) _firstFrame = true;
            lock (EventsLock)
                if (Events.Count < 400) Events.Add((id, _eventClock?.ElapsedMilliseconds ?? 0));
        }
        catch { }
        return 0;
    }

    // ---- COM helpers ----

    private static void* Slot(nint instance, int index) => (*(void***)instance)[index];

    private static int QueryInterface(nint instance, Guid iid, out nint result)
    {
        nint value = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &value);
        result = value;
        return hr;
    }

    private static void SetUnknown(nint attributes, Guid key, nint value)
        => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Slot(attributes, 27))(attributes, &key, value), "SetUnknown");

    private static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    private static void Check(int hr, string step)
    {
        if (hr < 0) throw new StepException(step, hr);
    }

    private sealed class StepException : Exception
    {
        internal StepException(string step, int hr) : base(step)
        {
            Step = step;
            HResult = hr;
        }

        internal string Step { get; }
    }
}
#endif
