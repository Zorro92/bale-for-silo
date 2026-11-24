#if DEBUG_SERVER
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Bale.Xbox.Services;

// bale_gpu's exclusive-mode audio output ABI (native/bale-gpu/include/bale_audio_out.h, docs/audio-passthrough.md,
// "Exclusive output") as blittable structs, and the pure parts of the debug audio-out-test: request rules, the test
// tone, the feeder and the report text. Shared with the Linux tests; DebugAudioOutTest.cs makes the native calls.

/// <summary><c>BaleAudioFormat</c> (48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BaleAudioFormat
{
    public uint Kind;
    public uint Channels;
    public uint SampleRate;
    public uint ContainerBits;
    public uint ValidBits;
    public uint ChannelMask;
    public uint IecSubtype;
    public uint EncodedRate;
    public uint EncodedChannels;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
}

/// <summary><c>BaleAudioOutOpenInfo</c> (80 bytes): what create did, also when it failed.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BaleAudioOutOpenInfo
{
    public uint Step;
    public int Hr;
    public int ActivateHr;
    public int PropertiesHr;
    public int FormatHr;
    public int InitializeHr;
    public int RealignHr;
    public uint ActivateMs;
    public long DefaultPeriod100ns;
    public long MinimumPeriod100ns;
    public long Period100ns;
    public uint BufferFrames;
    public uint RingFrames;
    public uint Mmcss;
    public uint Realigned;
    public uint Path;
    public int OffloadHr;
}

/// <summary><c>BaleAudioOutStats</c> (152 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BaleAudioOutStats
{
    public uint Status;
    public int Error;
    public uint BufferFrames;
    public uint RingFrames;
    public uint QueuedFrames;
    public uint QueuedMs;
    public uint Underruns;
    public uint Flushes;
    public ulong FramesWritten;
    public ulong FramesRendered;
    public ulong SilenceFrames;
    public ulong Events;
    public long DevicePosition100ns;
    public long DeviceQpc100ns;
    public long DevicePts100ns;
    public long SyncError100ns;
    public uint Syncs;
    public uint SyncsUnmeasured;
    public uint DropCorrections;
    public uint PadCorrections;
    public ulong DroppedFrames;
    public ulong PaddedFrames;
    public uint Path;
    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
    public uint Reserved4;
    public uint Reserved5;
}

/// <summary>The header's constants the test needs.</summary>
internal static class BaleAudioOut
{
    public const uint WriteClosed = 0xFFFFFFFF;
    public const long PtsNone = long.MinValue;
    public const uint StatusStopped = 0;
    public const uint StatusRunning = 1;
    public const uint StatusPaused = 2;
    public const uint StatusDeviceLost = 3;
    public const uint StatusFailed = 4;
    public const uint PathExclusive = 0;
    public const uint PathExclusiveOffload = 1;
    public const uint PathSharedOffload = 2;
}

/// <summary>
/// <c>GET /audio-out-test?seconds=5&amp;tone=440&amp;channels=8</c>: 1-30 s (default 5), a 20-20000 Hz tone (default
/// 440) in 2, 6 or 8 channels of 24 bits in 32 (default 8, the console's 7.1); or <c>?variants=1</c>: the Initialize
/// variants (one fresh client per row, never started). Either takes <c>uisounds=off</c>: XAML's UI sounds off for the
/// run (they may hold a shared stream on the endpoint), restored afterwards.
/// </summary>
internal readonly record struct DebugAudioOutOptions(int Seconds, int ToneHz, bool Variants = false, int Channels = 8,
    bool UiSoundsOff = false)
{
    public const string Usage = "usage: GET /audio-out-test?seconds=1..30&tone=20..20000&channels=2|6|8 (defaults: 5 s, 440 Hz, 8), "
        + "or GET /audio-out-test?variants=1; either with uisounds=off\n";

    public static bool TryParse(IReadOnlyDictionary<string, string> query, out DebugAudioOutOptions options)
    {
        options = new(5, 440);
        if (query.TryGetValue("uisounds", out var sounds) && sounds != "off") return false;
        var soundsOff = sounds == "off";
        if (query.TryGetValue("variants", out var variants))
        {
            if (variants != "1" || query.Keys.Any(key => key is not ("variants" or "uisounds"))) return false;
            options = new(5, 440, Variants: true, UiSoundsOff: soundsOff);
            return true;
        }
        var channels = query.GetValueOrDefault("channels") ?? "8";
        if (query.Keys.Any(key => key is not ("seconds" or "tone" or "channels" or "uisounds"))
            || !Number(query.GetValueOrDefault("seconds"), 5, 1, 30, out var seconds)
            || !Number(query.GetValueOrDefault("tone"), 440, 20, 20_000, out var tone)
            || channels is not ("2" or "6" or "8"))
            return false;
        options = new(seconds, tone, Channels: channels[0] - '0', UiSoundsOff: soundsOff);
        return true;
    }

    private static bool Number(string? text, int fallback, int minimum, int maximum, out int value)
    {
        if (text is null)
        {
            value = fallback;
            return true;
        }
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && text.Length <= 5
            && value >= minimum && value <= maximum;
    }
}

