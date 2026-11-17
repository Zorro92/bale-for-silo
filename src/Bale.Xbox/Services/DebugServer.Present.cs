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
/// Debug only. The engine runs on its own thread in bale_gpu.dll; its swap chain handle is set on the
/// panel on the UI thread. hdr=on switches HDMI to HDR10 first, as the player does, and restores after.
/// </summary>
internal sealed partial class DebugServer
{
    internal const string PresentUsage =
        "usage: GET /playtest-present?url=...&seconds=10 (1–60)&target=bgra|fp16|rgb10&hdr=on|off\n";

    private static readonly Guid IidSwapChainPanelNative2 = new("D5A2F60C-37B2-44A2-937B-8D8EB9726821");

    private static async Task<(string Status, string Body)> PlaytestPresentAsync(IReadOnlyDictionary<string, string> query)
    {
        if (query.Keys.Any(key => key is not ("url" or "seconds" or "target" or "hdr")))
            return ("400 Bad Request", PresentUsage);
        if (!DebugMediaEngine.TryParse(query.GetValueOrDefault("url"), query.GetValueOrDefault("seconds") ?? "10", out var uri,
                out var duration, out var error))
            return ("400 Bad Request", error + "\n");
        var target = query.GetValueOrDefault("target");
        if (target is not (null or "bgra" or "fp16" or "rgb10")) return ("400 Bad Request", PresentUsage);
        var hdr = query.GetValueOrDefault("hdr");
        if (hdr is not (null or "on" or "off")) return ("400 Bad Request", PresentUsage);
        if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)
            return ("409 Conflict", "a playtest is already running\n");
        var host = new PresentHost();
        try
        {
            Log.Info($"Debug present probe (target {target ?? "bgra"}, hdr {hdr ?? "off"}): start");
            var opened = await OnUiTaskAsync(() => host.OpenAsync(hdr == "on"));
            if (host.Panel == 0) return ("200 OK", opened);
            var middle = Task.Delay(TimeSpan.FromSeconds(duration / 2.0 + 1))
                .ContinueWith(_ => OnUiAsync(DisplayHdrOutput.CurrentModeText)).Unwrap();
            var body = await Task.Factory.StartNew(() => host.Run(uri, duration, target), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var during = await middle;
            var closed = await OnUiTaskAsync(host.CloseAsync);
            Log.Info("Debug present probe: done");
            return ("200 OK", opened + body + $"Display during playback: {during}\n" + closed);
        }
        catch (Exception ex)
        {
            // Type and HRESULT only: never the message.
            return ("200 OK", $"Present probe error: {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally
        {
            if (host.IsOpen) await OnUiTaskAsync(host.CloseAsync);
            Interlocked.Exchange(ref _playtestBusy, 0);
        }
    }

    private sealed unsafe class PresentHost
    {
        private Popup? _popup;
        private SwapChainPanel? _view;
        private DisplayHdrOutput? _hdr;
        private uint _width;
        private uint _height;

        /// <summary>The panel's ISwapChainPanelNative2 (owned reference), or 0.</summary>
        internal nint Panel { get; private set; }

        internal bool IsOpen => Panel != 0 || _popup is not null || _hdr is not null;

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
            _view = new SwapChainPanel { Width = bounds.Width, Height = bounds.Height };
            _popup = new Popup { Child = _view, IsOpen = true };
            var inspectable = WinRT.MarshalInspectable<object>.FromManaged(_view);
            try
            {
                var iid = IidSwapChainPanelNative2;
                var hr = Marshal.QueryInterface(inspectable, in iid, out var native);
                if (hr < 0)
                {
                    Close();
                    return $"Display before: {before}\nHDR request: {switched}\nPanel: QI ISwapChainPanelNative2 FAILS 0x{unchecked((uint)hr):X8}\n";
                }
                Panel = native;
            }
            finally { Marshal.Release(inspectable); }
            return $"Display before: {before}\nHDR request: {switched}\n"
                + $"Panel: {bounds.Width:0}x{bounds.Height:0} view pixels, {_width}x{_height} raw (scale {scale:0.###})\n";
        }

        internal string Run(Uri uri, int seconds, string? target)
        {
            var abi = bale_gpu_abi_version();
            if (abi != DebugRustProbe.EngineAbi) return $"Rust GPU layer ABI {abi} (expected {DebugRustProbe.EngineAbi}) MISMATCH\n";
            var url = uri.AbsoluteUri;
            var format = target ?? string.Empty;
            var buffer = new byte[16 * 1024];
            var self = GCHandle.Alloc(this);
            try
            {
                uint written;
                fixed (char* text = url)
                fixed (char* targetText = format)
                fixed (byte* report = buffer)
                    written = bale_gpu_engine_present(text, (uint)url.Length, 0, (uint)seconds, targetText, (uint)format.Length,
                        _width, _height, &Attach, GCHandle.ToIntPtr(self), report, (uint)buffer.Length);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length));
            }
            finally { self.Free(); }
        }

        /// <summary>Called on the engine thread with the swap chain handle: set it on the panel on the UI thread.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Attach(nint context, nint handle)
        {
            try
            {
                if (GCHandle.FromIntPtr(context).Target is not PresentHost host || host.Panel == 0) return unchecked((int)0x80004003);
                var hr = unchecked((int)0x8000FFFF);
                CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.High, () =>
                {
                    // ISwapChainPanelNative2: IUnknown (3), SetSwapChain (3), SetSwapChainHandle (4).
                    var vtable = *(nint**)host.Panel;
                    hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[4])(host.Panel, handle);
                }).AsTask().Wait(TimeSpan.FromSeconds(5));
                return hr;
            }
            catch (Exception ex) { return ex.HResult; }
        }

        internal async Task<string> CloseAsync()
        {
            Close();
            var restored = "nothing to restore";
            if (_hdr is { } output)
            {
                await output.RestoreAsync();
                _hdr = null;
                restored = "restore requested";
            }
            return $"HDR restore: {restored}\nDisplay after: {DisplayHdrOutput.CurrentModeText()}\n";
        }

        private void Close()
        {
            if (_popup is { } popup) popup.IsOpen = false;
            _popup = null;
            _view = null;
            if (Panel != 0)
            {
                Marshal.Release(Panel);
                Panel = 0;
            }
        }
    }

    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_gpu_abi_version();

    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe uint bale_gpu_engine_present(char* url, uint urlLength, nint source, uint seconds, char* target,
        uint targetLength, uint width, uint height, delegate* unmanaged[Cdecl]<nint, nint, int> attach, nint context,
        byte* report, uint reportCapacity);
}
#endif
