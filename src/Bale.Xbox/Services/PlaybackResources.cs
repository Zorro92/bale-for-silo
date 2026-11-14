using System.Runtime.InteropServices;
using Bale.Core.Playback;
using FFmpegInteropX;
using Windows.Media.Core;

namespace Bale.Xbox.Services;

/// <summary>Idle-only production resource cleanup. Called on the UI thread after media Close.</summary>
internal static unsafe class PlaybackResources
{
    internal static PlaybackResourceLifetime Lifetime { get; } = new();

    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_gpu_lifecycle_abi_version();
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_trim();
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern ulong bale_gpu_trimmed_count();
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_gpu_device_count();
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_media_device_hold(nint source, nint* device);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern void bale_gpu_media_device_release(nint device);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_gpu_trim_device(nint device);

    /// <summary>Captures the renderer device at Starting, before end-of-media can close the MSS.</summary>
    internal sealed class MediaDevice
    {
        private readonly object _gate = new();
        private MediaStreamSource? _source;
        private nint _device;

        internal MediaDevice(FFmpegMediaSource ffmpeg)
        {
            _source = ffmpeg.GetMediaStreamSource();
            if (_source is not null) _source.Starting += OnStarting;
        }

        private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            lock (_gate)
            {
                if (_source is not null && _device == 0) _device = HoldMediaDevice(sender);
            }
        }

        // Called before Close. Refuse any late Starting callback and transfer the device to the
        // teardown stack, which releases/trims it AFTER player, source and uploader Close.
        internal nint TakeForClose()
        {
            lock (_gate)
            {
                if (_source is { } source)
                {
                    if (_device == 0) _device = HoldMediaDevice(source);
                    try { source.Starting -= OnStarting; }
                    catch (Exception ex) { Failure("detach renderer device", ex); }
                    _source = null;
                }
                var device = _device;
                _device = 0;
                return device;
            }
        }
    }

    // Merely takes a COM reference, with no context operations during playback.
    private static nint HoldMediaDevice(MediaStreamSource source)
    {
        try
        {
            if (bale_gpu_lifecycle_abi_version() != 1) return 0;
            nint device = 0;
            var hr = bale_gpu_media_device_hold(((WinRT.IWinRTObject)source).NativeObject.ThisPtr, &device);
            GC.KeepAlive(source);
            if (hr < 0) Log.Info($"player: hold renderer device hr=0x{unchecked((uint)hr):X8}");
            return device;
        }
        catch (Exception ex) { Failure("hold renderer device", ex); return 0; }
    }

    internal static void ReleaseMediaDevice(nint device)
    {
        if (device == 0) return;
        try { bale_gpu_media_device_release(device); }
        catch (Exception ex) { Failure("release renderer device", ex); }
    }

    internal static void TrimDevice(nint device)
    {
        if (device == 0) return;
        try
        {
            var hr = bale_gpu_trim_device(device);
            if (hr < 0) Log.Info($"player resources: trim device hr=0x{unchecked((uint)hr):X8}");
        }
        catch (Exception ex) { Failure("trim device", ex); }
    }

    internal static void ReleaseIdle(string reason)
    {
        Lifetime.ReleaseIfIdle(() =>
        {
            try
            {
                var hr = bale_gpu_lifecycle_abi_version() == 1
                    ? bale_gpu_trim() : unchecked((int)0x80004002);
                if (hr < 0) Log.Info($"player: idle trim hr=0x{unchecked((uint)hr):X8}");
#if DEBUG_SERVER
                Interlocked.Increment(ref _releases);
                Log.Info($"player resources: {reason} idle-release=zc,driver-caches trim_hr=0x{unchecked((uint)hr):X8} trimmed={bale_gpu_trimmed_count()} owned_d3d11={bale_gpu_device_count()}");
#endif
            }
            catch (Exception ex) { Failure("idle trim", ex); }
            // App releases UI residency BEFORE this trim. The XAML compositor is OS-owned;
            // bale_gpu_trim only touches Bale-owned idle devices, never steals its device.
        });
    }

    private static void Failure(string operation, Exception ex) =>
        Log.Info($"player resources: {operation} type={ex.GetType().Name} hr=0x{unchecked((uint)ex.HResult):X8}");

#if DEBUG_SERVER
    private static int _releases;
    internal static string Describe()
    {
        try { return $"idle releases={Volatile.Read(ref _releases)} trimmed={bale_gpu_trimmed_count()} owned D3D11={bale_gpu_device_count()}"; }
        catch { return "unavailable"; }
    }
#endif
}
