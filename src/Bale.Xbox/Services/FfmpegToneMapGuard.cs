using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Bale.Xbox.Services;

/// <summary>Fail closed if the wrapper declined the requested stage or later fell back from d3d11va.</summary>
internal sealed class FfmpegToneMapGuard : IDisposable
{
    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid Primaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    private static readonly Guid Transfer = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    private static readonly Guid Matrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid Range = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private readonly FFmpegMediaSource _ffmpeg;
    private readonly MediaStreamSource _source;
    private readonly Action _failed;
    private readonly object _gate = new();
    private readonly HashSet<object> _nonVideo = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;
    private bool _failure;

    internal FfmpegToneMapGuard(FFmpegMediaSource source, Action failed)
    {
        _ffmpeg = source;
        _failed = failed;
        _source = source.GetMediaStreamSource();
        _source.SampleRequested += OnSampleRequested;
    }

    internal static bool IsSdrOutput(VideoEncodingProperties format) =>
        (string.Equals(format.Subtype, "NV12", StringComparison.OrdinalIgnoreCase)
            || Guid.TryParse(format.Subtype, out var subtype) && subtype == Nv12)
        // Wrapper UncompressedVideoSampleProvider::CreateStreamDescriptor: processed output is
        // always limited-range NV12 BT.709, even when the container and bitstream disagree.
        && HasValue(format, Primaries, 2)
        && HasValue(format, Transfer, 5)
        && HasValue(format, Matrix, 1)
        && HasValue(format, Range, 2);

    private static bool HasValue(VideoEncodingProperties format, Guid key, uint expected) =>
        format.Properties.TryGetValue(key, out var value) && value is uint number && number == expected;

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var descriptor = args.Request.StreamDescriptor;
                if (descriptor is null || _nonVideo.Contains(descriptor)) return;
                VideoStreamDescriptor video;
                // QI is required for IMediaStreamDescriptor on Native AOT (DebugVideoMediaType).
                try { video = WinRT.CastExtensions.As<VideoStreamDescriptor>(descriptor); }
                catch (InvalidCastException)
                {
                    if (_nonVideo.Count < 16) _nonVideo.Add(descriptor);
                    return;
                }
                if (!_failure && _ffmpeg.CurrentVideoStream?.DecoderEngine == DecoderEngine.FFmpegD3D11HardwareDecoder
                    && IsSdrOutput(video.EncodingProperties)) return;
            }
            catch { } // An unreadable descriptor cannot establish safe SDR output.
            // The wrapper's handler runs first and assigns synchronously. Remove a rejected sample
            // before the event completes, then stop the source; never present the HDR fallback.
            try { args.Request.Sample = null; } catch { }
            try { sender.NotifyError(MediaStreamSourceErrorStatus.Other); } catch { }
            if (_failure) return;
            _failure = true;
            try { Log.Info("player: console tone-map output rejected; server tone mapping required"); } catch { }
            try { _failed(); } catch { } // Never propagate through the native event callback.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _source.SampleRequested -= OnSampleRequested;
            _nonVideo.Clear();
        }
    }
}
