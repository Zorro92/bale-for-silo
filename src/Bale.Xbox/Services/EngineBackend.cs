using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.Core;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.Media.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// <see cref="PlaybackBackend"/> over IMFMediaEngine in rendering mode (owner, 2026-11-17: the engine becomes the
/// main player). bale_gpu.dll runs the engine; its windowless swap chain is shown on a raw-pixel SwapChainPanel
/// placed in the page's video host and scaled back to view pixels (the probe's layout, docs/player-plan.md).
/// The session has no callbacks into C#: a 100 ms poll of its state raises the backend's events, on a pool
/// thread like MediaPlayer's. Construct, place and dispose on the UI thread; open from it too (the open runs
/// on the pool, the engine's attach callback hops back to the UI thread).
/// </summary>
internal sealed partial class EngineBackend : PlaybackBackend
{
    /// <summary>bale_gpu.dll's ABI with the session's stream API (keep with NativeFrameDelivery.ExpectedAbi).</summary>
    internal const uint ExpectedAbi = 13;

    private readonly Session _session;
    private readonly Border _host;
    private readonly Canvas _canvas = new();
    private readonly SwapChainPanel _view = new();
    private readonly object _sync = new();
    private nint _panel;
    private nint _handle;
    private long _size;
    private Windows.Graphics.Display.DisplayInformation? _display;
    private Windows.Graphics.Display.Core.HdmiDisplayInformation? _hdmi;
    private Timer? _poll;
    private EngineSessionState _last;
    private bool _opened;
    private bool _ended;
    private bool _failed;
    private bool _disposed;
    private EngineStream[] _audio = [];
    private EngineStream[] _video = [];

    public EngineBackend(Border host)
    {
        _host = host;
        _session = new Session(this);
        _canvas.Children.Add(_view);
        host.Child = _canvas;
        Layout();
        Window.Current.SizeChanged += OnWindowSizeChanged;
        // A display mode change (Bale's own HDMI switch, or the console's) changes the raw pixel size.
        _display = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
        _display.DpiChanged += OnDisplayChanged;
        try
        {
            _hdmi = Windows.Graphics.Display.Core.HdmiDisplayInformation.GetForCurrentView();
            if (_hdmi is not null) _hdmi.DisplayModesChanged += OnHdmiModesChanged;
        }
        catch (Exception) { _hdmi = null; }
        // An owned IInspectable of the panel; Rust takes ISwapChainPanelNative2 from it.
        _panel = WinRT.MarshalInspectable<object>.FromManaged(_view);
    }

    public override PlaybackBackendSession PlaybackSession => _session;

    public override string Name => "IMFMediaEngine";

    /// <summary>The session state from the last poll (frame counters reset on seek and pause).</summary>
    internal EngineSessionState LastState => _last;

    /// <summary>The open report (step names and HRESULTs only), for the log.</summary>
    public string Report { get; private set; } = string.Empty;

    /// <summary>
    /// Opens <paramref name="source"/>: an IMFMediaSource-capable COM object (FFmpeg's MediaStreamSource, or its
    /// MediaSource wrapper), or with <paramref name="mime"/> an IRandomAccessStream. Raises MediaOpened or
    /// MediaFailed. The caller keeps the source alive until Dispose.
    /// </summary>
    public async Task OpenAsync(object source, string? mime, bool canSeek)
    {
        _session.CanSeekSource = canSeek;
        nint pointer;
        var stream = source as IRandomAccessStream;
        pointer = stream is not null
            ? WinRT.MarshalInterface<IRandomAccessStream>.FromManaged(stream)
            : ((WinRT.IWinRTObject)source).NativeObject.ThisPtr;
        int hr;
        try { (hr, Report) = await Task.Run(() => Open(pointer, stream is not null ? mime ?? "video/mp4" : null)); }
        finally { if (stream is not null) Marshal.Release(pointer); }
        if (_disposed) return;
        if (_handle == 0)
        {
            RaiseFailed(new PlaybackBackendFailure(hr, Decoder: false, Network: false));
            return;
        }
        LoadStreams();
        _poll = new Timer(_ => Poll(), null, 100, 100);
        Poll();
        _opened = true;
        RaiseOpened();
    }

