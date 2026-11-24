#if DEBUG_SERVER
using System.Runtime.InteropServices;
using System.Text;
using Windows.Media.Audio;
using Windows.Media.Devices;

namespace Bale.Xbox.Services;

/// <summary>
/// Audio passthrough capability probe (docs/audio-passthrough.md, "Capability probe"): which encoded formats the
/// default render endpoint would accept as an IEC 61937 bitstream right now. bale_gpu activates an IAudioClient the
/// UWP way and only asks exclusive-mode IsFormatSupported (never Initialize or Start), so Media Foundation keeps
/// the endpoint, even during playback. The endpoint's spatial sound configuration follows as context.
/// mmdevapi.dll is loaded here and handed over: bale_gpu never imports it (like d3d12.dll).
/// </summary>
internal static class DebugAudioProbe
{
    internal const string Usage = "usage: GET /audio-probe (no query keys)\n";
    private const string SummaryPrefix = "Summary: ";
    private const int ReportCapacity = 16 * 1024;
    private static int _busy;

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe uint bale_gpu_audio_probe(nint mmdevapi, byte* report, uint reportCapacity);

    // UWP-allowed (api-ms-win-core-libraryloader); a borrowed handle is never freed. Pointer-only signature:
    // DisableRuntimeMarshalling=true, Native AOT and CA1420.
    [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", ExactSpelling = true)]
    private static extern unsafe nint GetModuleHandleW(char* moduleName);

    internal static async Task<(string Status, string Body)> RunAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return ("409 Conflict", "an audio probe is already running\n");
        try
        {
            // A pool (MTA) thread, never the UI thread: bale_gpu waits up to 2 s for the activation callback.
            var report = await Task.Run(Probe);
            var summary = report.Split('\n').FirstOrDefault(line => line.StartsWith(SummaryPrefix, StringComparison.Ordinal));
            Log.Info("Debug audio-probe: " + (summary is null ? "no summary" : summary[SummaryPrefix.Length..]));
            return ("200 OK", report);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static unsafe string Probe()
    {
        var builder = new StringBuilder();
        nint mmdevapi = 0;
        var borrowed = false;
        try
        {
            // bale_gpu reports a missing module itself, so a failed load still reaches it (with 0). On the console a
            // bare-name load finds nothing (a UWP package's search path has no System32; 0.1.752 reported E_INVALIDARG):
            // load it from System32, else borrow the copy Media Foundation's audio renderer already loaded.
            if (!NativeLibrary.TryLoad("mmdevapi.dll", typeof(DebugAudioProbe).Assembly, DllImportSearchPath.System32, out mmdevapi))
            {
                var loadError = Marshal.GetLastPInvokeError();
                fixed (char* name = "mmdevapi.dll") mmdevapi = GetModuleHandleW(name);
                borrowed = mmdevapi != 0;
                Log.Info($"Debug audio-probe: System32 load failed (error {loadError}); in-process module {(borrowed ? "found" : "missing")}");
            }
            // A native failure ends the app without reaching a catch: leave a breadcrumb first.
            Log.Info("Debug audio-probe: before bale_gpu_audio_probe");
            var buffer = new byte[ReportCapacity];
            uint written;
            fixed (byte* report = buffer)
                written = bale_gpu_audio_probe(mmdevapi, report, (uint)buffer.Length);
            builder.Append(Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length)));
        }
        catch (Exception ex)
        {
            // Includes EntryPointNotFoundException for an older bale_gpu.dll; type and HRESULT only.
            builder.Append($"Audio passthrough probe: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})\n");
        }
        finally
        {
            if (mmdevapi != 0 && !borrowed) NativeLibrary.Free(mmdevapi);
        }
        Log.Info("Debug audio-probe: before spatial audio");
        builder.Append(DescribeSpatialAudio());
        return builder.ToString();
    }

    /// <summary>
    /// The console's spatial sound setting on the same endpoint (Windows.Media.Audio, UniversalApiContract 8):
    /// Dolby Atmos for home theater or DTS:X there means the console itself bitstreams its mix. Context only, not
    /// evidence that an app's own bitstream is accepted.
    /// </summary>
    private static string DescribeSpatialAudio()
    {
        try
        {
            var id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (string.IsNullOrEmpty(id)) return "Spatial audio: no default render endpoint\n";
            var configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(id);
            var formats = SpatialFormats();
            var builder = new StringBuilder("Spatial audio (SpatialAudioDeviceConfiguration): supported=")
                .Append(configuration.IsSpatialAudioSupported ? "yes" : "no")
                .Append(" active=").Append(SpatialName(configuration.ActiveSpatialAudioFormat, formats))
                .Append(" default=").Append(SpatialName(configuration.DefaultSpatialAudioFormat, formats))
                .Append("\n  formats:");
            foreach (var (name, subtype) in formats)
                builder.Append(' ').Append(name).Append('=').Append(configuration.IsSpatialAudioFormatSupported(subtype) ? "yes" : "no");
            return builder.Append('\n').ToString();
        }
        catch (Exception ex)
        {
            return $"Spatial audio: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})\n";
        }
    }

    private static (string Name, string Subtype)[] SpatialFormats() =>
    [
        ("WindowsSonic", SpatialAudioFormatSubtype.WindowsSonic),
        ("DolbyAtmosForHeadphones", SpatialAudioFormatSubtype.DolbyAtmosForHeadphones),
        ("DolbyAtmosForHomeTheater", SpatialAudioFormatSubtype.DolbyAtmosForHomeTheater),
        ("DolbyAtmosForSpeakers", SpatialAudioFormatSubtype.DolbyAtmosForSpeakers),
        ("DTSHeadphoneX", SpatialAudioFormatSubtype.DTSHeadphoneX),
        ("DTSXUltra", SpatialAudioFormatSubtype.DTSXUltra),
        ("DTSXForHomeTheater", SpatialAudioFormatSubtype.DTSXForHomeTheater),
    ];

    /// <summary>The subtype's name, "none" when empty, else the subtype string itself.</summary>
    private static string SpatialName(string? subtype, (string Name, string Subtype)[] formats)
    {
        if (string.IsNullOrEmpty(subtype)) return "none";
        foreach (var (name, known) in formats)
            if (string.Equals(known, subtype, StringComparison.OrdinalIgnoreCase)) return name;
        return subtype;
    }
}
#endif