/// <summary>
/// The test's formats: PCM at 48 kHz, 24 valid bits in a 32-bit container, 2, 6 or 8 channels with ksmedia's masks
/// (0x3, 0x3F, 0x63F). 8 channels is the console's only accepted exclusive format in the probe (2026-11-24, "Allow
/// passthrough" on, no headset), interleaved FL FR FC LFE BL BR SL SR; the variants run also saw 5.1 and 2.0 accepted
/// by IsFormatSupported. The tone plays on front left and right only; the other channels stay silent.
/// </summary>
internal static class DebugAudioOutTone
{
    public const int SampleRate = 48_000;
    /// <summary>-18 dBFS: plainly audible on a TV without being loud.</summary>
    public const double Amplitude = 0.125;
    private const int Full24 = 8_388_607;

    /// <summary>ksmedia.h's mask for 2, 6 (5.1 back) or 8 (7.1 surround) channels.</summary>
    public static uint Mask(int channels) => channels switch
    {
        2 => 0x3,
        6 => 0x3F,
        _ => 0x63F,
    };

    public static int BlockAlign(int channels) => channels * 4;

    public static BaleAudioFormat Format(int channels) => new()
    {
        Kind = 0,
        Channels = (uint)channels,
        SampleRate = SampleRate,
        ContainerBits = 32,
        ValidBits = 24,
        ChannelMask = Mask(channels),
    };

    /// <summary>The pts (100 ns) of absolute frame <paramref name="frame"/>.</summary>
    public static long Pts(long frame) => frame * 10_000_000 / SampleRate;

    /// <summary>
    /// Fills whole frames of <paramref name="samples"/> (<paramref name="channels"/> samples each) from absolute frame
    /// <paramref name="first"/>: the sine's 24-bit value sits in the top three bytes of each 32-bit sample (the low
    /// byte stays zero), front left and right only. The phase comes from the absolute frame, so chunks join without a
    /// click.
    /// </summary>
    public static void Fill(Span<int> samples, long first, int toneHz, int channels)
    {
        var frames = samples.Length / channels;
        for (var i = 0; i < frames; i++)
        {
            // An integer frequency repeats every SampleRate frames, which keeps the phase exact for hours.
            var phase = 2 * Math.PI * toneHz * ((first + i) % SampleRate) / SampleRate;
            var value = (int)Math.Round(Math.Sin(phase) * Amplitude * Full24) << 8;
            var frame = samples.Slice(i * channels, channels);
            frame.Clear();
            frame[0] = value;
            frame[1] = value;
        }
    }
}

/// <summary>bale_audio_out_write: bytes taken, 0 = full, <see cref="BaleAudioOut.WriteClosed"/> = stop.</summary>
internal delegate uint DebugAudioOutWrite(ReadOnlySpan<byte> data, long pts100ns);

/// <summary>
/// Feeds the tone the way the fork's feeder will feed decoded audio: 10 ms chunks with the pts of their first frame,
/// as much as the ring takes; on "full" it stops and the caller tries again a few milliseconds later, and a partly
/// taken chunk is resent from the first frame not taken, with that frame's pts.
/// </summary>
internal sealed class DebugAudioOutFeeder(int toneHz, int channels = 8)
{
    public const int ChunkFrames = 480;
    private readonly int[] _samples = new int[ChunkFrames * channels];
    private long _chunk;
    private int _taken = ChunkFrames;

