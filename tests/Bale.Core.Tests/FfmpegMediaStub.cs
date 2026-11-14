// Exercise Bale's configuration/observer code on Linux. These stand-ins do not decode video;
// Windows CI compiles against the pinned WinRT package and builds the actual MSIX.
namespace FFmpegInteropX
{
    internal enum VideoDecoderMode { Automatic, AutomaticSystemDecoder, ForceSystemDecoder, ForceFFmpegSoftwareDecoder }
    internal enum DecoderEngine { SystemDecoder, FFmpegSoftwareDecoder, FFmpegD3D11HardwareDecoder }
    internal enum HardwareDecoderStatus { Unknown, Available, NotAvailable }
    internal enum HdrSupport { Automatic, Enabled, Disabled }

    internal sealed class MediaSourceConfig
    {
        public VideoConfig Video { get; } = new();
        public AudioConfig Audio { get; } = new();
        public GeneralConfig General { get; } = new();
        public SubtitleConfig Subtitles { get; } = new();
        public Dictionary<string, object> FFmpegOptions { get; } = [];
    }

    internal sealed class VideoConfig
    {
        public VideoDecoderMode VideoDecoderMode { get; set; }
        public HdrSupport HdrSupport { get; set; }
    }

    internal sealed class AudioConfig
    {
        public bool SystemDecoderAAC { get; set; }
        public bool SystemDecoderMP3 { get; set; }
    }

    internal sealed class GeneralConfig
    {
        public bool ReadAheadBufferEnabled { get; set; }
        public long ReadAheadBufferSize { get; set; }
        public TimeSpan ReadAheadBufferDuration { get; set; }
        public uint FileStreamReadSize { get; set; }
    }

    internal sealed class SubtitleConfig
    {
        public bool AutoSelectForcedSubtitles { get; set; }
    }

    internal sealed class VideoStreamInfo
    {
        public string? CodecName { get; set; }
        public DecoderEngine DecoderEngine { get; set; }
        public HardwareDecoderStatus HardwareDecoderStatus { get; set; }
    }

    internal sealed class FFmpegMediaSource
    {
        public List<VideoStreamInfo> VideoStreams { get; } = [];
        public VideoStreamInfo? CurrentVideoStream => VideoStreams.FirstOrDefault();
        internal Windows.Media.Core.MediaStreamSource Source { get; } = new();
        public Windows.Media.Core.MediaStreamSource GetMediaStreamSource() => Source;
    }
}

namespace Windows.Media.Core
{
    internal sealed class MediaStreamSource
    {
        internal event Action<MediaStreamSource, MediaStreamSourceSampleRequestedEventArgs>? SampleRequested;
        internal int Observers => SampleRequested?.GetInvocationList().Length ?? 0;
        internal void Request(object descriptor) => SampleRequested?.Invoke(this, new() { Request = new() { StreamDescriptor = descriptor } });
    }

    internal sealed class MediaStreamSourceSampleRequestedEventArgs
    {
        public MediaStreamSourceSampleRequest Request { get; init; } = new();
    }

    internal sealed class MediaStreamSourceSampleRequest
    {
        public object? StreamDescriptor { get; init; }
    }

    internal sealed class VideoStreamDescriptor
    {
        public Windows.Media.MediaProperties.VideoEncodingProperties EncodingProperties { get; init; } = new();
    }
}

namespace Windows.Media.MediaProperties
{
    internal sealed class VideoEncodingProperties
    {
        public string Subtype { get; set; } = "";
        public uint Width { get; set; }
        public uint Height { get; set; }
        public Dictionary<Guid, object> Properties { get; } = [];
    }
}

namespace WinRT
{
    internal static class CastExtensions
    {
        internal static T As<T>(object value) => (T)value;
    }
}

namespace Bale.Xbox.Services
{
    internal static class FfmpegLogging
    {
        internal static void Configure() { }
    }

    internal static class ZeroCopyRuntime
    {
        internal static bool Register() => true;
    }
}
