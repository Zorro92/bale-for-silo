using System.Runtime.InteropServices;

namespace Bale.Xbox.Services;

/// <summary>A caption decoder's counters (bale_gpu.dll's subs::cc::BaleCcStats; field order is the C ABI).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BaleCcStats
{
    public ulong Packets, CaptionPackets, Pairs, Cues;
    public uint Late, Depth, Flushes, Seen;
}

/// <summary>
/// In-band closed captions (CEA-608 in the video stream's ATSC A/53 cc_data): bale_gpu.dll's caption decoder
/// (native/bale-gpu src/subs/cc, docs/closed-captions.md). The FFmpeg source opened with this decoder's token
/// (FfmpegSource.ApplyCaptionSink, option bale_cc_sink) hands it every packet of the playing H.264, HEVC or MPEG-2
/// stream on its sample thread; the decoder reorders them by PTS, decodes the selected channel (CC1 first) and keeps
/// what it shows. SubtitleOverlay.AttachCaptions draws its captions in the subtitle appearance; <see cref="Seen"/>
/// says whether the stream carries any. The player gives every FFmpeg mount of those codecs its own decoder and token
/// (PlayerPage.Captions.cs).
/// Create and Dispose run on the UI thread, the other calls on any; every call is counted in and out, and Dispose
/// waits for the calls in flight before it destroys the native decoder, after which calls return RO_E_CLOSED. No call
/// throws, and failures log a fixed message and an HRESULT only, never caption text.
/// </summary>
internal sealed unsafe partial class ClosedCaptions : IDisposable
{
    /// <summary>bale_cc_abi_version: the fork's tap and these exports (only ever added to under 1).</summary>
    internal const uint ExpectedAbi = 1;

    /// <summary>Seen bits: CC1..CC4 carried captions (caption commands, not just padding or Text).</summary>
    internal const uint SeenCc1 = 1, SeenCc2 = 2, SeenCc3 = 4, SeenCc4 = 8;

    /// <summary>Seen bit: the stream carries A/53 cc_data at all, padding included.</summary>
    internal const uint SeenCcData = 1 << 8;

    /// <summary>Seen bit: the stream carries CEA-708 data, which is not decoded yet.</summary>
    internal const uint SeenDtvcc = 1 << 9;

    /// <summary>The fork's token range for bale_cc_sink (1..999999999), as for bale_subtitle_sink.</summary>
    internal const ulong MaxToken = 999999999;

    private const int Disposed = unchecked((int)0x80000013); // RO_E_CLOSED

    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_cc_abi_version();
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_cc_create(nint* captions);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_cc_register(nint captions, ulong token);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_cc_select(nint captions, uint channel, uint service);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_cc_seen(nint captions);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_cc_stats(nint captions, BaleCcStats* stats);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_cc_detach(nint captions);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern void bale_cc_destroy(nint captions);

    // Guards _captions and _inFlight; never held across a native call.
    private readonly object _gate = new();
    private nint _captions;
    private int _inFlight;

    private ClosedCaptions(nint captions) => _captions = captions;

    /// <summary>A decoder on CC1, registered under no token. Null when the DLL or its caption ABI is unavailable.</summary>
    internal static ClosedCaptions? Create()
    {
        try
        {
            var abi = bale_cc_abi_version();
            if (abi != ExpectedAbi)
            {
                Log.Info($"Closed captions: ABI {abi} (expected {ExpectedAbi}); disabled");
                return null;
            }
            nint captions = 0;
            var hr = bale_cc_create(&captions);
            if (hr < 0 || captions == 0)
            {
                Log.Info($"Closed captions: create FAILS 0x{unchecked((uint)hr):X8}");
                return null;
            }
            return new ClosedCaptions(captions);
        }
        catch (Exception ex)
        {
            // Includes DllNotFound/EntryPointNotFoundException for an older DLL; type and HRESULT only.
            Log.Info($"Closed captions: create FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})");
            return null;
        }
    }

    /// <summary>
    /// Takes the packets the FFmpeg source opened with <paramref name="token"/> pushes (1..<see cref="MaxToken"/>; 0
    /// takes none) as a new stream: what was decoded, seen and counted is dropped. A remount registers its new token.
    /// </summary>
    internal int Register(ulong token) => Call("register", captions => bale_cc_register(captions, token));

    /// <summary>
    /// Decodes CC<paramref name="channel"/> (1..4) from now on; a change starts afresh and clears an attached overlay.
    /// CEA-708 services are not decoded yet.
    /// </summary>
    internal int Select(uint channel) => Call("select", captions => bale_cc_select(captions, channel, 0));

    /// <summary>The Seen* bits of the stream so far; 0 once disposed.</summary>
    internal uint Seen()
    {
        uint seen = 0;
        Call("seen", captions =>
        {
            seen = bale_cc_seen(captions);
            return 0;
        });
        return seen;
    }

    internal bool TryGetStats(out BaleCcStats stats)
    {
        stats = default;
        if (!Enter(out var captions)) return false;
        try
        {
            // Not through Call: a lambda cannot take the address of a captured local.
            BaleCcStats value = default;
            var hr = bale_cc_stats(captions, &value);
            if (hr < 0)
            {
                Log.Info($"Closed captions: stats FAILS 0x{unchecked((uint)hr):X8}");
                return false;
            }
            stats = value;
            return true;
        }
        catch (Exception ex)
        {
            Failed("stats", ex);
            return false;
        }
        finally { Exit(); }
    }

    /// <summary>Stops drawing the captions and drops the overlay's caption track if it is still this decoder's.</summary>
    internal int Detach() => Call("detach", captions => bale_cc_detach(captions));

    /// <summary>
    /// Runs <paramref name="call"/> with the native handle, counted in and out like every other call (SubtitleOverlay's
    /// attach, which needs both handles); RO_E_CLOSED once disposed.
    /// </summary>
    internal int Call(string operation, Func<nint, int> call)
    {
        if (!Enter(out var captions)) return Disposed;
        try
        {
            var hr = call(captions);
            if (hr < 0) Log.Info($"Closed captions: {operation} FAILS 0x{unchecked((uint)hr):X8}");
            return hr;
        }
        catch (Exception ex) { return Failed(operation, ex); }
        finally { Exit(); }
    }

    /// <summary>Counts a native call in; false once Dispose has begun.</summary>
    private bool Enter(out nint captions)
    {
        lock (_gate)
        {
            captions = _captions;
            if (captions == 0) return false;
            _inFlight++;
            return true;
        }
    }

    private void Exit()
    {
        lock (_gate)
        {
            if (--_inFlight == 0) Monitor.PulseAll(_gate);
        }
    }

    private static int Failed(string operation, Exception ex)
    {
        Log.Info($"Closed captions: {operation} FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})");
        return ex.HResult < 0 ? ex.HResult : unchecked((int)0x80004005);
    }

    /// <summary>
    /// Unregisters and detaches the decoder and releases it; a tap push already running finishes on its own reference.
    /// New calls are refused from here on, and calls in flight are waited for first.
    /// </summary>
    public void Dispose()
    {
        nint captions;
        lock (_gate)
        {
            captions = _captions;
            if (captions == 0) return;
            _captions = 0;
            while (_inFlight > 0) Monitor.Wait(_gate);
        }
        try { bale_cc_destroy(captions); }
        catch (Exception ex)
        {
            Log.Info($"Closed captions: destroy FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})");
        }
    }
}
