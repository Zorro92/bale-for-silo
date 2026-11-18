using Bale.Core.Playback;
using FFmpegInteropX;

namespace Bale.Xbox.Services;

/// <summary>The FFmpeg source's current video stream, copied off the wrapper.</summary>
internal sealed record FfmpegVideoSnapshot(int StreamIndex, string? CodecName, DecoderEngine DecoderEngine,
    HardwareDecoderStatus HardwareDecoderStatus, int PixelWidth, int PixelHeight, double FramesPerSecond);

/// <summary>The FFmpeg source's current audio stream, copied off the wrapper.</summary>
internal sealed record FfmpegAudioSnapshot(int StreamIndex, string? CodecName, DecoderEngine DecoderEngine, int Channels,
    int SampleRate);

/// <summary>
/// What the stats, the HUD and the debug readers show of an FFmpeg mount, so the UI thread never touches the
/// wrapper during playback. <c>CurrentVideoStream</c>/<c>CurrentAudioStream</c> (like <c>MetadataTags</c>,
/// <c>PlaybackItem</c>, <c>PlaybackSession</c>, <c>CreateMediaPlaybackItem</c> and <c>Close</c>) take the source lock,
/// which the sample thread may hold while it waits on a decoder: with rav1g on 2026-11-18 the UI thread froze inside
/// <c>CurrentVideoStream</c>, and the start budget and debug server stopped with it (the fork's bale/README.md, "rav1g
/// prewarm and threading notes"). The mount reads once at open, after creation and before the MediaStreamSource
/// exists (nothing decodes yet); later reads run on the thread pool, one at a time (<see cref="BackgroundSnapshot{T}"/>).
/// </summary>
internal sealed record FfmpegStreamSnapshot(FfmpegVideoSnapshot? Video, FfmpegAudioSnapshot? Audio)
{
    /// <summary>Reads the wrapper: takes its source lock, so never on the UI thread once samples flow.</summary>
    internal static FfmpegStreamSnapshot Read(FFmpegMediaSource source)
    {
        var video = source.CurrentVideoStream;
        var audio = source.CurrentAudioStream;
        return new(video is null ? null : new(video.StreamIndex, video.CodecName, video.DecoderEngine,
                video.HardwareDecoderStatus, video.PixelWidth, video.PixelHeight, video.FramesPerSecond),
            audio is null ? null : new(audio.StreamIndex, audio.CodecName, audio.DecoderEngine, audio.Channels, audio.SampleRate));
    }

    /// <summary>
    /// The mount's snapshot: read now (call at open, before CreateMediaPlaybackItem) and refreshed on the thread pool.
    /// </summary>
    internal static BackgroundSnapshot<FfmpegStreamSnapshot> Open(FFmpegMediaSource source, Action<Action>? schedule = null)
    {
        FfmpegStreamSnapshot? initial = null;
        try { initial = Read(source); }
        catch (Exception ex) { Failed(ex); }
        return new(() => Read(source), initial, schedule, Failed);
    }

    // Fixed words and the exception type only.
    private static void Failed(Exception ex) => Log.Info($"player: FFmpeg stream snapshot unavailable ({ex.GetType().Name})");
}
