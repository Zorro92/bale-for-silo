#if DEBUG_SERVER
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
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
using Windows.Media.Protection;
using Windows.Media.Render;
using Windows.System;

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

    // only=video|gpu|audio|display|platform|device|graph runs one section; each logs a breadcrumb first because
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
        if (Run("platform")) await AppendPlatformCapabilitiesAsync(builder);
        builder.Append("Audio:\n");
        await AppendAudioCapabilitiesAsync(builder, Run("device"), Run("graph"));
        Log.Info("Debug caps: done");
        return builder.ToString();
    }

    // Windows SDK winnt.h (microsoft/win32metadata, generation/WinSDK/RecompiledIdlHeaders/um).
    private static readonly (string Name, uint Feature)[] PlatformProcessorFeatures =
    [
        ("SSE4.1", 37), // PF_SSE4_1_INSTRUCTIONS_AVAILABLE
        ("SSE4.2", 38), // PF_SSE4_2_INSTRUCTIONS_AVAILABLE
        ("AVX", 39), // PF_AVX_INSTRUCTIONS_AVAILABLE
        ("AVX2", 40), // PF_AVX2_INSTRUCTIONS_AVAILABLE
        ("AVX512F", 41), // PF_AVX512F_INSTRUCTIONS_AVAILABLE
    ];

    [LibraryImport("kernel32.dll")]
    private static partial int IsProcessorFeaturePresent(uint processorFeature); // Win32 BOOL, not managed bool.

    private static async Task AppendPlatformCapabilitiesAsync(StringBuilder builder)
    {
        builder.Append("Platform:\n  CPU:");
        foreach (var (name, feature) in PlatformProcessorFeatures)
            builder.Append($" {name}={ReadPlatformCapability(() => IsProcessorFeaturePresent(feature) != 0 ? "yes" : "no")}");
        builder.Append($" (runtime Avx2={ReadPlatformCapability(() => Avx2.IsSupported ? "yes" : "no")}"
            + $" Avx512F={ReadPlatformCapability(() => Avx512F.IsSupported ? "yes" : "no")})\n");
        builder.Append($"  DeviceForm={ReadPlatformCapability(() => Windows.System.Profile.AnalyticsInfo.DeviceForm)}"
            + $" SoftwareVideo={ReadPlatformCapability(() => DeviceIdentity.SoftwareVideoSupported ? "yes" : "no")}\n");

        Log.Info("Debug caps: platform before MemoryManager queries");
        builder.Append("  Memory: AppMemoryUsage=")
            .Append(ReadPlatformCapability(() => $"{MemoryManager.AppMemoryUsage / (1024 * 1024)} MiB"))
            .Append(" AppMemoryUsageLimit=")
            .Append(ReadPlatformCapability(() => $"{MemoryManager.AppMemoryUsageLimit / (1024 * 1024)} MiB"))
            .Append(" ExpectedAppMemoryUsageLimit=")
            .Append(ReadPlatformCapability(() => $"{MemoryManager.ExpectedAppMemoryUsageLimit / (1024 * 1024)} MiB"))
            .Append('\n');
        AppendPlatformExtendedMemory(builder);
        await AppendPlatformProtectionCapabilitiesAsync(builder);
    }

    private static string ReadPlatformCapability(Func<string> query)
    {
        try { return query(); }
        catch (Exception ex) { return PlatformUnavailable(ex); }
    }

    // Never include exception messages: capability reports must not expose URLs or grants.
    private static string PlatformUnavailable(Exception ex) =>
        DebugScreenText.Compact($"unavailable (0x{unchecked((uint)ex.HResult):X8})", 120);

    // Microsoft hevc-xbox.md, Memory usage information: IApplicationResourcesStatics2 derives
    // from IInspectable (six inherited slots); extended memory is the Series X|S graphics partition.
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedMemoryInfo
    {
        public ulong AppMemoryUsage;
        public ulong AppMemoryLimit;
        public ulong ExtendedMemoryUsage;
        public ulong ExtendedMemoryLimit;
    }

    // Pointer-only signatures keep the HSTRING/COM ABI blittable with DisableRuntimeMarshalling.
    [LibraryImport("combase.dll")]
    private static unsafe partial int WindowsCreateString(char* sourceString, uint length, nint* hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static unsafe partial int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);

    private static unsafe void AppendPlatformExtendedMemory(StringBuilder builder)
    {
        nint classId = 0;
        nint factory = 0;
        nint resources = 0;
        try
        {
            const string runtimeClass = "Windows.Xbox.ApplicationModel.ApplicationResources";
            Log.Info("Debug caps: platform before WindowsCreateString");
            fixed (char* name = runtimeClass)
                Marshal.ThrowExceptionForHR(WindowsCreateString(name, (uint)runtimeClass.Length, &classId));
            var inspectableId = new Guid("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90"); // IID_IInspectable
            Log.Info("Debug caps: platform before RoGetActivationFactory (ApplicationResources)");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, &inspectableId, &factory));
            var resourcesId = new Guid("dd36a017-b640-45f7-a023-1615cf098923");
            var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)factory)[0];
            Log.Info("Debug caps: platform before QueryInterface (IApplicationResourcesStatics2)");
            Marshal.ThrowExceptionForHR(queryInterface(factory, &resourcesId, &resources));
            var getExtendedMemoryInfo = (delegate* unmanaged[Stdcall]<nint, ExtendedMemoryInfo*, int>)(*(void***)resources)[6];
            var memory = default(ExtendedMemoryInfo);
            Log.Info("Debug caps: platform before GetExtendedMemoryInfo");
            Marshal.ThrowExceptionForHR(getExtendedMemoryInfo(resources, &memory));
            builder.Append(string.Create(CultureInfo.InvariantCulture,
                $"  Extended memory: app={memory.AppMemoryUsage / (1024 * 1024)}/{memory.AppMemoryLimit / (1024 * 1024)} MiB"
                + $" graphics={memory.ExtendedMemoryUsage / (1024 * 1024)}/{memory.ExtendedMemoryLimit / (1024 * 1024)} MiB\n"));
        }
        catch (Exception ex)
        {
            builder.Append($"  Extended memory: {PlatformUnavailable(ex)}\n");
        }
        finally
        {
            ReleasePlatformCom(builder, resources);
            ReleasePlatformCom(builder, factory);
            if (classId != 0) try
            {
                Log.Info("Debug caps: platform before WindowsDeleteString");
                Marshal.ThrowExceptionForHR(WindowsDeleteString(classId));
            }
            catch (Exception ex)
            {
                builder.Append($"  Extended memory HSTRING release: {PlatformUnavailable(ex)}\n");
            }
        }
    }

    private static unsafe void ReleasePlatformCom(StringBuilder builder, nint instance)
    {
        if (instance == 0) return;
        try
        {
            Log.Info("Debug caps: platform before COM release");
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)instance)[2];
            release(instance);
        }
        catch (Exception ex)
        {
            builder.Append($"  Extended memory COM release: {PlatformUnavailable(ex)}\n");
        }
    }

    // Microsoft hevc-xbox.md, Capability checks. HDCP must be last because it is slow.
    // AV1 is informational: PlayReady hardware is expected to report NotSupported for this query.
    private static readonly (string Name, string Type)[] PlatformProtectionTypes =
    [
        ("HEVC 4K decode", "video/mp4;codecs=\"hvc1,mp4a\";features=\"decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=30,decode-bpc=10\""),
        ("HEVC 4K decode+display", "video/mp4;codecs=\"hvc1,mp4a\";features=\"decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=30,decode-bpc=10,display-res-x=3840,display-res-y=2160,display-bpc=8\""),
        ("HEVC 4K HDR", "video/mp4;codecs=\"hvc1,mp4a\";features=\"decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=30,decode-bpc=10,display-res-x=3840,display-res-y=2160,display-bpc=10,hdr=1\""),
        ("HEVC 4K60 HDR", "video/mp4;codecs=\"hvc1,mp4a\";features=\"decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=60,decode-bpc=10,display-res-x=3840,display-res-y=2160,display-bpc=10,hdr=1\""),
        ("AV1 4K HDR", "video/mp4;codecs=\"av01,mp4a\";features=\"decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=30,decode-bpc=10,display-res-x=3840,display-res-y=2160,display-bpc=10,hdr=1\""),
        ("VP9 4K HDR", "video/mp4;codecs=\"vp09,mp4a\";features=\"decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=30,decode-bpc=10,display-res-x=3840,display-res-y=2160,display-bpc=10,hdr=1\""),
        ("HDCP 2.2", "video/mp4;codecs=\"hvc1,mp4a\";features=\"hdcp=2\""),
    ];

    private static async Task AppendPlatformProtectionCapabilitiesAsync(StringBuilder builder)
    {
        builder.Append("  Protection capabilities (PlayReady hardware):\n");
        ProtectionCapabilities protection;
        try
        {
            Log.Info("Debug caps: platform before ProtectionCapabilities activation");
            protection = new ProtectionCapabilities();
        }
        catch (Exception ex)
        {
            builder.Append($"    {PlatformUnavailable(ex)}\n");
            return;
        }
        foreach (var (name, type) in PlatformProtectionTypes)
        {
            var attempts = 0;
            try
            {
                var result = ProtectionCapabilityResult.Maybe;
                // IsTypeSupported guidance: Maybe can mean the result is still being computed.
                // Bound each query to ten attempts, with no delay after its final result.
                while (attempts < 10)
                {
                    attempts++;
                    Log.Info($"Debug caps: platform before {name} (attempt {attempts})");
                    result = protection.IsTypeSupported(type, "com.microsoft.playready.hardware");
                    if (result != ProtectionCapabilityResult.Maybe || attempts == 10) break;
                    await Task.Delay(100);
                }
                builder.Append($"    {name}: {result}");
            }
            catch (Exception ex)
            {
                builder.Append($"    {name}: {PlatformUnavailable(ex)}");
            }
            if (attempts > 1) builder.Append($" ({attempts} attempts)");
            builder.Append('\n');
        }
    }

    // GUIDs, descriptor layout and ID3D11VideoDevice slots match the Windows SDK's d3d11.h
    // (microsoft/win32metadata, generation/WinSDK/RecompiledIdlHeaders/um/d3d11.h).
    private static readonly Dictionary<Guid, string> GpuDecoderProfileNames = new()
    {
        [new Guid("1b81be68-a0c7-11d3-b984-00c04f2e73c5")] = "H264_VLD_NOFGT",
        [new Guid("705b9d82-76cf-49d6-b7e6-ac8872db013c")] = "H264_VLD_MULTIVIEW_NOFGT",
        [new Guid("efd64d74-c9e8-41d7-a5e9-e9b0e39fa319")] = "MPEG4PT2_VLD_SIMPLE",
        [new Guid("1b81bea3-a0c7-11d3-b984-00c04f2e73c5")] = "VC1_VLD",
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

    // Pointer outputs avoid by-ref marshalling, which DisableRuntimeMarshalling disallows.
    [DllImport("d3d11.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern unsafe int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        IntPtr* device, int* featureLevel, IntPtr* immediateContext);

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
            var featureLevel = 0;
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x800,
                IntPtr.Zero, 0, 7, &device, &featureLevel, &context));
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
        // App-mode D3D12 availability is independent of D3D11 video-device support.
        AppendD3D12GpuCapabilities(builder);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GamingDeviceModelInformation
    {
        public uint VendorId;
        public uint DeviceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuLuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DxgiAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public GpuLuid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct D3D12FeatureDataFeatureLevels
    {
        public uint NumFeatureLevels;
        public int* FeatureLevelsRequested;
        public int MaxSupportedFeatureLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D12FeatureDataShaderModel
    {
        public int HighestShaderModel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D12CommandQueueDesc
    {
        public int Type;
        public int Priority;
        public uint Flags;
        public uint NodeMask;
    }

    private sealed record GpuAdapter(uint Index, IntPtr Instance, string Description, uint Flags, GpuLuid Luid)
    {
        public bool IsSoftware => (Flags & 2) != 0;
    }

    private static readonly Guid D3D12DeviceId = new("189819f1-1db6-4b57-be54-1821339b85f7");

    private static void AppendD3D12GpuCapabilities(StringBuilder builder)
    {
        builder.Append("GPU (D3D12, app-mode availability):\n");
        AppendGpuGameMode(builder);
        AppendGpuConsoleModel(builder);
        var adapters = new List<GpuAdapter>();
        var dxgiLibrary = IntPtr.Zero;
        try
        {
            AppendGpuAdapters(builder, adapters, out dxgiLibrary);
            AppendD3D12Devices(builder, adapters);
        }
        finally
        {
            foreach (var adapter in adapters) ReleaseGpuCom(adapter.Instance);
            // Keep DXGI loaded until all adapter COM references have been released.
            FreeGpuLibrary(dxgiLibrary, "dxgi.dll");
        }
    }

    private static bool TryLoadGpuExport(StringBuilder builder, string libraryName, string exportName,
        out IntPtr library, out IntPtr export)
    {
        library = IntPtr.Zero;
        export = IntPtr.Zero;
        Log.Info($"Debug caps: gpu before loading {libraryName}");
        if (!NativeLibrary.TryLoad(libraryName, out library))
        {
            builder.Append($"  {exportName}: error ({libraryName} missing)\n");
            return false;
        }
        Log.Info($"Debug caps: gpu before resolving {exportName}");
        if (NativeLibrary.TryGetExport(library, exportName, out export)) return true;
        builder.Append($"  {exportName}: error (export missing)\n");
        return false;
    }

    private static unsafe void AppendGpuGameMode(StringBuilder builder)
    {
        var library = IntPtr.Zero;
        try
        {
            if (!TryLoadGpuExport(builder, "api-ms-win-gaming-expandedresources-l1-1-0.dll",
                "HasExpandedResources", out library, out var export)) return;
            var hasExpandedResources = (delegate* unmanaged[Stdcall]<int*, int>)export;
            var expanded = 0; // Win32 BOOL, not a marshalled managed bool.
            Log.Info("Debug caps: gpu before HasExpandedResources");
            Marshal.ThrowExceptionForHR(hasExpandedResources(&expanded));
            builder.Append($"  HasExpandedResources (game mode): {(expanded != 0 ? "yes" : "no")}\n");
        }
        catch (Exception ex)
        {
            builder.Append($"  HasExpandedResources: error: 0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally
        {
            FreeGpuLibrary(library, "api-ms-win-gaming-expandedresources-l1-1-0.dll");
        }
    }

    private static unsafe void AppendGpuConsoleModel(StringBuilder builder)
    {
        var library = IntPtr.Zero;
        try
        {
            if (!TryLoadGpuExport(builder, "api-ms-win-gaming-deviceinformation-l1-1-0.dll",
                "GetGamingDeviceModelInformation", out library, out var export)) return;
            var getModel = (delegate* unmanaged[Stdcall]<GamingDeviceModelInformation*, int>)export;
            var model = default(GamingDeviceModelInformation);
            Log.Info("Debug caps: gpu before GetGamingDeviceModelInformation");
            Marshal.ThrowExceptionForHR(getModel(&model));
            var name = model.DeviceId switch
            {
                0x1D27FABB => "Xbox Series S",
                0x2F7A3DFF => "Xbox Series X",
                _ => "other",
            };
            builder.Append($"  Console model: VendorId=0x{model.VendorId:X8} DeviceId=0x{model.DeviceId:X8} ({name})\n");
        }
        catch (Exception ex)
        {
            builder.Append($"  Console model: error: 0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally
        {
            FreeGpuLibrary(library, "api-ms-win-gaming-deviceinformation-l1-1-0.dll");
        }
    }

    private static unsafe void AppendGpuAdapters(StringBuilder builder, List<GpuAdapter> adapters, out IntPtr library)
    {
        library = IntPtr.Zero;
        var factory = IntPtr.Zero;
        try
        {
            if (!TryLoadGpuExport(builder, "dxgi.dll", "CreateDXGIFactory2", out library, out var export)) return;
            var createFactory = (delegate* unmanaged[Stdcall]<uint, Guid*, IntPtr*, int>)export;
            var factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IID_IDXGIFactory1
            Log.Info("Debug caps: gpu before CreateDXGIFactory2 (IDXGIFactory1)");
            Marshal.ThrowExceptionForHR(createFactory(0, &factoryId, &factory));
            // Windows SDK dxgi.h declaration order: IUnknown(3) + IDXGIObject(4) = 7.
            // IDXGIFactory's five methods occupy 7–11; IDXGIFactory1::EnumAdapters1 = 7 + 5 = 12.
            // IDXGIAdapter's EnumOutputs/GetDesc/CheckInterfaceSupport occupy 7–9;
            // IDXGIAdapter1::GetDesc1 = 7 + 3 = 10.
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)factory)[12];
            builder.Append("  DXGI adapters (memory in bytes; Flags SOFTWARE=2):\n");
            for (uint index = 0; ; index++)
            {
                var adapter = IntPtr.Zero;
                try
                {
                    Log.Info($"Debug caps: gpu before EnumAdapters1 ({index})");
                    var hr = enumAdapters(factory, index, &adapter);
                    if (hr == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                    if (hr < 0)
                    {
                        builder.Append($"    EnumAdapters1 ({index}): error: 0x{unchecked((uint)hr):X8}\n");
                        break;
                    }
                    var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, DxgiAdapterDesc1*, int>)(*(void***)adapter)[10];
                    var desc = default(DxgiAdapterDesc1);
                    Log.Info($"Debug caps: gpu before GetDesc1 ({index})");
                    Marshal.ThrowExceptionForHR(getDesc(adapter, &desc));
                    var length = 0;
                    while (length < 128 && desc.Description[length] != '\0') length++;
                    var description = EscapeText(new string(desc.Description, 0, length));
                    builder.Append($"    {index}: {description}; VendorId=0x{desc.VendorId:X4} DeviceId=0x{desc.DeviceId:X4}"
                        + $" DedicatedVideoMemory={desc.DedicatedVideoMemory} DedicatedSystemMemory={desc.DedicatedSystemMemory}"
                        + $" SharedSystemMemory={desc.SharedSystemMemory} Flags=0x{desc.Flags:X8}\n");
                    adapters.Add(new GpuAdapter(index, adapter, description, desc.Flags, desc.AdapterLuid));
                    adapter = IntPtr.Zero; // Ownership transfers to the caller's finally block.
                }
                catch (Exception ex)
                {
                    builder.Append($"    DXGI adapter {index}: error: 0x{unchecked((uint)ex.HResult):X8}\n");
                }
                finally
                {
                    ReleaseGpuCom(adapter);
                }
            }
            if (adapters.Count == 0) builder.Append("    none described\n");
        }
        catch (Exception ex)
        {
            builder.Append($"  DXGI adapters: error: 0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally
        {
            ReleaseGpuCom(factory);
        }
    }

    private static unsafe void AppendD3D12Devices(StringBuilder builder, List<GpuAdapter> adapters)
    {
        var library = IntPtr.Zero;
        try
        {
            if (!TryLoadGpuExport(builder, "d3d12.dll", "D3D12CreateDevice", out library, out var export)) return;
            var createDevice = (delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, IntPtr*, int>)export;
            AppendD3D12DefaultDevice(builder, createDevice, adapters);
            var deviceId = D3D12DeviceId;
            foreach (var adapter in adapters)
            {
                if (adapter.IsSoftware) continue;
                var device = IntPtr.Zero;
                try
                {
                    Log.Info($"Debug caps: gpu before D3D12CreateDevice (DXGI {adapter.Index})");
                    var hr = createDevice(adapter.Instance, 0xB000, &deviceId, &device); // D3D_FEATURE_LEVEL_11_0
                    builder.Append($"  D3D12CreateDevice (DXGI {adapter.Index}, {adapter.Description}): 0x{unchecked((uint)hr):X8}\n");
                }
                catch (Exception ex)
                {
                    builder.Append($"  D3D12CreateDevice (DXGI {adapter.Index}): error: 0x{unchecked((uint)ex.HResult):X8}\n");
                }
                finally
                {
                    ReleaseGpuCom(device);
                }
            }
        }
        catch (Exception ex)
        {
            builder.Append($"  D3D12: error: 0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally
        {
            FreeGpuLibrary(library, "d3d12.dll");
        }
    }

    private static unsafe void AppendD3D12DefaultDevice(StringBuilder builder,
        delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, IntPtr*, int> createDevice, List<GpuAdapter> adapters)
    {
        var device = IntPtr.Zero;
        try
        {
            var deviceId = D3D12DeviceId;
            Log.Info("Debug caps: gpu before D3D12CreateDevice (default adapter)");
            var hr = createDevice(IntPtr.Zero, 0xB000, &deviceId, &device);
            builder.Append($"  D3D12CreateDevice (default, FL 11_0): 0x{unchecked((uint)hr):X8}\n");
            if (hr < 0 || device == IntPtr.Zero) return;
            AppendD3D12Adapter(builder, device, adapters);
            AppendD3D12FeatureLevels(builder, device);
            AppendD3D12ShaderModel(builder, device);
            AppendD3D12ComputeQueue(builder, device);
            AppendD3D12VideoDecode(builder, device);
        }
        catch (Exception ex)
        {
            builder.Append($"  D3D12 default device: error: 0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally
        {
            ReleaseGpuCom(device);
        }
    }

    // Windows SDK d3d12.h: IUnknown(3) + ID3D12Object(4) = 7 inherited slots.
    // ID3D12Device starts at GetNodeCount=7, CreateCommandQueue=8, CreateCommandAllocator=9,
    // CreateGraphicsPipelineState=10, CreateComputePipelineState=11, CreateCommandList=12,
    // CheckFeatureSupport=13. GetAdapterLuid is its 37th declared method: 7 + 36 = 43.
    private static unsafe void AppendD3D12Adapter(StringBuilder builder, IntPtr device, List<GpuAdapter> adapters)
    {
        try
        {
            // x64 MSVC COM struct return: hidden return buffer follows this; return value is that buffer.
            var getLuid = (delegate* unmanaged[Stdcall]<IntPtr, GpuLuid*, GpuLuid*>)(*(void***)device)[43];
            var luid = default(GpuLuid);
            Log.Info("Debug caps: gpu before ID3D12Device::GetAdapterLuid");
            getLuid(device, &luid);
            GpuAdapter? adapter = null;
            foreach (var candidate in adapters)
            {
                if (candidate.Luid.LowPart != luid.LowPart || candidate.Luid.HighPart != luid.HighPart) continue;
                adapter = candidate;
                break;
            }
            builder.Append($"  D3D12 adapter LUID={unchecked((uint)luid.HighPart):X8}:{luid.LowPart:X8}: ");
            if (adapter is null) builder.Append("no DXGI match (hardware/software unknown)\n");
            else builder.Append($"DXGI {adapter.Index}, {adapter.Description}; SOFTWARE={(adapter.IsSoftware ? "yes" : "no")}\n");
        }
        catch (Exception ex)
        {
            builder.Append($"  D3D12 adapter: error: 0x{unchecked((uint)ex.HResult):X8}\n");
        }
    }

    private static unsafe void AppendD3D12FeatureLevels(StringBuilder builder, IntPtr device)
    {
        try
        {
            var checkFeature = (delegate* unmanaged[Stdcall]<IntPtr, int, void*, uint, int>)(*(void***)device)[13];
            int* levels = stackalloc int[] { 0xB000, 0xB100, 0xC000, 0xC100, 0xC200 }; // 11_0 through 12_2
            var data = new D3D12FeatureDataFeatureLevels { NumFeatureLevels = 5, FeatureLevelsRequested = levels };
            Log.Info("Debug caps: gpu before CheckFeatureSupport (FEATURE_LEVELS)");
            Marshal.ThrowExceptionForHR(checkFeature(device, 2, &data, (uint)sizeof(D3D12FeatureDataFeatureLevels)));
            builder.Append($"  Max feature level: 0x{data.MaxSupportedFeatureLevel:X4}\n");
        }
        catch (Exception ex)
        {
            builder.Append($"  Feature levels: error: 0x{unchecked((uint)ex.HResult):X8}\n");
        }
    }

    private static unsafe void AppendD3D12ShaderModel(StringBuilder builder, IntPtr device)
    {
        try
        {
            var checkFeature = (delegate* unmanaged[Stdcall]<IntPtr, int, void*, uint, int>)(*(void***)device)[13];
            ReadOnlySpan<int> models = [0x66, 0x65, 0x64, 0x63, 0x62, 0x61, 0x60, 0x51];
            foreach (var model in models)
            {
                var data = new D3D12FeatureDataShaderModel { HighestShaderModel = model };
                Log.Info($"Debug caps: gpu before CheckFeatureSupport (SHADER_MODEL 0x{model:X2})");
                var hr = checkFeature(device, 7, &data, (uint)sizeof(D3D12FeatureDataShaderModel));
                if (hr == unchecked((int)0x80070057) && model != 0x51) continue; // E_INVALIDARG: older runtime.
                Marshal.ThrowExceptionForHR(hr);
                builder.Append($"  Max shader model: {data.HighestShaderModel >> 4}.{data.HighestShaderModel & 15} (0x{data.HighestShaderModel:X2})\n");
                return;
            }
        }
        catch (Exception ex)
        {
            builder.Append($"  Shader model: error: 0x{unchecked((uint)ex.HResult):X8}\n");
        }
    }

    private static unsafe void AppendD3D12ComputeQueue(StringBuilder builder, IntPtr device)
    {
        var queue = IntPtr.Zero;
        try
        {
            var createQueue = (delegate* unmanaged[Stdcall]<IntPtr, D3D12CommandQueueDesc*, Guid*, IntPtr*, int>)(*(void***)device)[8];
            var desc = new D3D12CommandQueueDesc { Type = 2 }; // COMPUTE; normal priority, no flags, node mask 0.
            var queueId = new Guid("0ec870a6-5d7e-4c22-8cfc-5baae07616ed"); // IID_ID3D12CommandQueue
            Log.Info("Debug caps: gpu before ID3D12Device::CreateCommandQueue (COMPUTE)");
            var hr = createQueue(device, &desc, &queueId, &queue);
            builder.Append($"  CreateCommandQueue (COMPUTE): 0x{unchecked((uint)hr):X8}\n");
        }
        catch (Exception ex)
        {
            builder.Append($"  CreateCommandQueue (COMPUTE): error: 0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally
        {
            ReleaseGpuCom(queue);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D12VideoDecodeProfileCount
    {
        public uint NodeIndex;
        public uint ProfileCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct D3D12VideoDecodeProfiles
    {
        public uint NodeIndex;
        public uint ProfileCount;
        public Guid* Profiles;
    }

    // d3d12video.h D3D12_FEATURE_DATA_VIDEO_DECODE_SUPPORT, with D3D12_VIDEO_DECODE_CONFIGURATION inlined.
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D12VideoDecodeSupport
    {
        public uint NodeIndex;
        public Guid DecodeProfile;
        public int BitstreamEncryption;
        public int InterlaceType;
        public uint Width;
        public uint Height;
        public int DecodeFormat;
        public uint FrameRateNumerator;
        public uint FrameRateDenominator;
        public uint BitRate;
        public int SupportFlags;
        public int ConfigurationFlags;
        public int DecodeTier;
    }

    // ID3D12VideoDevice derives directly from IUnknown: CheckFeatureSupport is slot 3.
    // D3D12_FEATURE_VIDEO: DECODE_SUPPORT=0, DECODE_PROFILES=1, DECODE_PROFILE_COUNT=10.
    private static unsafe void AppendD3D12VideoDecode(StringBuilder builder, IntPtr device)
    {
        var videoDevice = IntPtr.Zero;
        try
        {
            var videoDeviceId = new Guid("1f052807-0b46-4acc-8a89-364f793718a4"); // IID_ID3D12VideoDevice
            var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)device)[0];
            Log.Info("Debug caps: gpu before QueryInterface (ID3D12VideoDevice)");
            var hr = queryInterface(device, &videoDeviceId, &videoDevice);
            builder.Append($"  ID3D12VideoDevice: 0x{unchecked((uint)hr):X8}\n");
            if (hr < 0 || videoDevice == IntPtr.Zero) return;
            var checkFeature = (delegate* unmanaged[Stdcall]<IntPtr, int, void*, uint, int>)(*(void***)videoDevice)[3];
            var count = new D3D12VideoDecodeProfileCount();
            Log.Info("Debug caps: gpu before CheckFeatureSupport (VIDEO_DECODE_PROFILE_COUNT)");
            Marshal.ThrowExceptionForHR(checkFeature(videoDevice, 10, &count, (uint)sizeof(D3D12VideoDecodeProfileCount)));
            builder.Append($"  D3D12 decode profiles: {count.ProfileCount}\n");
            if (count.ProfileCount == 0) return;
            var profiles = new Guid[Math.Min(count.ProfileCount, 64u)];
            fixed (Guid* list = profiles)
            {
                var data = new D3D12VideoDecodeProfiles { ProfileCount = (uint)profiles.Length, Profiles = list };
                Log.Info("Debug caps: gpu before CheckFeatureSupport (VIDEO_DECODE_PROFILES)");
                Marshal.ThrowExceptionForHR(checkFeature(videoDevice, 1, &data, (uint)sizeof(D3D12VideoDecodeProfiles)));
            }
            foreach (var profile in profiles)
            {
                var line = new StringBuilder("  D3D12 ")
                    .Append(GpuDecoderProfileNames.TryGetValue(profile, out var name) ? name : profile.ToString())
                    .Append(':');
                var any = false;
                var firstFailure = 0;
                var unsupportedFlags = -1;
                foreach (var (formatName, format) in GpuDecoderOutputFormats)
                {
                    foreach (var (sizeName, width, height) in GpuDecoderSizes)
                    {
                        var support = new D3D12VideoDecodeSupport
                        {
                            DecodeProfile = profile,
                            Width = width,
                            Height = height,
                            DecodeFormat = format,
                            FrameRateNumerator = 30,
                            FrameRateDenominator = 1,
                        };
                        // A failed query for one combination is "not supported", never the whole report.
                        var supportHr = checkFeature(videoDevice, 0, &support, (uint)sizeof(D3D12VideoDecodeSupport));
                        if (supportHr < 0)
                        {
                            if (firstFailure == 0) firstFailure = supportHr;
                            continue;
                        }
                        if ((support.SupportFlags & 1) == 0)
                        {
                            if (unsupportedFlags < 0) unsupportedFlags = support.SupportFlags;
                            continue;
                        }
                        line.Append(' ').Append(formatName).Append('/').Append(sizeName).Append("(tier ").Append(support.DecodeTier).Append(')');
                        any = true;
                    }
                }
                if (!any)
                {
                    line.Append(" (no supported NV12/P010 size");
                    if (firstFailure != 0) line.Append($"; first query error 0x{unchecked((uint)firstFailure):X8}");
                    if (unsupportedFlags >= 0) line.Append($"; SupportFlags=0x{unsupportedFlags:X}");
                    line.Append(')');
                }
                builder.Append(line).Append('\n');
            }
        }
        catch (Exception ex)
        {
            builder.Append($"  D3D12 video decode: error: 0x{unchecked((uint)ex.HResult):X8}\n");
        }
        finally
        {
            ReleaseGpuCom(videoDevice);
        }
    }

    private static void FreeGpuLibrary(IntPtr library, string libraryName)
    {
        if (library == IntPtr.Zero) return;
        Log.Info($"Debug caps: gpu before unloading {libraryName}");
        NativeLibrary.Free(library);
    }

    private static unsafe void ReleaseGpuCom(IntPtr instance)
    {
        if (instance == IntPtr.Zero) return;
        Log.Info("Debug caps: gpu before COM release");
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
        var capabilities = DisplayHdrOutput.DetectCapabilities();
        builder.Append($"  Bale HDR: Hdr10={capabilities.Hdr.Hdr10} Hdr10Plus={capabilities.Hdr.Hdr10Plus}"
            + $" Hlg={capabilities.Hdr.Hlg} DolbyVisionProfiles={capabilities.Hdr.DolbyVisionProfiles.Count}"
            + $" hdr_evidence={capabilities.DisplayHdrEvidence}\n");
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
