using System.Runtime.InteropServices;
using System.Text;
using Bale.Core.Playback;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Services;

/// <summary>The overlay's counters (bale_gpu.dll's BaleSubStats; field order is the C ABI).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BaleSubStats
{
    public ulong Presents, Renders;
    public uint LastRenderUs, MaxRenderUs, Images, Events, Width, Height;
}

/// <summary>
/// Bale's own subtitle drawing: bale_gpu.dll (Rust) owns a D3D11 composition swap chain attached to a XAML
/// SwapChainPanel and renders the loaded ASS track with libass on its own thread at the media clock. This
/// wrapper only feeds it layout, track, fonts and clock. Create and Dispose run on the UI thread; the player
/// calls everything else there too. No call throws into the player, and failures log a fixed message and an
/// HRESULT only: never subtitle text, font names or URLs.
/// </summary>
internal sealed unsafe class SubtitleOverlay : IDisposable
{
    private const uint ExpectedAbi = 1;
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int Disposed = unchecked((int)0x80000013); // RO_E_CLOSED

    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_sub_abi_version();
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_sub_overlay_create(nint panel, uint width, uint height, nint* overlay);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_sub_overlay_set_layout(nint overlay, uint width, uint height, float scaleX, float scaleY, float videoX, float videoY, float videoW, float videoH);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_sub_overlay_load_ass(nint overlay, byte* data, nuint length);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_sub_overlay_add_font(nint overlay, byte* name, nuint nameLength, byte* data, nuint length);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_sub_overlay_clear(nint overlay);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_sub_overlay_set_clock(nint overlay, long positionMs, double rate, uint playing);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_sub_overlay_stats(nint overlay, BaleSubStats* stats);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern void bale_sub_overlay_destroy(nint overlay);

    private nint _overlay;

    private SubtitleOverlay(nint overlay) => _overlay = overlay;

    /// <summary>
    /// UI thread. Null when the DLL, its subtitle ABI or the D3D device is unavailable; the player then
    /// simply has no subtitle overlay. <paramref name="layout"/> supplies the panel's physical size.
    /// </summary>
    internal static SubtitleOverlay? Create(SwapChainPanel panel, SubtitleOverlayLayout layout)
    {
        nint native = 0;
        try
        {
            var abi = bale_sub_abi_version();
            if (abi != ExpectedAbi)
            {
                Log.Info($"Subtitle overlay: ABI {abi} (expected {ExpectedAbi}); disabled");
                return null;
            }
            // The panel's IInspectable, AddRef'd for this call only: Rust AddRefs what it keeps.
            native = WinRT.MarshalInspectable<object>.FromManaged(panel);
            if (native == 0)
            {
                Log.Info("Subtitle overlay: panel pointer unavailable; disabled");
                return null;
            }
            nint overlay = 0;
            var hr = bale_sub_overlay_create(native, Math.Max(layout.Width, 1), Math.Max(layout.Height, 1), &overlay);
            if (hr < 0 || overlay == 0)
            {
                Log.Info($"Subtitle overlay: create FAILS 0x{unchecked((uint)hr):X8}");
                return null;
            }
            Log.Info($"Subtitle overlay: created {layout.Width}x{layout.Height}");
            var created = new SubtitleOverlay(overlay);
            created.SetLayout(layout);
            return created;
        }
        catch (Exception ex)
        {
            // Includes DllNotFound/EntryPointNotFoundException for an older DLL; type and HRESULT only.
            Log.Info($"Subtitle overlay: create FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})");
            return null;
        }
        finally
        {
            if (native != 0)
            {
                try { Marshal.Release(native); }
                catch { }
            }
        }
    }

    internal int SetLayout(SubtitleOverlayLayout layout) => Call("layout", overlay => bale_sub_overlay_set_layout(overlay,
        Math.Max(layout.Width, 1), Math.Max(layout.Height, 1), layout.ScaleX, layout.ScaleY,
        layout.VideoX, layout.VideoY, layout.VideoWidth, layout.VideoHeight));

    /// <summary>Replaces the track with ASS/SSA script text; returns the HRESULT (E_INVALIDARG = unparseable).</summary>
    internal int LoadAss(string text)
    {
        if (string.IsNullOrEmpty(text)) return InvalidArgument;
        byte[] utf8;
        try { utf8 = Encoding.UTF8.GetBytes(text); }
        catch (Exception ex) { return Failed("load", ex); }
        return Call("load", overlay =>
        {
            fixed (byte* data = utf8) return bale_sub_overlay_load_ass(overlay, data, (nuint)utf8.Length);
        });
    }

    /// <summary>An embedded font (MKV attachment / Silo font bundle) for the next load; the name may be empty.</summary>
    internal int AddFont(string? name, byte[] font)
    {
        if (font is not { Length: > 0 }) return InvalidArgument;
        byte[] utf8;
        try { utf8 = Encoding.UTF8.GetBytes(name ?? string.Empty); }
        catch (Exception ex) { return Failed("font", ex); }
        return Call("font", overlay =>
        {
            // An empty array pins to null, which the ABI allows with a zero length.
            fixed (byte* namePointer = utf8)
            fixed (byte* data = font)
                return bale_sub_overlay_add_font(overlay, namePointer, (nuint)utf8.Length, data, (nuint)font.Length);
        });
    }

    /// <summary>Drops the track; the overlay presents a transparent frame.</summary>
    internal int Clear() => Call("clear", overlay => bale_sub_overlay_clear(overlay));

    /// <summary>Source position in ms at the moment of the call; the render thread extrapolates while playing.</summary>
    internal int SetClock(long positionMs, double rate, bool playing) =>
        Call("clock", overlay => bale_sub_overlay_set_clock(overlay, positionMs, rate, playing ? 1u : 0u));

    internal bool TryGetStats(out BaleSubStats stats)
    {
        stats = default;
        var overlay = _overlay;
        if (overlay == 0) return false;
        try
        {
            // Not through Call: a lambda cannot take the address of a captured local.
            BaleSubStats value = default;
            var hr = bale_sub_overlay_stats(overlay, &value);
            if (hr < 0)
            {
                Log.Info($"Subtitle overlay: stats FAILS 0x{unchecked((uint)hr):X8}");
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
    }

    private delegate int NativeCall(nint overlay);

    private int Call(string operation, NativeCall call)
    {
        var overlay = _overlay;
        if (overlay == 0) return Disposed;
        try
        {
            var hr = call(overlay);
            if (hr < 0) Log.Info($"Subtitle overlay: {operation} FAILS 0x{unchecked((uint)hr):X8}");
            return hr;
        }
        catch (Exception ex) { return Failed(operation, ex); }
    }

    private static int Failed(string operation, Exception ex)
    {
        Log.Info($"Subtitle overlay: {operation} FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})");
        return ex.HResult < 0 ? ex.HResult : unchecked((int)0x80004005);
    }

    /// <summary>UI thread: stops and joins the render thread and releases the device and swap chain.</summary>
    public void Dispose()
    {
        var overlay = Interlocked.Exchange(ref _overlay, 0);
        if (overlay == 0) return;
        try
        {
            bale_sub_overlay_destroy(overlay);
            Log.Info("Subtitle overlay: destroyed");
        }
        catch (Exception ex)
        {
            Log.Info($"Subtitle overlay: destroy FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})");
        }
    }
}
