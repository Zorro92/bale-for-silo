#if DEBUG_SERVER
using System.Globalization;
using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Bale.Xbox.Services;

/// <summary>Passive MSS descriptor capture with post-start decoder/format refresh for stats.</summary>
internal sealed class DebugVideoMediaType : IDisposable
{
    private readonly FFmpegMediaSource _ffmpeg;
    private readonly MediaStreamSource _source;
    private readonly VideoDecoderMode _decoderMode;
    private readonly object _gate = new();
    private readonly HashSet<object> _nonVideo = new(ReferenceEqualityComparer.Instance);
    private bool _finished;
    private bool _disposed;
    private bool _playbackStarted;
    private VideoEncodingProperties? _format;
    private string? _description;

    internal DebugVideoMediaType(FFmpegMediaSource source, VideoDecoderMode decoderMode)
    {
        _ffmpeg = source;
        _decoderMode = decoderMode;
        _source = source.GetMediaStreamSource();
        // The public wrapper has no descriptor getter. SampleRequest.StreamDescriptor exposes
        // the actual MSS input type, even for compressed/system video. Subscribe after the item
        // exists and before player.Source; never read/retain Sample, request a deferral or set data.
        _source.SampleRequested += OnSampleRequested;
    }

    internal string? Description => Volatile.Read(ref _description);

    internal void Refresh(bool playbackStarted)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _playbackStarted |= playbackStarted;
            if (_format is not null) UpdateDescription();
        }
    }

    internal static string Describe(string route, string source, VideoEncodingProperties format)
    {
        // A subtype should be a short MF name/FourCC or GUID. Never echo arbitrary media text.
        var subtype = format.Subtype;
        if (string.IsNullOrEmpty(subtype)) subtype = "unset";
        else if (Guid.TryParse(subtype, out var id))
        {
            // Windows SDK mfapi.h: FFmpegInteropX also supplies the P010 GUID as a string.
            subtype = id == new Guid("30313050-0000-0010-8000-00aa00389b71") ? "P010"
                : id == new Guid("3231564e-0000-0010-8000-00aa00389b71") ? "NV12" : id.ToString("D");
        }
        else if (subtype.Length > 80 || !subtype.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) subtype = "unknown";
        return string.Create(CultureInfo.InvariantCulture,
            $"route={route} source={source} subtype={subtype} width={format.Width} height={format.Height} ")
            + DebugVideoColour.Describe(format.Properties);
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        lock (_gate)
        {
            if (_finished) return;
            try
            {
                var descriptor = args.Request.StreamDescriptor;
                if (descriptor is null || _nonVideo.Contains(descriptor)) return;
                VideoStreamDescriptor video;
                // Native AOT projects IMediaStreamDescriptor as IInspectable: a C# type test
                // misses it. Use the same explicit QI as DebugFrameDelivery.IsVideo.
                try { video = WinRT.CastExtensions.As<VideoStreamDescriptor>(descriptor); }
                catch (InvalidCastException)
                {
                    if (_nonVideo.Count < 16) _nonVideo.Add(descriptor);
                    return;
                }
                // Keep encoding properties, never a sample/request. D3D11 may update this descriptor
                // on first decoded frames (e.g. P010); the engine can also fall back after OnStarting.
                _format = video.EncodingProperties;
                UpdateDescription();
            }
            catch (Exception ex)
            {
                var description = $"route=ffmpeg source=mss unavailable ({ex.GetType().Name})";
                Publish(description);
            }
            Detach();
        }
    }

    private void UpdateDescription()
    {
        try
        {
            var video = _ffmpeg.CurrentVideoStream;
            var engine = video?.DecoderEngine;
            var description = Describe("ffmpeg", "mss", _format!)
                + $" decoderMode={_decoderMode}" + DecoderDetails(engine)
                + $" hardwareStatus={video?.HardwareDecoderStatus.ToString() ?? "unknown"}"
                + (_playbackStarted ? " decoderStatus=observed" : " decoderStatus=pending");
            // HardwareDecoderStatus can remain Available after a D3D11 provider falls back.
            // The live DecoderEngine is authoritative; never describe software as d3d11va.
            if (_playbackStarted && _decoderMode == VideoDecoderMode.Automatic && engine == DecoderEngine.FFmpegSoftwareDecoder)
                description += " fallback=software";
            Publish(description);
        }
        catch (Exception ex) { Publish($"route=ffmpeg source=mss unavailable ({ex.GetType().Name})"); }
    }

    private void Publish(string description)
    {
        if (description == _description) return;
        Volatile.Write(ref _description, description);
        Log.Info("player: video media type " + description);
    }

    private static string DecoderDetails(DecoderEngine? engine) => engine switch
    {
        // FFmpegInteropX's SystemDecoder uses compressed sample providers; FFmpeg engines
        // expose decoded pixels (NV12/P010 etc.). Report the actual engine, never infer from mode.
        DecoderEngine.SystemDecoder => " decoder=system compressed=yes",
        DecoderEngine.FFmpegSoftwareDecoder => " decoder=ffmpeg-software compressed=no",
        DecoderEngine.FFmpegD3D11HardwareDecoder => " decoder=ffmpeg-d3d11 compressed=no",
        _ => " decoder=unknown compressed=unknown",
    };

    private void Detach()
    {
        if (_finished) return;
        _finished = true;
        _nonVideo.Clear();
        try { _source.SampleRequested -= OnSampleRequested; }
        catch (Exception ex) { Log.Info($"player: video media type observer detach {ex.GetType().Name}"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _format = null;
            Detach();
        }
    }
}
#endif