    public long FramesTaken { get; private set; }
    public int FullAnswers { get; private set; }
    public bool Closed { get; private set; }

    /// <summary>Writes until the ring is full or the output closed; returns the frames taken by this call.</summary>
    public long Feed(DebugAudioOutWrite write)
    {
        long taken = 0;
        while (!Closed)
        {
            if (_taken == ChunkFrames)
            {
                DebugAudioOutTone.Fill(_samples, _chunk, toneHz, channels);
                _taken = 0;
            }
            var data = MemoryMarshal.AsBytes(_samples.AsSpan(_taken * channels));
            var answer = write(data, DebugAudioOutTone.Pts(_chunk + _taken));
            if (answer == BaleAudioOut.WriteClosed)
            {
                Closed = true;
                break;
            }
            var frames = (int)(Math.Min(answer, (uint)data.Length) / DebugAudioOutTone.BlockAlign(channels));
            if (frames == 0)
            {
                FullAnswers++;
                break;
            }
            _taken += frames;
            taken += frames;
            FramesTaken += frames;
            if (_taken == ChunkFrames) _chunk += ChunkFrames;
        }
        return taken;
    }
}

/// <summary>The audio-out-test report: one line per step, a line per second while playing, then a result and a summary.</summary>
internal static class DebugAudioOutReport
{
    public const string SummaryPrefix = "Summary: ";

    // winerror.h and audioclient.h (AUDCLNT_ERR(n) = 0x88890000 + n) names the output can return.
    private static readonly Dictionary<uint, string> HresultNames = new()
    {
        [0x00000001] = "S_FALSE",
        [0x80004002] = "E_NOINTERFACE",
        [0x80004003] = "E_POINTER",
        [0x80004005] = "E_FAIL",
        [0x8000FFFF] = "E_UNEXPECTED",
        [0x80010106] = "RPC_E_CHANGED_MODE",
        [0x800401F0] = "CO_E_NOTINITIALIZED",
        [0x80070005] = "E_ACCESSDENIED",
        [0x80070006] = "E_HANDLE",
        [0x8007000E] = "E_OUTOFMEMORY",
        [0x80070057] = "E_INVALIDARG",
        [0x8007007F] = "HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND)",
        [0x80070490] = "E_NOT_SET",
        [0x800705B4] = "HRESULT_FROM_WIN32(ERROR_TIMEOUT)",
        [0x88890001] = "AUDCLNT_E_NOT_INITIALIZED",
        [0x88890002] = "AUDCLNT_E_ALREADY_INITIALIZED",
        [0x88890003] = "AUDCLNT_E_WRONG_ENDPOINT_TYPE",
        [0x88890004] = "AUDCLNT_E_DEVICE_INVALIDATED",
        [0x88890005] = "AUDCLNT_E_NOT_STOPPED",
        [0x88890006] = "AUDCLNT_E_BUFFER_TOO_LARGE",
        [0x88890007] = "AUDCLNT_E_OUT_OF_ORDER",
        [0x88890008] = "AUDCLNT_E_UNSUPPORTED_FORMAT",
        [0x88890009] = "AUDCLNT_E_INVALID_SIZE",
        [0x8889000A] = "AUDCLNT_E_DEVICE_IN_USE",
        [0x8889000B] = "AUDCLNT_E_BUFFER_OPERATION_PENDING",
        [0x8889000C] = "AUDCLNT_E_THREAD_NOT_REGISTERED",
        [0x8889000E] = "AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED",
        [0x8889000F] = "AUDCLNT_E_ENDPOINT_CREATE_FAILED",
        [0x88890010] = "AUDCLNT_E_SERVICE_NOT_RUNNING",
        [0x88890011] = "AUDCLNT_E_EVENTHANDLE_NOT_EXPECTED",
        [0x88890012] = "AUDCLNT_E_EXCLUSIVE_MODE_ONLY",
        [0x88890013] = "AUDCLNT_E_BUFDURATION_PERIOD_NOT_EQUAL",
        [0x88890014] = "AUDCLNT_E_EVENTHANDLE_NOT_SET",
        [0x88890015] = "AUDCLNT_E_INCORRECT_BUFFER_SIZE",
        [0x88890016] = "AUDCLNT_E_BUFFER_SIZE_ERROR",
        [0x88890017] = "AUDCLNT_E_CPUUSAGE_EXCEEDED",
        [0x88890018] = "AUDCLNT_E_BUFFER_ERROR",
        [0x88890019] = "AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED",
        [0x88890020] = "AUDCLNT_E_INVALID_DEVICE_PERIOD",
        [0x88890026] = "AUDCLNT_E_RESOURCES_INVALIDATED",
    };