    private unsafe (int HResult, string Report) Open(nint source, string? mime)
    {
        if (bale_gpu_abi_version() != ExpectedAbi) return (unchecked((int)0x80004002), "Rust GPU layer ABI mismatch\n");
        var type = mime ?? string.Empty;
        var buffer = new byte[16 * 1024];
        var self = GCHandle.Alloc(this);
        try
        {
            uint written;
            nint session;
            var (width, height) = Unpack(Volatile.Read(ref _size));
            fixed (char* mimeText = type)
            fixed (byte* report = buffer)
                written = bale_gpu_engine_session_open(null, 0, source, mimeText, (uint)type.Length, width, height, &Attach,
                    GCHandle.ToIntPtr(self), 0, 1, 0, &session, report, (uint)buffer.Length);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length));
            lock (_sync)
            {
                if (_disposed && session != 0)
                {
                    bale_gpu_engine_session_close(session, null, 0);
                    session = 0;
                }
                _handle = session;
            }
            return (session != 0 ? 0 : ParseError(text), text);
        }
        catch (Exception ex) { return (ex.HResult, $"Engine session: {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}\n"); }
        finally { self.Free(); }
    }

    /// <summary>The first "Error: 0x........" in an open report, else E_FAIL.</summary>
    private static int ParseError(string report)
    {
        var at = report.IndexOf("Error: 0x", StringComparison.Ordinal);
        return at >= 0 && uint.TryParse(report.AsSpan(at + 9, Math.Min(8, report.Length - at - 9)), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var value) ? unchecked((int)value) : unchecked((int)0x80004005);
    }

    public override void Play()
    {
        Call(handle => bale_gpu_engine_session_play(handle));
        Poll();
    }

    public override void Pause()
    {
        Call(handle => bale_gpu_engine_session_pause(handle));
        Poll();
    }

    public override IReadOnlyList<PlaybackBackendTrack> AudioTracks => _audio.Select(stream => stream.Track).ToArray();

    public override int SelectedAudioIndex => Selected(audio: true);

    public override bool SelectAudio(int index) => Select(audio: true, index);

    public override int VideoTrackCount => _video.Length;

    public override int SelectedVideoIndex => Selected(audio: false);

    public override bool SelectVideo(int index) => Select(audio: false, index);

    private int Selected(bool audio)
    {
        LoadStreams();
        return Array.FindIndex(audio ? _audio : _video, stream => stream.Selected);
    }

    /// <summary>Selects one stream of the list (deselecting its siblings in the engine), then re-reads the selection.</summary>
    private bool Select(bool audio, int index)
    {
        var list = audio ? _audio : _video;
        if (index < 0 || index >= list.Length) return false;
        var engineIndex = list[index].EngineIndex;
        if (Call(handle => bale_gpu_engine_session_select_stream(handle, (uint)engineIndex)) < 0) return false;
        LoadStreams();
        list = audio ? _audio : _video;
        return index < list.Length && list[index].Selected;
    }

    /// <summary>Reads the engine's stream list: audio and video streams in engine order.</summary>
    private unsafe void LoadStreams()
    {
        var audio = new List<EngineStream>();
        var video = new List<EngineStream>();
        lock (_sync)
        {
            if (_handle == 0 || _disposed) return;
            uint count;
            if (bale_gpu_engine_session_stream_count(_handle, &count) < 0) return;
            for (uint i = 0; i < count; i++)
            {
                EngineStreamInfo info;
                if (bale_gpu_engine_session_stream_info(_handle, i, &info) < 0) continue;
                var stream = new EngineStream((int)i, info.Selected != 0, new PlaybackBackendTrack(
                    Text(info.Language, 32), Codec(info.Subtype), info.Channels > 0 ? info.Channels : null, Text(info.Name, 128), Decodable: true));
                if (info.Kind == 1) audio.Add(stream);
                else if (info.Kind == 2) video.Add(stream);
            }
        }
        _audio = [.. audio];
        _video = [.. video];
    }

    private static unsafe string? Text(ushort* text, int capacity)
    {
        var length = 0;
        while (length < capacity && text[length] != 0) length++;
        return length == 0 ? null : new string((char*)text, 0, length);
    }

    /// <summary>A codec name the audio matcher folds (aac, ac3, eac3, dts, pcm, mp3, flac, opus), else the GUID.</summary>
    private static unsafe string? Codec(byte* subtype)
    {
        var guid = new Guid(new ReadOnlySpan<byte>(subtype, 16));
        if (guid == Guid.Empty) return null;
        if (guid == DolbyAc3) return "ac3";
        if (guid == DolbyDdPlus) return "eac3";
        if (guid == DtsHd) return "dts";
        // FourCC/WAVE_FORMAT subtypes share MFAudioFormat's base GUID.
        var bytes = guid.ToByteArray();
        if (bytes.AsSpan(4).SequenceEqual(AudioBase.AsSpan(4)))
        {
            return BitConverter.ToUInt32(bytes, 0) switch
            {
                0x1610 or 0x1600 or 0xFF => "aac",
                0x55 or 0x50 => "mp3",
                0x92 or 0x2000 => "ac3",
                0x8 or 0x2001 => "dts",
                0x1 or 0x3 => "pcm",
                0xF1AC => "flac",
                0x704F => "opus",
                0x6C61 => "alac",
                var tag => "0x" + tag.ToString("X4", CultureInfo.InvariantCulture),
            };
        }
        return guid.ToString("B");
    }

    private static readonly Guid DolbyAc3 = new("e06d802c-db46-11cf-b4d1-00805f6cbbea");
    private static readonly Guid DolbyDdPlus = new("a7fb87af-2d02-42fb-a4d4-05cd93843bdd");
    private static readonly Guid DtsHd = new("a2e58eb7-0fa9-48bb-a40c-fa0e156d0645");
    private static readonly byte[] AudioBase = new Guid("00000000-0000-0010-8000-00aa00389b71").ToByteArray();

    /// <summary>Raises the backend's events from the session's state; also refreshes the position cache.</summary>
    private void Poll()
    {
        EngineSessionState state;
        lock (_sync)
        {
            if (_handle == 0 || _disposed) return;
            if (!TryState(out state)) return;
        }
        var previous = _last;
        _last = state;
        _session.Update(state, previous);
        if (state.Failed && !_failed)
        {
            _failed = true;
            RaiseFailed(new PlaybackBackendFailure(unchecked((int)0x80004005), Decoder: true, Network: false));
            return;
        }
        if (state.Ended && !_ended && _opened)
        {
            _ended = true;
            RaiseEnded();
        }
        else if (!state.Ended) _ended = false;
    }

    private unsafe bool TryState(out EngineSessionState state)
    {
        state = default;
        fixed (EngineSessionState* pointer = &state)
            return bale_gpu_engine_session_state(_handle, pointer) >= 0;
    }

    /// <summary>Runs one session call under the lock; E_POINTER once closed.</summary>
    private int Call(Func<nint, int> call)
    {
        lock (_sync) return _handle == 0 || _disposed ? unchecked((int)0x80004003) : call(_handle);
    }

    /// <summary>
    /// The panel at the screen's raw pixel size, scaled back to view pixels: a SwapChainPanel maps swap chain pixels to
    /// view pixels, so otherwise only the top-left of the engine's raw-pixel swap chain would show. UI thread.
    /// </summary>
    private void Layout()
    {
        var bounds = Window.Current.Bounds;
        var display = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
        var width = display.ScreenWidthInRawPixels;
        var height = display.ScreenHeightInRawPixels;
        if (width == 0 || height == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            width = (uint)Math.Max(1, Math.Round(bounds.Width * display.RawPixelsPerViewPixel));
            height = (uint)Math.Max(1, Math.Round(bounds.Height * display.RawPixelsPerViewPixel));
        }
        _view.Width = width;
        _view.Height = height;
        _view.RenderTransform = new Windows.UI.Xaml.Media.ScaleTransform
        {
            ScaleX = bounds.Width / Math.Max(1, width),
            ScaleY = bounds.Height / Math.Max(1, height),
        };
        Volatile.Write(ref _size, ((long)width << 32) | height);
        Call(handle => bale_gpu_engine_session_resize(handle, width, height));
    }

    private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs e) => Layout();

    private void OnDisplayChanged(Windows.Graphics.Display.DisplayInformation sender, object args) => Layout();

    private void OnHdmiModesChanged(Windows.Graphics.Display.Core.HdmiDisplayInformation sender, object args) =>
        _ = CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
        {
            if (!_disposed) Layout();
        });

    private static (uint Width, uint Height) Unpack(long size) => ((uint)(size >> 32), (uint)(size & 0xFFFFFFFF));

    /// <summary>Called on the engine thread with the swap chain handle: set it on the panel, on the UI thread.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Attach(nint context, nint handle)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not EngineBackend backend || backend._panel == 0) return unchecked((int)0x80004003);
            var hr = unchecked((int)0x8000FFFF);
            CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.High, () =>
            {
                if (backend._panel != 0) hr = bale_gpu_panel_set_swap_chain_handle(backend._panel, handle);
            }).AsTask().Wait(TimeSpan.FromSeconds(5));
            return hr;
        }
        catch (Exception ex) { return ex.HResult; }
    }

    /// <summary>Stops polling, shuts the engine down (before the caller releases the source) and removes the panel. UI thread.</summary>
    public override void Dispose()
    {
        nint handle;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            handle = _handle;
            _handle = 0;
        }
        _poll?.Dispose();
        _poll = null;
        Window.Current.SizeChanged -= OnWindowSizeChanged;
        if (_display is { } display) display.DpiChanged -= OnDisplayChanged;
        _display = null;
        if (_hdmi is { } hdmi) hdmi.DisplayModesChanged -= OnHdmiModesChanged;
        _hdmi = null;
        if (handle != 0)
        {
            try
            {
                unsafe
                {
                    var buffer = new byte[4 * 1024];
                    uint written;
                    fixed (byte* report = buffer) written = bale_gpu_engine_session_close(handle, report, (uint)buffer.Length);
                    var text = System.Text.Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length));
                    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log.Info("engine: close " + line);
                }
            }
            catch (Exception ex) { Log.Info($"engine: close {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}"); }
        }
        if (ReferenceEquals(_host.Child, _canvas)) _host.Child = null;
        if (_panel != 0)
        {
            Marshal.Release(_panel);
            _panel = 0;
        }
    }

    private sealed record EngineStream(int EngineIndex, bool Selected, PlaybackBackendTrack Track);

    /// <summary>The engine's clock and state, from the last poll (position reads the engine live).</summary>
    private sealed class Session(EngineBackend owner) : PlaybackBackendSession
    {
        private double _seekTarget = double.NaN;
        private long _seekIssued;
        private bool _seekSawSeeking;
        private MediaPlaybackState _state = MediaPlaybackState.Opening;

        public bool CanSeekSource { get; set; }

        public override TimeSpan Position
        {
            get
            {
                EngineSessionState state;
                lock (owner._sync)
                {
                    if (owner._handle == 0 || owner._disposed || !owner.TryState(out state)) return TimeSpan.FromSeconds(Math.Max(0, owner._last.Position));
                }
                return TimeSpan.FromSeconds(double.IsFinite(state.Position) ? Math.Max(0, state.Position) : 0);
            }
            set
            {
                var target = Math.Max(0, value.TotalSeconds);
                var current = Position.TotalSeconds;
                // Already there (the open lands at its start): no engine seek, but report it like one.
                if (Math.Abs(current - target) < 0.05)
                {
                    RaiseSeekCompleted();
                    return;
                }
                _seekTarget = target;
                _seekIssued = Environment.TickCount64;
                _seekSawSeeking = false;
                // FFmpeg's sessionless FastSeek (wrapper bale.30) lands on a keyframe either way; other sources may
                // honour the engine's approximate mode, as MediaPlayer's own seeks do.
                var hr = owner.Call(handle => bale_gpu_engine_session_seek(handle, target, 1));
                if (hr < 0)
                {
                    _seekTarget = double.NaN;
                    throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException();
                }
            }
        }

        public override MediaPlaybackState PlaybackState => _state;

        public override bool CanSeek => CanSeekSource && double.IsFinite(owner._last.Duration) && owner._last.Duration > 0;

        public override double PlaybackRate => 1;

        public override uint NaturalVideoWidth => owner._last.Width;

        public override uint NaturalVideoHeight => owner._last.Height;

        public override IReadOnlyList<(double Start, double End)> SeekableRanges() =>
            CanSeek ? [(0, owner._last.Duration)] : [];

        public void Update(EngineSessionState state, EngineSessionState previous)
        {
            if (state.Width != previous.Width || state.Height != previous.Height) RaiseNaturalVideoSizeChanged();
            if (state.Waiting && !previous.Waiting) RaiseBufferingStarted();
            else if (!state.Waiting && previous.Waiting) RaiseBufferingEnded();
            if (!double.IsNaN(_seekTarget))
            {
                if (state.Seeking) _seekSawSeeking = true;
                else if (_seekSawSeeking || Environment.TickCount64 - _seekIssued > 400)
                {
                    _seekTarget = double.NaN;
                    RaiseSeekCompleted();
                }
            }
            var mapped = state.Paused ? MediaPlaybackState.Paused
                : state.Waiting || state.Seeking ? MediaPlaybackState.Buffering
                : MediaPlaybackState.Playing;
            if (mapped != _state)
            {
                _state = mapped;
                RaiseStateChanged();
            }
        }
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
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_engine_session_resize(nint session, uint width, uint height);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern unsafe int bale_gpu_engine_session_state(nint session, EngineSessionState* state);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern unsafe int bale_gpu_engine_session_stream_count(nint session, uint* count);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern unsafe int bale_gpu_engine_session_stream_info(nint session, uint index, EngineStreamInfo* info);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_engine_session_select_stream(nint session, uint index);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern unsafe uint bale_gpu_engine_session_close(nint session, byte* report, uint reportCapacity);
}

/// <summary>engine_session::StreamInfo (C layout).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EngineStreamInfo
{
    public uint Kind;
    public uint Selected;
    public uint Channels;
    public fixed byte Subtype[16];
    public fixed ushort Language[32];
    public fixed ushort Name[128];
}

/// <summary>Live engine state (engine_session::SessionState, repr(C)).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EngineSessionState
{
    public double Position;
    public double Duration;
    public double FramesPerSecond;
    public double Jitter;
    public double FrameDelay;
    public ulong FramesRendered;
    public ulong FramesDropped;
    public ulong FramesCorrupted;
    public uint Width;
    public uint Height;
    public uint Flags;
    public uint ReadyState;

    public readonly bool Paused => (Flags & 1) != 0;
    public readonly bool Ended => (Flags & 2) != 0;
    public readonly bool Failed => (Flags & 4) != 0;
    public readonly bool Seeking => (Flags & 8) != 0;
    public readonly bool Waiting => (Flags & 16) != 0;
}
