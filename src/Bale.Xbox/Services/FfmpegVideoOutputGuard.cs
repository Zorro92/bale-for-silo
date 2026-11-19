#if DEBUG_SERVER
using Bale.Core.Playback;
#endif
using FFmpegInteropX;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Bale.Xbox.Services;

/// <summary>Reject a declined tone-map stage, DV7 enhancement stream or decoder/output fallback before presentation.</summary>
internal sealed partial class FfmpegVideoOutputGuard : IDisposable
{
    internal static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    internal static readonly Guid P010 = new("30313050-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Primaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    internal static readonly Guid Transfer = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    internal static readonly Guid Matrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    internal static readonly Guid Range = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    // Bale-private media type attribute (UINT32 1): the fork's tone-map stage produced this output.
    // A declined stage passes P010 through with the container's (possibly BT.709) tags, so 10-bit
    // SDR output is trusted only with this mark. The stage writes NV12 for a bale_gpu without P010
    // output (fork bale.41), or when Bale asked for dithered 8-bit output on an 8-bit SDR display
    // (bale_tonemap_output_depth=8, phase 2 of the bit-depth rule, AGENTS.md); that NV12 must carry the
    // mark. Otherwise its inputs are 10-bit, or 8-bit SDR on the downscale route, which the rule keeps
    // at P010, so unrequested NV12 would drop depth and is rejected.
    internal static readonly Guid ToneMapped = new("b41e6a52-3c9d-4f0b-8e7a-5d2c1f9a0b37");
    private readonly FFmpegMediaSource _ffmpeg;
    private readonly MediaStreamSource _source;
    private readonly Action _failed;
    private readonly bool _toneMap;
    private readonly bool _allowNv12;
    private readonly bool _nv12Requested;
    private readonly int? _baseLayerStreamIndex;
#if DEBUG_SERVER
    private readonly Rav1gOutputCheck? _rav1g;
#endif
    private readonly object _gate = new();
    private readonly HashSet<object> _nonVideo = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;
    private bool _failure;

    /// <param name="allowNv12">True only when bale_gpu cannot write P010, so the stage's SDR output is NV12.</param>
    /// <param name="nv12Requested">Bale asked this mount's stage for dithered 8-bit output (FfmpegSource.ApplyStageOutputDepth):
    /// marked NV12 is accepted; marked P010 (an older wrapper ignoring the key) still is.</param>
    internal FfmpegVideoOutputGuard(FFmpegMediaSource source, Action failed, bool toneMap = true, int? baseLayerStreamIndex = null,
        bool allowNv12 = false, bool nv12Requested = false)
#if DEBUG_SERVER
        : this(source, failed, null, toneMap, baseLayerStreamIndex, allowNv12, nv12Requested)
    {
    }

    /// <param name="rav1g">The debug rav1g route's rule (Rav1gPolicy.OutputVerdict) instead of the stage/DV7 rules.</param>
    internal FfmpegVideoOutputGuard(FFmpegMediaSource source, Action failed, Rav1gOutputCheck? rav1g, bool toneMap = true,
        int? baseLayerStreamIndex = null, bool allowNv12 = false, bool nv12Requested = false)
#endif
    {
#if DEBUG_SERVER
        _rav1g = rav1g;
#endif
        _ffmpeg = source;
        _failed = failed;
        _toneMap = toneMap;
        _allowNv12 = allowNv12;
        _nv12Requested = nv12Requested;
        _baseLayerStreamIndex = baseLayerStreamIndex;
        _source = source.GetMediaStreamSource();
        _source.SampleRequested += OnSampleRequested;
    }

    internal static bool IsSdrOutput(VideoEncodingProperties format, bool allowNv12 = false, bool nv12Requested = false) =>
        (allowNv12 && IsSubtype(format, "NV12", Nv12)
            || nv12Requested && IsSubtype(format, "NV12", Nv12) && IsMarkedToneMapped(format)
            || IsSubtype(format, "P010", P010) && IsMarkedToneMapped(format))
        // Wrapper UncompressedVideoSampleProvider::CreateStreamDescriptor: processed output is
        // always limited-range BT.709, even when the container and bitstream disagree.
        && HasValue(format, Primaries, 2)
        && HasValue(format, Transfer, 5)
        && HasValue(format, Matrix, 1)
        && HasValue(format, Range, 2);

    internal static bool IsSubtype(VideoEncodingProperties format, string name, Guid guid) =>
        string.Equals(format.Subtype, name, StringComparison.OrdinalIgnoreCase)
            || Guid.TryParse(format.Subtype, out var subtype) && subtype == guid;

    private static bool IsMarkedToneMapped(VideoEncodingProperties format) => HasValue(format, ToneMapped, 1);

    private static bool HasValue(VideoEncodingProperties format, Guid key, uint expected) =>
        format.Properties.TryGetValue(key, out var value) && value is uint number && number == expected;

    internal static bool IsHdr10Output(VideoEncodingProperties format) =>
        (string.Equals(format.Subtype, "P010", StringComparison.OrdinalIgnoreCase)
            || Guid.TryParse(format.Subtype, out var subtype) && subtype == P010)
        && HasValue(format, Primaries, 9) && HasValue(format, Transfer, 15)
        && HasValue(format, Matrix, 4) && HasValue(format, Range, 2);

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
                var current = _ffmpeg.CurrentVideoStream;
#if DEBUG_SERVER
                if (_rav1g is not null)
                {
                    if (!_failure && _rav1g.Accept(video.EncodingProperties, current?.DecoderEngine)) return;
                }
                else
#endif
                if (!_failure && current?.DecoderEngine == DecoderEngine.FFmpegD3D11HardwareDecoder
                    && (_baseLayerStreamIndex is null || current.StreamIndex == _baseLayerStreamIndex
                        && Bale.Core.Playback.SoftwareDecodePolicy.NormalizeCodec(current.CodecName) == "hevc")
                    && (_toneMap ? IsSdrOutput(video.EncodingProperties, _allowNv12, _nv12Requested)
                        : IsHdr10Output(video.EncodingProperties))) return;
            }
            catch { } // An unreadable descriptor cannot establish the promised output.
            // The wrapper's handler runs first and assigns synchronously. Remove a rejected sample
            // before the event completes, then stop the source; never present the HDR fallback.
            try { args.Request.Sample = null; } catch { }
            try { sender.NotifyError(MediaStreamSourceErrorStatus.Other); } catch { }
            if (_failure) return;
            _failure = true;
            try { Log.Info("player: decoded video output rejected; server conversion required"); } catch { }
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

#if DEBUG_SERVER
/// <summary>
/// The debug rav1g route's output rule (fork bale.44, design §4; Rav1gPolicy.OutputVerdict): direct output carries
/// the rav1g mark with the source's depth and colour, stage output the tone-map mark; unmarked output is the open's
/// dav1d fallback, accepted and reported. The verdict is computed once per distinct media type, not per sample.
/// </summary>
internal sealed class Rav1gOutputCheck(Av1SourceFacts source, bool sdrDisplay, bool allowNv12Stage)
{
    private Rav1gOutputFacts? _last;
    private Rav1gVerdict? _verdict;
    private int _gpuEngine = -1;

