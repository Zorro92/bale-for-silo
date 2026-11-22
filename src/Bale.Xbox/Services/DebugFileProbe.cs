#if DEBUG_SERVER
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Bale.Core.Api.Models;

namespace Bale.Xbox.Services;

internal sealed class DebugPlayUrlException(string reason) : Exception(reason);

/// <summary>
/// Demux through the mount's existing range stream, before any player/decoder is opened. Never hand FFmpeg a URL.
/// Bind only public FFmpeg 8 ABI prefixes (n8.1 libavformat/avformat.h, libavcodec/codec_par.h, libavutil/pixdesc.h);
/// check all three majors before dereferencing them. The packaged FFmpeg and Linux integration tests use this ABI.
/// </summary>
internal static unsafe class DebugFileProbeReader
{
    private const string Format = "avformat-62.dll", Codec = "avcodec-62.dll", Util = "avutil-60.dll";
    private const int Eof = -541478725, IoError = -5, ProbeBufferSize = 32768;
    private sealed record Input(Stream Stream, CancellationToken Cancellation);

    // Deliberately synchronous on a pool thread: the normal range readers already own caching/prefetch.
    internal static DebugFileProbe Read(Stream stream, CancellationToken cancellation)
    {
        if (IntPtr.Size != 8 || avformat_version() >> 16 != 62 || avcodec_version() >> 16 != 62 || avutil_version() >> 16 != 60)
            throw new DebugPlayUrlException("Debug file probe requires the packaged FFmpeg 8 ABI.");
        cancellation.ThrowIfCancellationRequested();
        var handle = GCHandle.Alloc(new Input(stream, cancellation));
        nint io = 0, context = 0, options = 0;
        byte* buffer = null;
        try
        {
            buffer = (byte*)av_malloc(ProbeBufferSize);
            if (buffer == null) throw new OutOfMemoryException();
            io = avio_alloc_context(buffer, ProbeBufferSize, 0, GCHandle.ToIntPtr(handle), &ReadPacket, 0, &Seek);
            if (io == 0) throw new OutOfMemoryException();
            buffer = null; // AVIO owns it; FFmpeg may replace it while probing.
            context = avformat_alloc_context();
            if (context == 0) throw new OutOfMemoryException();
            ((FormatContext*)context)->Io = io;
            // Plain self-contained files only. No playlists or nested URL/file opens, even if mislabeled.
            Set(&options, "format_whitelist", "mov,matroska,avi,mpegts,mpeg,ogg,asf,flv,ivf");
            Set(&options, "protocol_whitelist", "");
            Set(&options, "probesize", "5000000");
            Set(&options, "analyzeduration", "5000000");
            Set(&options, "max_streams", "32");
            Require(avformat_open_input(&context, 0, 0, &options));
            var format = (FormatContext*)context;
            if (format->StreamCount is 0 or > 32) throw new DebugPlayUrlException("Debug file stream inventory is unsupported.");
            // find_stream_info may decode a header/frame when the container omitted facts; keep that temporary
            // CPU work at one thread (4 shared cores). All its packets/contexts die before the real mount.
            var initialStreams = (int)format->StreamCount;
            var streamOptions = stackalloc nint[initialStreams];
            new Span<nint>(streamOptions, initialStreams).Clear();
            try
            {
                for (var i = 0; i < initialStreams; i++) Set(streamOptions + i, "threads", "1");
                Require(avformat_find_stream_info(context, streamOptions));
            }
            finally
            {
                for (var i = 0; i < initialStreams; i++) av_dict_free(streamOptions + i);
            }
            cancellation.ThrowIfCancellationRequested();
            var videos = new List<nint>();
            for (var i = 0; i < format->StreamCount; i++)
            {
                var candidate = (AvStream*)format->Streams[i];
                if (candidate->Parameters->Type == 0 && (candidate->Disposition & (1 << 10)) == 0)
                    videos.Add((nint)candidate);
            }
            if (videos.Count != 1) throw new DebugPlayUrlException("Debug file playback requires exactly one video stream.");
            var video = (AvStream*)videos[0];
            var parameters = video->Parameters;
            // AV_PKT_DATA_DOVI_CONF = 29. This probe does not prove DV base-layer compatibility: fail visibly.
            if (av_packet_side_data_get(parameters->SideData, parameters->SideDataCount, 29) != 0)
                throw new DebugPlayUrlException("Debug file probe does not support Dolby Vision; use a plain base-layer clip.");
            var pixels = (PixelDescriptor*)av_pix_fmt_desc_get(parameters->PixelFormat);
            var depth = pixels == null ? parameters->RawDepth : pixels->ComponentDepth;
            if (parameters->Width <= 0 || parameters->Height <= 0 || depth is <= 0 or > 16)
                throw new DebugPlayUrlException("Debug file probe could not determine video dimensions or depth.");
            var fps = Rate(av_guess_frame_rate(context, (nint)video, 0)) ?? Rate(video->AverageRate) ?? Rate(parameters->FrameRate);
            var audioIndex = av_find_best_stream(context, 1, -1, -1, 0, 0);
            var audioCodec = audioIndex < 0 ? null : Text(avcodec_get_name(((AvStream*)format->Streams[audioIndex])->Parameters->Codec));
            var formatName = Text(*(nint*)format->InputFormat);
            var container = formatName?.Split(',')[0] switch
            {
                "mov" => "mp4", "matroska" => "matroska", "mpegts" => "mpegts",
                "avi" => "avi", "mpeg" => "mpeg", "ogg" => "ogg", "asf" => "asf", "flv" => "flv", "ivf" => "ivf",
                _ => throw new DebugPlayUrlException("Debug file container is unsupported."),
            };
            return new(container, new VideoTrack
            {
                Codec = Text(avcodec_get_name(parameters->Codec)), Width = parameters->Width, Height = parameters->Height,
                BitDepth = depth, PixelFormat = Text(av_get_pix_fmt_name(parameters->PixelFormat)),
                Profile = Text(avcodec_profile_name(parameters->Codec, parameters->Profile)),
                ColorTransfer = Text(av_color_transfer_name(parameters->Transfer)),
                ColorPrimaries = Text(av_color_primaries_name(parameters->Primaries)),
                ColorSpace = Text(av_color_space_name(parameters->Matrix)),
                ColorRange = Text(av_color_range_name(parameters->Range)), FrameRate = fps?.ToString("R", CultureInfo.InvariantCulture),
            }, audioCodec, fps, format->Duration > 0 ? format->Duration / 1000000.0 : null);
        }
        finally
        {
            av_dict_free(&options);
            if (context != 0) avformat_close_input(&context);
            if (io != 0)
            {
                // AVIOContext's public prefix: av_class, buffer. avio_context_free does not free this buffer.
                av_free(((nint*)io)[1]);
                avio_context_free(&io);
            }
            if (buffer != null) av_free((nint)buffer);
            handle.Free();
            stream.Position = 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadPacket(nint opaque, byte* buffer, int size)
    {
        try
        {
            var input = (Input)GCHandle.FromIntPtr(opaque).Target!;
            input.Cancellation.ThrowIfCancellationRequested();
            var read = input.Stream.Read(new Span<byte>(buffer, size));
            return read == 0 ? Eof : read;
        }
        catch { return IoError; } // No managed exception may cross FFmpeg's C callback.
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Seek(nint opaque, long offset, int whence)
    {
        try
        {
            var input = (Input)GCHandle.FromIntPtr(opaque).Target!;
            input.Cancellation.ThrowIfCancellationRequested();
            return (whence & 0x10000) != 0 ? input.Stream.Length : input.Stream.Seek(offset, (SeekOrigin)(whence & ~0x20000));
        }
        catch { return IoError; }
    }

    private static string? Text(nint text) => text == 0 ? null : Marshal.PtrToStringUTF8(text);
    private static double? Rate(Rational rate) => rate.Num > 0 && rate.Den > 0 ? (double)rate.Num / rate.Den : null;
    private static void Require(int result)
    {
        if (result < 0) throw new DebugPlayUrlException($"Debug file probe failed (FFmpeg {result}).");
    }
    // Pointer-only signatures: the app disables runtime marshalling (CA1420), so strings go over as UTF-8 by hand.
    private static void Set(nint* options, string name, string value)
    {
        var n = Marshal.StringToCoTaskMemUTF8(name);
        var v = Marshal.StringToCoTaskMemUTF8(value);
        try { Require(av_dict_set(options, (byte*)n, (byte*)v, 0)); }
        finally { Marshal.FreeCoTaskMem(n); Marshal.FreeCoTaskMem(v); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rational { public int Num, Den; }
    [StructLayout(LayoutKind.Explicit)] private struct FormatContext
    {
        [FieldOffset(8)] public nint InputFormat;
        [FieldOffset(32)] public nint Io;
        [FieldOffset(44)] public uint StreamCount;
        [FieldOffset(48)] public nint* Streams;
        [FieldOffset(104)] public long Duration;
    }
    [StructLayout(LayoutKind.Explicit)] private struct AvStream
    {
        [FieldOffset(16)] public CodecParameters* Parameters;
        [FieldOffset(64)] public int Disposition;
        [FieldOffset(88)] public Rational AverageRate;
    }
    [StructLayout(LayoutKind.Explicit)] private struct CodecParameters
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(4)] public int Codec;
        [FieldOffset(32)] public nint SideData;
        [FieldOffset(40)] public int SideDataCount;
        [FieldOffset(44)] public int PixelFormat;
        [FieldOffset(60)] public int RawDepth;
        [FieldOffset(64)] public int Profile;
        [FieldOffset(72)] public int Width;
        [FieldOffset(76)] public int Height;
        [FieldOffset(88)] public Rational FrameRate;
        [FieldOffset(100)] public int Range;
        [FieldOffset(104)] public int Primaries;
        [FieldOffset(108)] public int Transfer;
        [FieldOffset(112)] public int Matrix;
    }
    [StructLayout(LayoutKind.Explicit)] private struct PixelDescriptor { [FieldOffset(40)] public int ComponentDepth; }

    [DllImport(Format, CallingConvention = CallingConvention.Cdecl)] private static extern uint avformat_version();
    [DllImport(Codec, CallingConvention = CallingConvention.Cdecl)] private static extern uint avcodec_version();
    [DllImport(Util, CallingConvention = CallingConvention.Cdecl)] private static extern uint avutil_version();
    [DllImport(Util, CallingConvention = CallingConvention.Cdecl)] private static extern nint av_malloc(nuint size);
    [DllImport(Util, CallingConvention = CallingConvention.Cdecl)] private static extern void av_free(nint memory);
    [DllImport(Format, CallingConvention = CallingConvention.Cdecl)] private static extern nint avio_alloc_context(byte* buffer, int size, int write,
        nint opaque, delegate* unmanaged[Cdecl]<nint, byte*, int, int> read, nint writePacket, delegate* unmanaged[Cdecl]<nint, long, int, long> seek);
    [DllImport(Format, CallingConvention = CallingConvention.Cdecl)] private static extern void avio_context_free(nint* io);
    [DllImport(Format, CallingConvention = CallingConvention.Cdecl)] private static extern nint avformat_alloc_context();
    [DllImport(Format, CallingConvention = CallingConvention.Cdecl)] private static extern int avformat_open_input(nint* context, nint url, nint format, nint* options);
    [DllImport(Format, CallingConvention = CallingConvention.Cdecl)] private static extern int avformat_find_stream_info(nint context, nint* options);
    [DllImport(Format, CallingConvention = CallingConvention.Cdecl)] private static extern void avformat_close_input(nint* context);
    [DllImport(Format, CallingConvention = CallingConvention.Cdecl)] private static extern Rational av_guess_frame_rate(nint context, nint stream, nint frame);
    [DllImport(Format, CallingConvention = CallingConvention.Cdecl)] private static extern int av_find_best_stream(nint context, int type, int wanted, int related, nint decoder, int flags);
    [DllImport(Util, CallingConvention = CallingConvention.Cdecl)] private static extern int av_dict_set(nint* dict, byte* name, byte* value, int flags);
    [DllImport(Util, CallingConvention = CallingConvention.Cdecl)] private static extern void av_dict_free(nint* dict);
    [DllImport(Util, CallingConvention = CallingConvention.Cdecl)] private static extern nint av_pix_fmt_desc_get(int format);
    [DllImport(Util, CallingConvention = CallingConvention.Cdecl)] private static extern nint av_get_pix_fmt_name(int format);
    [DllImport(Util, CallingConvention = CallingConvention.Cdecl)] private static extern nint av_color_transfer_name(int transfer);
    [DllImport(Util, CallingConvention = CallingConvention.Cdecl)] private static extern nint av_color_primaries_name(int primaries);
    [DllImport(Util, CallingConvention = CallingConvention.Cdecl)] private static extern nint av_color_space_name(int matrix);
    [DllImport(Util, CallingConvention = CallingConvention.Cdecl)] private static extern nint av_color_range_name(int range);
    [DllImport(Codec, CallingConvention = CallingConvention.Cdecl)] private static extern nint avcodec_get_name(int codec);
    [DllImport(Codec, CallingConvention = CallingConvention.Cdecl)] private static extern nint avcodec_profile_name(int codec, int profile);
    [DllImport(Codec, CallingConvention = CallingConvention.Cdecl)] private static extern nint av_packet_side_data_get(nint data, int count, int type);
}
#endif
