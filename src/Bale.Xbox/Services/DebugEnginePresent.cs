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
    /// <summary>The panel's raw size for the engine thread: width &lt;&lt; 32 | height.</summary>
    private long _size;
    private Windows.Graphics.Display.DisplayInformation? _display;
    private Windows.Graphics.Display.Core.HdmiDisplayInformation? _hdmi;
    private TextBlock? _overlay;
    private Border? _overlayBox;
    /// <summary>An open engine session (bale_gpu_engine_session_open), or 0.</summary>
    private nint _session;

    internal bool HasSession => _session != 0;

    /// <summary>The stats overlay's text; null hides it. UI thread.</summary>
    internal void SetOverlay(string? text)
    {
        if (_overlay is null || _overlayBox is null) return;
        if (text is null) _overlayBox.Visibility = Visibility.Collapsed;
        else
        {
            _overlay.Text = text;
            _overlayBox.Visibility = Visibility.Visible;
        }
    }

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
        _view = new SwapChainPanel();
        _overlay = new TextBlock
        {
            Margin = new Thickness(40, 30, 40, 30), FontSize = 15, FontFamily = new Windows.UI.Xaml.Media.FontFamily("Consolas"),
            Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White), TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        var overlayBox = new Border
        {
            Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0, 0, 0)),
            CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(40, 30, 0, 0), Child = _overlay, Visibility = Visibility.Collapsed,
        };
        _overlayBox = overlayBox;
        var bounds = Window.Current.Bounds;
        var root = new Grid { Width = bounds.Width, Height = bounds.Height };
        // The panel keeps its raw-pixel layout and scale-back transform (Layout); the overlay is view-sized.
        var panelCanvas = new Canvas();
        panelCanvas.Children.Add(_view);
        root.Children.Add(panelCanvas);
        root.Children.Add(overlayBox);
        _popup = new Popup { Child = root, IsOpen = true };
        var layout = Layout();
        _display = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
        _display.DpiChanged += OnDisplayChanged;
        Window.Current.SizeChanged += OnWindowSizeChanged;
        try
        {
            _hdmi = Windows.Graphics.Display.Core.HdmiDisplayInformation.GetForCurrentView();
            if (_hdmi is not null) _hdmi.DisplayModesChanged += OnHdmiModesChanged;
        }
        catch { _hdmi = null; }
        // An owned IInspectable of the panel; Rust takes ISwapChainPanelNative2 from it.
        _panel = WinRT.MarshalInspectable<object>.FromManaged(_view);
        return $"Display before: {before}\nHDR request: {switched}\n" + layout;
    }

    /// <summary>
    /// Sizes the panel to the screen in raw pixels (the HDMI output, 3840x2160 on a 4K mode) and scales it
    /// back to view pixels: a SwapChainPanel maps swap chain pixels to view pixels, so otherwise only the
    /// top-left part of the engine's raw-pixel swap chain would show. UI thread.
    /// </summary>
    private string Layout()
    {
        if (_view is not { } view) return string.Empty;
        var bounds = Window.Current.Bounds;
        var display = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
        var width = display.ScreenWidthInRawPixels;
        var height = display.ScreenHeightInRawPixels;
        if (width == 0 || height == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            width = (uint)Math.Max(1, Math.Round(bounds.Width * display.RawPixelsPerViewPixel));
            height = (uint)Math.Max(1, Math.Round(bounds.Height * display.RawPixelsPerViewPixel));
        }
        view.Width = width;
        view.Height = height;
        if (_session != 0) bale_gpu_engine_session_resize(_session, width, height);
        view.RenderTransform = new Windows.UI.Xaml.Media.ScaleTransform { ScaleX = bounds.Width / width, ScaleY = bounds.Height / height };
        _width = width;
        _height = height;
        Volatile.Write(ref _size, ((long)width << 32) | height);
        return $"Panel: {bounds.Width:0}x{bounds.Height:0} view pixels, {width}x{height} raw (screen)\n";
    }

    private void OnDisplayChanged(Windows.Graphics.Display.DisplayInformation sender, object args) => Relayout();

    private void OnHdmiModesChanged(Windows.Graphics.Display.Core.HdmiDisplayInformation sender, object args) =>
        _ = CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, Relayout);

    private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs e) => Relayout();

    private void Relayout()
    {
        var text = Layout();
        if (text.Length > 0) Log.Info("present-engine: relayout " + text.TrimEnd());
    }

    /// <summary>Polled by the engine thread: the current raw panel size.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong Size(nint context)
    {
        try { return GCHandle.FromIntPtr(context).Target is DebugEnginePresent host ? (ulong)Volatile.Read(ref host._size) : 0; }
        catch { return 0; }
    }

    /// <summary>Plays a URL the engine fetches itself (public media only: no headers).</summary>
    internal string Run(Uri uri, int seconds, string? target) => Run(uri.AbsoluteUri, 0, null, seconds, target, 0, present: true);

    /// <summary>
    /// Plays an authenticated <see cref="Windows.Storage.Streams.IRandomAccessStream"/> (borrowed COM pointer).
    /// <paramref name="present"/> false: frame-server mode, counting every decoded frame (no Open/Close needed).
    /// <paramref name="frameRate"/> (0 = unknown) lets the report compare counts with the expected frames.
    /// </summary>
    internal string RunStream(nint stream, string mime, int seconds, double frameRate, bool present) =>
        Run(string.Empty, stream, mime, seconds, null, frameRate, present);

    /// <summary>Plays a COM media source (FFmpegInteropX's MediaStreamSource; borrowed) through the engine extension.</summary>
    internal string RunSource(nint source, int seconds, double frameRate, bool present) =>
        Run(string.Empty, source, null, seconds, null, frameRate, present);

    private unsafe string Run(string url, nint source, string? mime, int seconds, string? target, double frameRate, bool present)
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
                    written = present
                        ? bale_gpu_engine_present(text, (uint)url.Length, source, mimeText, (uint)type.Length, (uint)seconds, frameRate,
                            targetText, (uint)format.Length, _width, _height, &Attach, &Size, GCHandle.ToIntPtr(self), report, (uint)buffer.Length)
                        : bale_gpu_engine_present(text, (uint)url.Length, source, mimeText, (uint)type.Length, (uint)seconds, frameRate,
                            targetText, (uint)format.Length, 0, 0, null, null, 0, report, (uint)buffer.Length);
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

    /// <summary>
    /// Opens an engine session on <paramref name="source"/> (a media source, or a stream of type
    /// <paramref name="mime"/>) attached to this panel, at <paramref name="start"/> seconds. Any thread
    /// but the UI thread (the attach callback dispatches to it). Returns the open report.
    /// </summary>
    internal unsafe string OpenSession(nint source, string? mime, double start, bool paused)
    {
        try
        {
            var abi = bale_gpu_abi_version();
            if (abi != DebugRustProbe.EngineAbi) return $"Rust GPU layer ABI {abi} (expected {DebugRustProbe.EngineAbi}) MISMATCH\n";
            var type = mime ?? string.Empty;
            var buffer = new byte[16 * 1024];
            var self = GCHandle.Alloc(this);
            try
            {
                uint written;
                nint session;
                fixed (char* mimeText = type)
                fixed (byte* report = buffer)
                    written = bale_gpu_engine_session_open(null, 0, source, mimeText, (uint)type.Length, _width, _height, &Attach,
                        GCHandle.ToIntPtr(self), start, paused ? 1u : 0u, 0, &session, report, (uint)buffer.Length);
                _session = session;
                return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length));
            }
            finally { self.Free(); }
        }
        catch (Exception ex) { return $"Engine session: FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8})\n"; }
    }

    internal int Play() => _session == 0 ? unchecked((int)0x80004003) : bale_gpu_engine_session_play(_session);

    internal int Pause() => _session == 0 ? unchecked((int)0x80004003) : bale_gpu_engine_session_pause(_session);

    internal int Seek(double seconds, bool approximate = false) =>
        _session == 0 ? unchecked((int)0x80004003) : bale_gpu_engine_session_seek(_session, seconds, approximate ? 1u : 0u);

    internal int Volume(double volume, bool muted) =>
        _session == 0 ? unchecked((int)0x80004003) : bale_gpu_engine_session_volume(_session, volume, muted ? 1u : 0u);

    internal unsafe bool TryState(out EngineSessionState state)
    {
        state = default;
        if (_session == 0) return false;
        fixed (EngineSessionState* pointer = &state)
            return bale_gpu_engine_session_state(_session, pointer) >= 0;
    }

    /// <summary>Shuts the session down and returns its closing report (engine error, events).</summary>
    internal unsafe string CloseSession()
    {
        if (_session == 0) return string.Empty;
        var session = _session;
        _session = 0;
        var buffer = new byte[8 * 1024];
        uint written;
        fixed (byte* report = buffer)
            written = bale_gpu_engine_session_close(session, report, (uint)buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length));
    }

    internal async Task<string> CloseAsync()
    {
        var closing = CloseSession();
        if (_display is { } display) display.DpiChanged -= OnDisplayChanged;
        _display = null;
        Window.Current.SizeChanged -= OnWindowSizeChanged;
        if (_hdmi is { } hdmi) hdmi.DisplayModesChanged -= OnHdmiModesChanged;
        _hdmi = null;
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
        return closing + $"HDR restore: {restored}\nDisplay after: {DisplayHdrOutput.CurrentModeText()}\n";
    }

    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_gpu_abi_version();

    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_panel_set_swap_chain_handle(nint panel, nint handle);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe uint bale_gpu_engine_session_open(char* url, uint urlLength, nint source, char* mime, uint mimeLength,
        uint width, uint height, delegate* unmanaged[Cdecl]<nint, nint, int> attach, nint context, double start, uint paused, uint muted,
        nint* session, byte* report, uint reportCapacity);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_engine_session_play(nint session);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_engine_session_pause(nint session);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_engine_session_seek(nint session, double seconds, uint approximate);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_engine_session_volume(nint session, double volume, uint muted);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_engine_session_resize(nint session, uint width, uint height);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern unsafe int bale_gpu_engine_session_state(nint session, EngineSessionState* state);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern unsafe uint bale_gpu_engine_session_close(nint session, byte* report, uint reportCapacity);

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe uint bale_gpu_engine_present(char* url, uint urlLength, nint source, char* mime, uint mimeLength,
        uint seconds, double frameRate, char* target, uint targetLength, uint width, uint height, delegate* unmanaged[Cdecl]<nint, nint, int> attach,
        delegate* unmanaged[Cdecl]<nint, ulong> size, nint context, byte* report, uint reportCapacity);
}

/// <summary>
/// Memory-only switch: when on, the real player hands its authenticated progressive stream to
/// <see cref="DebugEnginePresent"/> for <see cref="Seconds"/> instead of MediaPlayer, then leaves.
/// Library media needs Bale's headers, which a bare URL can't give the engine.
/// </summary>
internal static class DebugPresentEngineSwitch
{
    private static int _seconds;
    private static string _mode = "present";
    private static string _lastReport = "no run yet\n";

    internal static int Seconds => Volatile.Read(ref _seconds);
    /// <summary>
    /// "present" (rendering mode on screen for N seconds), "frames" (frame-server: every decoded frame
    /// counted, nothing shown) or "player" (an interactive engine player with sound, keys and a stats pane).
    /// </summary>
    internal static string Mode => Volatile.Read(ref _mode);
    internal static string LastReport => Volatile.Read(ref _lastReport);

    internal static void Set(int seconds, string mode)
    {
        Volatile.Write(ref _mode, mode);
        Volatile.Write(ref _seconds, seconds);
    }
    internal static void Report(string text) => Volatile.Write(ref _lastReport, text);
}
#endif
