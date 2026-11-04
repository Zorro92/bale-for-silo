#if DEBUG_SERVER
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Enumeration;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.Graphics.Display.Core;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Media.Render;

namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    private static int _playtestBusy;

    private static async Task<(string Status, string Body)> PlaytestAsync(string? url, string? seconds)
    {
        if (!DebugPlaytest.TryParse(url, seconds, out var uri, out var duration, out var error))
            return ("400 Bad Request", error + "\n");
        if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)
            return ("409 Conflict", "a playtest is already running\n");

        var builder = new StringBuilder();
        var failures = new ConcurrentQueue<string>();
        var didOpen = false;
        var initialPosition = TimeSpan.Zero;
        var finalPosition = TimeSpan.Zero;
        void Attempt(string step, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                failures.Enqueue($"{step}: {DebugScreenText.Compact(ex.Message, 300)} (0x{unchecked((uint)ex.HResult):X8})");
                Log.Error("Debug playtest: " + step, ex);
            }
        }

        try
        {
            // These breadcrumbs survive a native media failure that never reaches a managed catch.
            Log.Info("Debug playtest: before open");
            using var source = MediaSource.CreateFromUri(uri);
            using var player = new MediaPlayer { IsMuted = true, AutoPlay = false };
            // MediaPlaybackItem/MediaPlaybackSession are not disposable; the player and source are.
            var item = new MediaPlaybackItem(source);
            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new object();
            var observing = true;
            var videoTracks = new Dictionary<VideoTrack, string>();

            void OnOpened(MediaPlayer sender, object args)
            {
                try { opened.TrySetResult(true); }
                catch (Exception ex)
                {
                    failures.Enqueue("MediaOpened handler: " + ex.Message);
                    Log.Error("Debug playtest: MediaOpened", ex);
                }
            }
            void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
            {
                try
                {
                    failures.Enqueue($"MediaFailed: Error={args.Error} ErrorMessage=\"{EscapeText(DebugScreenText.Compact(args.ErrorMessage, 500))}\""
                        + $" ExtendedErrorCode=0x{unchecked((uint)(args.ExtendedErrorCode?.HResult ?? 0)):X8}");
                }
                catch (Exception ex)
                {
                    failures.Enqueue("MediaFailed handler: " + ex.Message);
                    Log.Error("Debug playtest: MediaFailed", ex);
                }
                finally
                {
                    failed.TrySetResult(true);
                    opened.TrySetResult(false);
                }
            }
            void OnVideoOpenFailed(VideoTrack track, VideoTrackOpenFailedEventArgs args)
            {
                try
                {
                    var detail = $"0x{unchecked((uint)(args.ExtendedError?.HResult ?? 0)):X8} {DebugScreenText.Compact(args.ExtendedError?.Message ?? string.Empty, 300)}";
                    lock (gate) videoTracks[track] = detail;
                    failures.Enqueue("VideoTrack.OpenFailed ExtendedError=" + detail);
                }
                catch (Exception ex)
                {
                    failures.Enqueue("VideoTrack.OpenFailed handler: " + ex.Message);
                    Log.Error("Debug playtest: VideoTrack.OpenFailed", ex);
                }
            }
            void WatchVideoTracks()
            {
                lock (gate)
                {
                    if (!observing) return;
                    foreach (var track in item.VideoTracks)
                    {
                        if (!videoTracks.TryAdd(track, string.Empty)) continue;
                        track.OpenFailed += OnVideoOpenFailed;
                    }
                }
            }
            void OnVideoTracksChanged(MediaPlaybackItem sender, IVectorChangedEventArgs args)
            {
                try { WatchVideoTracks(); }
                catch (Exception ex)
                {
                    failures.Enqueue("VideoTracksChanged handler: " + ex.Message);
                    Log.Error("Debug playtest: VideoTracksChanged", ex);
                }
            }

            try
            {
                player.MediaOpened += OnOpened;
                player.MediaFailed += OnFailed;
                // Subscribe before setting Source: tracks can fail before MediaOpened is raised.
                item.VideoTracksChanged += OnVideoTracksChanged;
                WatchVideoTracks();
                player.Source = item;
                if (await Task.WhenAny(opened.Task, Task.Delay(DebugPlaytest.OpenTimeoutMs)) == opened.Task)
                    didOpen = await opened.Task;
                else failures.Enqueue($"open timed out after {DebugPlaytest.OpenTimeoutMs} ms");

                if (didOpen && !failed.Task.IsCompleted)
                {
                    initialPosition = player.PlaybackSession.Position;
                    Log.Info("Debug playtest: before play");
                    player.Play();
                    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(duration)), failed.Task);
                }
            }
            finally
            {
                Attempt("session snapshot", () =>
                {
                    var session = player.PlaybackSession;
                    finalPosition = session.Position;
                    builder.Append(string.Create(CultureInfo.InvariantCulture,
                        $"PlaybackSession: NaturalVideoWidth={session.NaturalVideoWidth} NaturalVideoHeight={session.NaturalVideoHeight}"
                        + $" PlaybackState={session.PlaybackState} Position={finalPosition.TotalSeconds:0.###}s InitialPosition={initialPosition.TotalSeconds:0.###}s\n"));
                });
                Attempt("pause", () => player.Pause());
                KeyValuePair<VideoTrack, string>[] videos;
                lock (gate)
                {
                    observing = false;
                    videos = videoTracks.ToArray();
                }
                Attempt("unsubscribe VideoTracksChanged", () => item.VideoTracksChanged -= OnVideoTracksChanged);
                foreach (var (track, _) in videos)
                    Attempt("unsubscribe VideoTrack.OpenFailed", () => track.OpenFailed -= OnVideoOpenFailed);
                Attempt("unsubscribe MediaOpened", () => player.MediaOpened -= OnOpened);
                Attempt("unsubscribe MediaFailed", () => player.MediaFailed -= OnFailed);
                Attempt("track snapshot", () => AppendPlaytestTracks(builder, item, videos, failures));
                // MediaPlayer has no Stop; Pause + detach stops this isolated probe before disposal.
                Attempt("detach source", () => player.Source = null);
            }
        }
        catch (Exception ex)
        {
            failures.Enqueue($"probe: {DebugScreenText.Compact(ex.Message, 500)} (0x{unchecked((uint)ex.HResult):X8})");
            Log.Error("Debug playtest", ex);
        }
        finally
        {
            Log.Info("Debug playtest: done");
            Interlocked.Exchange(ref _playtestBusy, 0);
        }

        var reasons = failures.ToArray();
        return ("200 OK", $"Opened: {(didOpen ? "yes" : "no")}\n" + builder
            + (reasons.Length == 0 ? string.Empty : "Failures:\n" + string.Join("\n", reasons.Select(reason => "  " + DebugScreenText.Compact(reason, 800))) + "\n")
            + DebugPlaytest.Verdict(didOpen, initialPosition, finalPosition, reasons));
    }

    private static void AppendPlaytestTracks(StringBuilder builder, MediaPlaybackItem item,
        KeyValuePair<VideoTrack, string>[] videos, ConcurrentQueue<string> failures)
    {
        var names = KnownCodecNames();
        builder.Append($"Video tracks: {videos.Length}\n");
        for (var i = 0; i < videos.Length; i++)
        {
            var (track, openError) = videos[i];
            builder.Append($"  Video {i + 1}:");
            if (openError.Length > 0) builder.Append(" OpenFailed.ExtendedError=").Append(openError);
            try
            {
                var format = track.GetEncodingProperties();
                var support = track.SupportInfo;
                builder.Append(string.Create(CultureInfo.InvariantCulture,
                    $" Subtype={names.Describe(format.Subtype)} {format.Width}x{format.Height}"
                    + $" FrameRate={format.FrameRate.Numerator}/{format.FrameRate.Denominator} Bitrate={format.Bitrate}"
                    + $" DecoderStatus={support.DecoderStatus} MediaSourceStatus={support.MediaSourceStatus}\n"));
                if (support.DecoderStatus != MediaDecoderStatus.FullySupported)
                    failures.Enqueue($"Video {i + 1} DecoderStatus={support.DecoderStatus}");
            }
            catch (Exception ex)
            {
                builder.Append(" error: ").Append(DebugScreenText.Compact(ex.Message, 300)).Append('\n');
                failures.Enqueue($"could not inspect Video {i + 1}: {ex.Message}");
            }
        }
        builder.Append($"Audio tracks: {item.AudioTracks.Count}\n");
        for (var i = 0; i < item.AudioTracks.Count; i++)
        {
            builder.Append($"  Audio {i + 1}:");
            try
            {
                var track = item.AudioTracks[i];
                var format = track.GetEncodingProperties();
                var support = track.SupportInfo;
                builder.Append($" Subtype={names.Describe(format.Subtype)} ChannelCount={format.ChannelCount} SampleRate={format.SampleRate}"
                    + $" DecoderStatus={support.DecoderStatus} DegradationReason={support.DegradationReason}\n");
                if (support.DecoderStatus != MediaDecoderStatus.FullySupported)
                    failures.Enqueue($"Audio {i + 1} DecoderStatus={support.DecoderStatus}");
            }
            catch (Exception ex)
            {
                builder.Append(" error: ").Append(DebugScreenText.Compact(ex.Message, 300)).Append('\n');
                failures.Enqueue($"could not inspect Audio {i + 1}: {ex.Message}");
            }
        }
    }

    // only=video|gpu|audio|display|device|graph runs one section; each logs a breadcrumb first because
    // a native failure in a media API ends the app without reaching any catch.
    private static async Task<string> DescribeCapabilitiesAsync(string? only)
    {
        bool Run(string section)
        {
            if (!string.IsNullOrEmpty(only) && !string.Equals(only, section, StringComparison.OrdinalIgnoreCase)) return false;
            Log.Info($"Debug caps: {section}");
            return true;
        }
        var builder = new StringBuilder("Decoders (installed; not a guarantee of playback/profile support or passthrough):\n");
        if (Run("video")) await AppendVideoDecodersBySubtypeAsync(builder);
        if (Run("gpu")) AppendGpuCapabilities(builder);
        if (Run("audio")) await AppendDecodersAsync(builder, CodecKind.Audio, ["AAC", "AC3", "EAC3", "DTS", "DTS-HD", "TrueHD/MLP", "FLAC", "ALAC", "Opus", "MP3", "PCM"]);
        // These view-scoped display APIs must run on the UI thread, unlike codec/device enumeration.
        if (Run("display")) builder.Append("Display:\n").Append(await OnUiAsync(DescribeDisplayCapabilities));
        builder.Append("Audio:\n");
        await AppendAudioCapabilitiesAsync(builder, Run("device"), Run("graph"));
        Log.Info("Debug caps: done");
        return builder.ToString();
    }

    // GUIDs, descriptor layout and ID3D11VideoDevice slots match the Windows SDK's d3d11.h
    // (microsoft/win32metadata, generation/WinSDK/RecompiledIdlHeaders/um/d3d11.h).
    private static readonly Dictionary<Guid, string> GpuDecoderProfileNames = new()
    {
        [new Guid("1b81be68-a0c7-11d3-b984-00c04f2e73c5")] = "H264_VLD_NOFGT",
        [new Guid("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0")] = "HEVC_VLD_MAIN",
        [new Guid("107af0e0-ef1a-4d19-aba8-67a163073d13")] = "HEVC_VLD_MAIN10",
        [new Guid("463707f8-a1d0-4585-876d-83aa6d60b89e")] = "VP9_VLD_PROFILE0",
        [new Guid("a4c749ef-6ecf-48aa-8448-50a7a1165ff7")] = "VP9_VLD_10BIT_PROFILE2",
        [new Guid("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a")] = "AV1_VLD_PROFILE0",
        [new Guid("6936ff0f-45b1-4163-9cc1-646ef6946108")] = "AV1_VLD_PROFILE1",
        [new Guid("0c5f2aa1-e541-4089-bb7b-98110a19d7c8")] = "AV1_VLD_PROFILE2",
        [new Guid("90b899ea-3a62-4705-88b3-8df04b2744e7")] = "VP8_VLD",
        [new Guid("ee27417f-5e28-4e65-beea-1d26b508adc9")] = "MPEG2_VLD",
        [new Guid("86695f12-340e-4f04-9fd3-9253dd327460")] = "MPEG2and1_VLD",
        [new Guid("1b81bea4-a0c7-11d3-b984-00c04f2e73c5")] = "VC1_D2010",
    };

    private static readonly (string Name, int Format)[] GpuDecoderOutputFormats =
    [
        ("NV12", 103),
        ("P010", 104),
    ];

    private static readonly (string Name, uint Width, uint Height)[] GpuDecoderSizes =
    [
        ("1080p", 1920, 1080),
        ("4K", 3840, 2160),
        ("8K", 7680, 4320),
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11VideoDecoderDesc
    {
        public Guid Guid;
        public uint SampleWidth;
        public uint SampleHeight;
        public int OutputFormat;
    }

    // Blittable arguments and raw COM calls avoid runtime COM marshalling under Native AOT.
    [DllImport("d3d11.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    private static unsafe void AppendGpuCapabilities(StringBuilder builder)
    {
        var device = IntPtr.Zero;
        var context = IntPtr.Zero;
        var videoDevice = IntPtr.Zero;
        try
        {
            builder.Append("GPU (D3D11 hardware video decode; independent of installed decoders):\n");
            Log.Info("Debug caps: gpu before device creation");
            // HARDWARE=1, VIDEO_SUPPORT=0x800, SDK_VERSION=7; never fall back to software.
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x800,
                IntPtr.Zero, 0, 7, out device, out var featureLevel, out context));
            builder.Append($"  Feature level: 0x{featureLevel:X4}\n");
            var videoDeviceId = new Guid("10ec4d5b-975a-4689-b9e4-d0aac30fe333");
            var deviceVtable = *(void***)device;
            var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)deviceVtable[0];
            Marshal.ThrowExceptionForHR(queryInterface(device, &videoDeviceId, &videoDevice));

            var videoVtable = *(void***)videoDevice;
            // ID3D11VideoDevice derives directly from IUnknown: profile queries occupy slots 11–14.
            var getProfileCount = (delegate* unmanaged[Stdcall]<IntPtr, uint>)videoVtable[11];
            var getProfile = (delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, int>)videoVtable[12];
            var checkFormat = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, int*, int>)videoVtable[13];
            var getConfigCount = (delegate* unmanaged[Stdcall]<IntPtr, D3D11VideoDecoderDesc*, uint*, int>)videoVtable[14];
            var profileCount = getProfileCount(videoDevice);
            builder.Append($"  Decoder profiles: {profileCount}\n");
            Log.Info($"Debug caps: gpu before profile loop ({profileCount} profiles)");
            for (uint index = 0; index < profileCount; index++)
            {
                var profile = Guid.Empty;
                Marshal.ThrowExceptionForHR(getProfile(videoDevice, index, &profile));
                var line = new StringBuilder("  ")
                    .Append(GpuDecoderProfileNames.TryGetValue(profile, out var name) ? name : profile.ToString())
                    .Append(": ");
                var hasFormat = false;
                foreach (var (formatName, format) in GpuDecoderOutputFormats)
                {
                    var supported = 0;
                    Marshal.ThrowExceptionForHR(checkFormat(videoDevice, &profile, format, &supported));
                    if (supported == 0) continue;
                    if (hasFormat) line.Append("; ");
                    line.Append(formatName);
                    hasFormat = true;
                    foreach (var (sizeName, width, height) in GpuDecoderSizes)
                    {
                        var desc = new D3D11VideoDecoderDesc
                        {
                            Guid = profile,
                            SampleWidth = width,
                            SampleHeight = height,
                            OutputFormat = format,
                        };
                        uint count = 0;
                        // A format can be supported while a particular size has no valid configuration.
                        if (getConfigCount(videoDevice, &desc, &count) >= 0 && count > 0)
                            line.Append(' ').Append(sizeName);
                    }
                }
                if (!hasFormat) line.Append("(no output formats)");
                builder.Append(line).Append('\n');
            }
        }
        catch (Exception ex)
        {
            builder.Append($"gpu error: 0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally
        {
            ReleaseGpuCom(videoDevice);
            ReleaseGpuCom(context);
            ReleaseGpuCom(device);
        }
    }

    private static unsafe void ReleaseGpuCom(IntPtr instance)
    {
        if (instance == IntPtr.Zero) return;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)instance)[2];
        release(instance);
    }

    private static DebugCodecNames KnownCodecNames()
    {
        return new DebugCodecNames(
        [
            (CodecSubtypes.VideoFormatH264, "H264"),
            (CodecSubtypes.VideoFormatH264ES, "H264"),
            (CodecSubtypes.VideoFormatH265, "HEVC"),
            (CodecSubtypes.VideoFormatHevc, "HEVC"),
            (CodecSubtypes.VideoFormatHevcES, "HEVC"),
            (CodecSubtypes.VideoFormatVP80, "VP8"),
            (CodecSubtypes.VideoFormatVP90, "VP9"),
            // The UWP projection lacks an AV1 property; a runtime check cannot guard a missing
            // compile-time member. Use the Media Foundation AV01 subtype GUID directly.
            ("31305641-0000-0010-8000-00aa00389b71", "AV1"),
            (CodecSubtypes.VideoFormatMpeg2, "MPEG2"),
            (CodecSubtypes.VideoFormatWvc1, "VC1"),
            (CodecSubtypes.VideoFormatWmv3, "VC1"),
            (CodecSubtypes.AudioFormatAac, "AAC"),
            (CodecSubtypes.AudioFormatAdts, "AAC"),
            (CodecSubtypes.AudioFormatDolbyAC3, "AC3"),
            (CodecSubtypes.AudioFormatDolbyDDPlus, "EAC3"),
            (CodecSubtypes.AudioFormatDts, "DTS"),
            (CodecSubtypes.AudioFormatFlac, "FLAC"),
            (CodecSubtypes.AudioFormatAlac, "ALAC"),
            (CodecSubtypes.AudioFormatOpus, "Opus"),
            (CodecSubtypes.AudioFormatMP3, "MP3"),
            (CodecSubtypes.AudioFormatPcm, "PCM"),
            (CodecSubtypes.AudioFormatFloat, "PCM"),
            // Raw stream aliases in the Windows SDK's wmcodecdsp.h / mfapi.h. Deliberately do
            // not map IEC61937/S/PDIF output subtypes to decoder support for these codecs.
            ("e06d8033-db46-11cf-b4d1-00805f6cbbea", "DTS"),
            ("00002001-0000-0010-8000-00aa00389b71", "DTS"),
            ("a2e58eb7-0fa9-48bb-a40c-fa0e156d0645", "DTS-HD"),
            ("a61ac364-ad0e-4744-89ff-213ce0df8804", "DTS-HD"),
            ("eb27cec4-163e-4ca3-8b74-8e25f91b517e", "TrueHD/MLP"),
            // MLP FourCC, also used by LAVFilters common/includes/moreuuids.h (no SDK property).
            ("20504c4d-0000-0010-8000-00aa00389b71", "TrueHD/MLP"),
        ]);
    }

    // On the Xbox, an unfiltered video CodecQuery ends the app natively before it returns, so
    // video asks for one subtype at a time (each logged first, to name the one that fails).
    private static readonly (string Name, string Subtype)[] VideoSubtypes =
    [
        ("H264", CodecSubtypes.VideoFormatH264),
        ("HEVC", CodecSubtypes.VideoFormatHevc),
        ("VP8", CodecSubtypes.VideoFormatVP80),
        ("VP9", CodecSubtypes.VideoFormatVP90),
        ("AV1", "31305641-0000-0010-8000-00aa00389b71"),
        ("MPEG2", CodecSubtypes.VideoFormatMpeg2),
        ("VC1", CodecSubtypes.VideoFormatWvc1),
        ("DolbyVision dvhe", "65687664-0000-0010-8000-00aa00389b71"), // FourCC literals: CI's projection may predate these
        ("DolbyVision dvh1", "31687664-0000-0010-8000-00aa00389b71"),
    ];

    private static async Task AppendVideoDecodersBySubtypeAsync(StringBuilder builder)
    {
        builder.Append("  Video (queried per subtype):\n");
        foreach (var (name, subtype) in VideoSubtypes)
        {
            try
            {
                Log.Info($"Debug caps: video {name}");
                var codecs = await new CodecQuery().FindAllAsync(CodecKind.Video, CodecCategory.Decoder, subtype).AsTask();
                builder.Append($"    {name}: {(codecs.Count == 0 ? "no decoder" : string.Join("; ", codecs.Select(c => $"{EscapeText(DebugScreenText.Compact(c.DisplayName, 120))} IsTrusted={c.IsTrusted}")))}\n");
            }
            catch (Exception ex)
            {
                builder.Append($"    {name}: error: {DebugScreenText.Compact(ex.Message, 300)}\n");
            }
        }
    }

    private static async Task AppendDecodersAsync(StringBuilder builder, CodecKind kind, string[] codecNames)
    {
        try
        {
            var codecs = await new CodecQuery().FindAllAsync(kind, CodecCategory.Decoder, string.Empty).AsTask();
            Log.Info($"Debug caps: {kind} query returned {codecs.Count}");
            var names = KnownCodecNames();
            var observed = new List<(string DisplayName, IEnumerable<string> Subtypes)>();
            builder.Append($"  {kind}: {codecs.Count} decoders\n");
            foreach (var codec in codecs)
            {
                Log.Info($"Debug caps: {kind} codec {codec.DisplayName}");
                var supported = codec.Subtypes.ToList();
                observed.Add((codec.DisplayName, supported));
                builder.Append($"    {EscapeText(DebugScreenText.Compact(codec.DisplayName, 240))} IsTrusted={codec.IsTrusted}\n")
                    .Append("      Subtypes: ").Append(supported.Count == 0 ? "none" : string.Join(", ", supported.Select(names.Describe))).Append('\n');
            }
            builder.Append(names.Summary(observed, codecNames));
        }
        catch (Exception ex)
        {
            builder.Append($"  {kind}: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
    }

    private static string DescribeDisplayCapabilities()
    {
        var builder = new StringBuilder();
        try
        {
            var hdmi = HdmiDisplayInformation.GetForCurrentView();
            if (hdmi is null) builder.Append("  HDMI: unavailable for this view\n");
            else
            {
                var current = hdmi.GetCurrentDisplayMode();
                builder.Append("  HDMI current: ").Append(current is null ? "none" : DescribeDisplayMode(current)).Append('\n');
                var modes = hdmi.GetSupportedDisplayModes().Select(DescribeDisplayMode).Distinct(StringComparer.Ordinal).ToList();
                builder.Append($"  HDMI supported: {modes.Count} distinct modes\n");
                foreach (var mode in modes) builder.Append("    ").Append(mode).Append('\n');
            }
        }
        catch (Exception ex)
        {
            builder.Append($"  HDMI: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
        try
        {
            if (!ApiInformation.IsMethodPresent("Windows.Graphics.Display.DisplayInformation", "GetAdvancedColorInfo"))
                builder.Append("  Advanced color: unavailable\n");
            else
            {
                var color = DisplayInformation.GetForCurrentView().GetAdvancedColorInfo();
                if (color is null) builder.Append("  Advanced color: unavailable for this view\n");
                else builder.Append(string.Create(CultureInfo.InvariantCulture,$"  Advanced color: current={color.CurrentAdvancedColorKind}"
                    + $" HDR={color.IsAdvancedColorKindAvailable(AdvancedColorKind.HighDynamicRange)}"
                    + $" WideColorGamut={color.IsAdvancedColorKindAvailable(AdvancedColorKind.WideColorGamut)}"
                    + $" SdrWhiteLevelInNits={color.SdrWhiteLevelInNits:0.###}"
                    + $" MinLuminanceInNits={color.MinLuminanceInNits:0.###} MaxLuminanceInNits={color.MaxLuminanceInNits:0.###}"
                    + $" MaxAverageFullFrameLuminanceInNits={color.MaxAverageFullFrameLuminanceInNits:0.###}\n"));
            }
        }
        catch (Exception ex)
        {
            builder.Append($"  Advanced color: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
        return builder.ToString();
    }

    private static string DescribeDisplayMode(HdmiDisplayMode mode)
    {
        var dolbyVision = ApiInformation.IsPropertyPresent("Windows.Graphics.Display.Core.HdmiDisplayMode", "IsDolbyVisionLowLatencySupported")
            ? mode.IsDolbyVisionLowLatencySupported.ToString() : "unavailable";
        return string.Create(CultureInfo.InvariantCulture,$"{mode.ResolutionWidthInRawPixels}x{mode.ResolutionHeightInRawPixels} @{mode.RefreshRate:0.###}Hz"
            + $" BitsPerPixel={mode.BitsPerPixel} ColorSpace={mode.ColorSpace} PixelEncoding={mode.PixelEncoding}"
            + $" IsSmpte2084Supported={mode.IsSmpte2084Supported} Is2086MetadataSupported={mode.Is2086MetadataSupported}"
            + $" IsDolbyVisionLowLatencySupported={dolbyVision}");
    }

    private static async Task AppendAudioCapabilitiesAsync(StringBuilder builder, bool runDevice, bool runGraph)
    {
        if (runDevice) try
        {
            var id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (string.IsNullOrEmpty(id)) builder.Append("  Default render: none\n");
            else
            {
                var device = await DeviceInformation.CreateFromIdAsync(id).AsTask();
                builder.Append($"  Default render: {EscapeText(DebugScreenText.Compact(device.Name, 240))}\n");
            }
        }
        catch (Exception ex)
        {
            builder.Append($"  Default render: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
        if (runGraph) try
        {
            // Read the default graph format without starting playback or changing audio routing.
            var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Media)).AsTask();
            using var graph = created.Graph;
            if (created.Status != AudioGraphCreationStatus.Success || graph is null)
                builder.Append($"  Graph mix: unavailable ({created.Status})\n");
            else
            {
                var format = graph.EncodingProperties;
                builder.Append($"  Graph mix (default settings): channels={format.ChannelCount} sampleRate={format.SampleRate}Hz bitsPerSample={format.BitsPerSample} subtype={format.Subtype}\n");
            }
        }
        catch (Exception ex)
        {
            builder.Append($"  Graph mix: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
        try
        {
            var baseline = XboxAudioCapabilities.Current;
            builder.Append("  XboxAudioCapabilities baseline: decode=").Append(string.Join(", ", baseline.DecodeCodecs))
                .Append("; passthrough=").Append(baseline.Passthrough is { } passthrough ? string.Join(", ", passthrough.Codecs) : "unclaimed").Append('\n');
        }
        catch (Exception ex)
        {
            builder.Append($"  XboxAudioCapabilities baseline: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
        builder.Append("  Decoder enumeration and graph mix do not prove encoded passthrough or output-channel limits.\n");
    }
}
#endif