    /// <summary><c>S_OK</c>, <c>NAME (0xXXXXXXXX)</c>, or the bare hex for codes without a known name.</summary>
    public static string Hr(int hr) => hr == 0 ? "S_OK"
        : HresultNames.TryGetValue(unchecked((uint)hr), out var name) ? $"{name} (0x{unchecked((uint)hr):X8})"
        : $"0x{unchecked((uint)hr):X8}";

    public static string Step(uint step) => step switch
    {
        0 => "done",
        1 => "arguments",
        2 => "mmdevapi.dll",
        3 => "render thread",
        4 => "default render id",
        5 => "activation",
        6 => "IsFormatSupported",
        7 => "Initialize",
        8 => "event/buffer/clock",
        9 => "output table",
        _ => $"step {step}",
    };

    public static string Status(uint status) => status switch
    {
        BaleAudioOut.StatusStopped => "stopped",
        BaleAudioOut.StatusRunning => "running",
        BaleAudioOut.StatusPaused => "paused",
        BaleAudioOut.StatusDeviceLost => "device lost",
        BaleAudioOut.StatusFailed => "failed",
        _ => $"status {status}",
    };

    public static string Mmcss(uint mmcss) => mmcss switch
    {
        0 => "normal priority (avrt.dll not loaded)",
        1 => "MMCSS Pro Audio",
        2 => "normal priority (AvSetMmThreadCharacteristicsW refused)",
        _ => $"mmcss {mmcss}",
    };

    /// <summary>100 ns as milliseconds with three decimals.</summary>
    public static string Ms(long hns) => (hns / 10_000.0).ToString("0.000", CultureInfo.InvariantCulture);

    public static string Header(DebugAudioOutOptions options) =>
        $"Audio output test: exclusive WASAPI (offload fallbacks), PCM {options.Channels} ch {DebugAudioOutTone.SampleRate} Hz 32-bit (24 valid) "
        + $"mask 0x{DebugAudioOutTone.Mask(options.Channels):X}, {options.ToneHz} Hz tone on front L/R at -18 dBFS, {options.Seconds} s, "
        + "synced to a wall-clock video position\n";

    public static string Path(uint path) => path switch
    {
        BaleAudioOut.PathExclusive => "exclusive",
        BaleAudioOut.PathExclusiveOffload => "exclusive offload",
        BaleAudioOut.PathSharedOffload => "shared offload",
        _ => $"path {path}",
    };

    /// <summary>The app's XAML UI sounds, which may hold a shared stream on the endpoint (context for DEVICE_IN_USE).</summary>
    public static string UiSounds(string state, bool switchedOff) =>
        $"App: XAML UI sounds (ElementSoundPlayer.State) {state}{(switchedOff ? ", switched Off for this run" : string.Empty)}\n";

