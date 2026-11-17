#if DEBUG_SERVER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace Bale.Xbox.Services;

/// <summary>
/// IMFMediaEngine rendering mode (windowless swap chain) on a full-screen SwapChainPanel: the one
/// lower-level path that keeps the system HDR10 / Dolby Vision presentation (docs/player-plan.md).
/// Debug only. The engine runs on its own thread in bale_gpu.dll, which also sets its swap chain handle
/// on the panel (ISwapChainPanelNative2) when called back here on the UI thread.
/// Open and Close on the UI thread; Run on a dedicated thread.
/// </summary>
internal sealed class DebugEnginePresent
{
    private Popup? _popup;
    private SwapChainPanel? _view;
    private DisplayHdrOutput? _hdr;
    private uint _width;
    private uint _height;

    /// <summary>The panel's IInspectable (owned reference), or 0.</summary>
    private nint _panel;

    internal bool IsOpen => _panel != 0 || _popup is not null || _hdr is not null;

    internal async Task<string> OpenAsync(bool hdr)
    {
        var before = DisplayHdrOutput.CurrentModeText();
        var switched = "not requested";
        if (hdr)
        {
            _hdr = new DisplayHdrOutput();
            switched = await _hdr.TryEnableHdr10Async() ? "switched to HDR10" : "not switched";
        }
        var bounds = Window.Current.Bounds;
        var scale = Windows.Graphics.Display.DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
        _width = (uint)Math.Max(1, Math.Round(bounds.Width * scale));
        _height = (uint)Math.Max(1, Math.Round(bounds.Height * scale));
        // A SwapChainPanel maps swap chain pixels to view pixels; the engine's swap chain is in raw
        // pixels, so lay the panel out at raw size and scale it back, or only its top-left quarter shows.
        _view = new SwapChainPanel
        {
            Width = _width,
            Height = _height,
            RenderTransform = new Windows.UI.Xaml.Media.ScaleTransform { ScaleX = 1 / scale, ScaleY = 1 / scale },
        };
        _popup = new Popup { Child = _view, IsOpen = true };
        // An owned IInspectable of the panel; Rust takes ISwapChainPanelNative2 from it.
        _panel = WinRT.MarshalInspectable<object>.FromManaged(_view);
        return $"Display before: {before}\nHDR request: {switched}\n"
            + $"Panel: {bounds.Width:0}x{bounds.Height:0} view pixels, {_width}x{_height} raw (scale {scale:0.###})\n";
    }

    /// <summary>Plays a URL the engine fetches itself (public media only: no headers).</summary>
    internal string Run(Uri uri, int seconds, string? target) => Run(uri.AbsoluteUri, 0, null, seconds, target);

    /// <summary>Plays an authenticated <see cref="Windows.Storage.Streams.IRandomAccessStream"/> (borrowed COM pointer).</summary>
    internal string RunStream(nint stream, string mime, int seconds) => Run(string.Empty, stream, mime, seconds, null);

    private unsafe string Run(string url, nint source, string? mime, int seconds, string? target)
    {
        try
        {
            var abi = bale_gpu_abi_version();
            if (abi != DebugRustProbe.EngineAbi) return $"Rust GPU layer ABI {abi} (expected {DebugRustProbe.EngineAbi}) MISMATCH\n";
            var format = target ?? string.Empty;
            var type = mime ?? string.Empty;
            var buffer = new byte[16 * 1024];
            var self = GCHandle.Alloc(this);
            try
            {
                uint written;
                fixed (char* text = url)
                fixed (char* mimeText = type)
                fixed (char* targetText = format)
                fixed (byte* report = buffer)
                    written = bale_gpu_engine_present(text, (uint)url.Length, source, mimeText, (uint)type.Length, (uint)seconds,
                        targetText, (uint)format.Length, _width, _height, &Attach, GCHandle.ToIntPtr(self), report, (uint)buffer.Length);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length));
            }
            finally { self.Free(); }
        }
        catch (Exception ex)
        {
            // Type and HRESULT only: never the message.
            return $"Present probe: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})\n";
        }
    }

    /// <summary>Called on the engine thread with the swap chain handle: Rust sets it on the panel, on the UI thread.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Attach(nint context, nint handle)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not DebugEnginePresent host || host._panel == 0) return unchecked((int)0x80004003);
            var hr = unchecked((int)0x8000FFFF);
            CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.High,
                () => hr = bale_gpu_panel_set_swap_chain_handle(host._panel, handle)).AsTask().Wait(TimeSpan.FromSeconds(5));
            return hr;
        }
        catch (Exception ex) { return ex.HResult; }
    }

    internal async Task<string> CloseAsync()
    {
        if (_popup is { } popup) popup.IsOpen = false;
        _popup = null;
        _view = null;
        if (_panel != 0)
        {
            Marshal.Release(_panel);
            _panel = 0;
        }
        var restored = "nothing to restore";
        if (_hdr is { } output)
        {
            await output.RestoreAsync();
            _hdr = null;
            restored = "restore requested";
        }
        return $"HDR restore: {restored}\nDisplay after: {DisplayHdrOutput.CurrentModeText()}\n";
    }

    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_gpu_abi_version();

    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_panel_set_swap_chain_handle(nint panel, nint handle);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe uint bale_gpu_engine_present(char* url, uint urlLength, nint source, char* mime, uint mimeLength,
        uint seconds, char* target, uint targetLength, uint width, uint height, delegate* unmanaged[Cdecl]<nint, nint, int> attach,
        nint context, byte* report, uint reportCapacity);
}

/// <summary>
/// Memory-only switch: when on, the real player hands its authenticated progressive stream to
/// <see cref="DebugEnginePresent"/> for <see cref="Seconds"/> instead of MediaPlayer, then leaves.
/// Library media needs Bale's headers, which a bare URL can't give the engine.
/// </summary>
internal static class DebugPresentEngineSwitch
{
    private static int _seconds;
    private static string _lastReport = "no run yet\n";

    internal static int Seconds => Volatile.Read(ref _seconds);
    internal static string LastReport => Volatile.Read(ref _lastReport);

    internal static void Set(int seconds) => Volatile.Write(ref _seconds, seconds);
    internal static void Report(string text) => Volatile.Write(ref _lastReport, text);
}
#endif