    /// <summary>The last verdict's kind (direct, stage, dav1d, reject), or null before the first video sample.</summary>
    internal string? Verdict => Volatile.Read(ref _verdict)?.Kind;

    /// <summary>The video stream reported rav1g's D3D11 engine (true) or dav1d's software engine (false), if seen.</summary>
    internal bool? GpuEngine => Volatile.Read(ref _gpuEngine) is var engine and >= 0 ? engine == 1 : null;

    /// <summary>Called under the guard's lock for each video sample.</summary>
    internal bool Accept(VideoEncodingProperties format, DecoderEngine? engine)
    {
        if (engine is { } reported) Volatile.Write(ref _gpuEngine, reported == DecoderEngine.FFmpegD3D11HardwareDecoder ? 1 : 0);
        var facts = Facts(format);
        if (_verdict is null || facts != _last)
        {
            var verdict = Rav1gPolicy.OutputVerdict(source, facts, sdrDisplay, allowNv12Stage);
            _last = facts;
            Volatile.Write(ref _verdict, verdict);
            // Fixed words and media-type numbers only.
            Log.Info(verdict.Kind == "dav1d"
                ? $"player: rav1g output unmarked ({facts.Subtype}); the open fell back to dav1d"
                : $"player: rav1g output {verdict.Kind} ({facts.Subtype}{(verdict.Reason is { } reason ? ", " + reason : "")})");
        }
        return _verdict!.Accepted;
    }

    internal static Rav1gOutputFacts Facts(VideoEncodingProperties format) => new(
        FfmpegVideoOutputGuard.IsSubtype(format, "NV12", FfmpegVideoOutputGuard.Nv12) ? "NV12"
            : FfmpegVideoOutputGuard.IsSubtype(format, "P010", FfmpegVideoOutputGuard.P010) ? "P010"
            : format.Subtype is { Length: <= 64 } subtype ? subtype : "other",
        Value(format, Rav1gPolicy.OutputMark) == 1, Value(format, FfmpegVideoOutputGuard.ToneMapped) == 1,
        Value(format, FfmpegVideoOutputGuard.Primaries), Value(format, FfmpegVideoOutputGuard.Transfer),
        Value(format, FfmpegVideoOutputGuard.Matrix), Value(format, FfmpegVideoOutputGuard.Range));

    private static uint? Value(VideoEncodingProperties format, Guid key) =>
        format.Properties.TryGetValue(key, out var value) && value is uint number ? number : null;
}
#endif