    /// <summary>Create's result and each step it reached (steps after the one it stopped at are left out).</summary>
    public static string Open(int hr, uint id, in BaleAudioOutOpenInfo info)
    {
        var step = info.Step;
        bool Reached(uint at) => step == 0 || step >= at;
        var text = new StringBuilder()
            .Append("Create: ").Append(Hr(hr));
        if (hr >= 0) text.Append(" id=").Append(id.ToString(CultureInfo.InvariantCulture));
        else text.Append(" at ").Append(Step(step));
        if (Reached(5)) text.Append("; activation ").Append(Hr(info.ActivateHr));
        if (Reached(6))
            text.Append(" in ").Append(info.ActivateMs.ToString(CultureInfo.InvariantCulture)).Append(" ms")
                .Append(", Movie category ").Append(Hr(info.PropertiesHr))
                .Append(", IsFormatSupported ").Append(Hr(info.FormatHr));
        if (Reached(7)) text.Append(", Initialize ").Append(Hr(info.InitializeHr));
        if (info.Realigned != 0) text.Append(", re-aligned Initialize ").Append(Hr(info.RealignHr));
        // The offload tries come after a plain exclusive refusal (UNSUPPORTED_FORMAT / OFFLOAD_MODE_ONLY).
        if (info.Path != BaleAudioOut.PathExclusive || info.OffloadHr != 0)
            text.Append(", exclusive offload Initialize ").Append(Hr(info.OffloadHr));
        if (info.Path == BaleAudioOut.PathSharedOffload)
            text.Append(", shared offload Initialize ").Append(Hr(hr >= 0 ? 0 : hr));
        if (Reached(7)) text.Append("; path ").Append(Path(info.Path));
        text.Append('\n');
        if (info.DefaultPeriod100ns != 0)
        {
            text.Append("Device: period default ").Append(Ms(info.DefaultPeriod100ns)).Append(" ms, minimum ")
                .Append(Ms(info.MinimumPeriod100ns)).Append(" ms");
            if (step == 0)
                text.Append("; initialized ").Append(Ms(info.Period100ns))
                    .Append(" ms, buffer ").Append(info.BufferFrames.ToString(CultureInfo.InvariantCulture))
                    .Append(" frames; ring ").Append(info.RingFrames.ToString(CultureInfo.InvariantCulture))
                    .Append(" frames (").Append((info.RingFrames * 1000L / DebugAudioOutTone.SampleRate).ToString(CultureInfo.InvariantCulture))
                    .Append(" ms); render thread ").Append(Mmcss(info.Mmcss));
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>One line a second while playing.</summary>
    public static string Second(double seconds, in BaleAudioOutStats stats) =>
        string.Create(CultureInfo.InvariantCulture,
            $"  t={seconds:0.0} s: {Status(stats.Status)}, device {Ms(stats.DevicePosition100ns)} ms, queued {stats.QueuedMs} ms, "
            + $"underruns {stats.Underruns}, sync error {Ms(stats.SyncError100ns)} ms ({stats.Syncs} syncs), "
            + $"drops {stats.DropCorrections}, pads {stats.PadCorrections}\n");

    /// <summary>
    /// The device clock against QueryPerformanceCounter between two readings of one stream (no flush between), in
    /// parts per million: positive when the audio clock runs fast. Null without two distinct readings.
    /// </summary>
    public static double? DriftPpm(in BaleAudioOutStats first, in BaleAudioOutStats last)
    {
        if (first.DeviceQpc100ns <= 0 || last.DeviceQpc100ns <= first.DeviceQpc100ns || first.Flushes != last.Flushes)
            return null;
        double wall = last.DeviceQpc100ns - first.DeviceQpc100ns;
        double device = last.DevicePosition100ns - first.DevicePosition100ns;
        return (device - wall) / wall * 1e6;
    }

    public static string Ppm(double? ppm) => ppm is { } value
        ? value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " ppm"
        : "unavailable";

    public static string Result(in BaleAudioOutStats last, double? drift, long wall100ns, long fed, int fullAnswers) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Result: {fed} frames fed ({last.FramesWritten} taken by the ring, {fullAnswers} full answers), {last.FramesRendered} rendered "
            + $"in {last.Events} periods; device clock {Ms(last.DevicePosition100ns)} ms after {Ms(wall100ns)} ms of wall time since start "
            + $"(includes the start latency), drift {Ppm(drift)}; underruns {last.Underruns} ({last.SilenceFrames} silent frames); "
            + $"sync {last.Syncs} measured, {last.SyncsUnmeasured} unmeasured, last error {Ms(last.SyncError100ns)} ms, "
            + $"drops {last.DropCorrections} ({last.DroppedFrames} frames), pads {last.PadCorrections} ({last.PaddedFrames} frames); "
            + $"status {Status(last.Status)}{(last.Error != 0 ? " " + Hr(last.Error) : string.Empty)}\n");

    public static string Summary(in BaleAudioOutStats last, double? drift) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{SummaryPrefix}played; status {Status(last.Status)}{(last.Error != 0 ? " " + Hr(last.Error) : string.Empty)}, "
            + $"underruns {last.Underruns}, drift {Ppm(drift)}, sync error {Ms(last.SyncError100ns)} ms, "
            + $"drops {last.DropCorrections}, pads {last.PadCorrections}\n");

    public static string NotPlayed(string reason) => $"{SummaryPrefix}not played ({reason})\n";
}
#endif
