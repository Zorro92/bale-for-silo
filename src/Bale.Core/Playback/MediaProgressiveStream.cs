namespace Bale.Core.Playback;

/// <summary>
/// One forward-only <c>server_remux_progressive</c> response with ProgressiveHttpStream's contract: reads
/// never throw, and <see cref="Faulted"/> tells an ended transport from the title's end. The C#
/// <see cref="ProgressiveHttpStream"/> or the Rust <see cref="RustProgressiveStream"/> (docs/rust-port.md,
/// slice 7 step 5); the player and its stats read either through this type.
/// </summary>
public abstract class MediaProgressiveStream : Stream
{
    /// <summary>The response ended before the server finished it (abort, reset, stall), or was closed.</summary>
    public abstract bool Faulted { get; }

    /// <summary>The server ended the response cleanly: FFmpeg read the source to its end.</summary>
    public abstract bool Completed { get; }

    /// <summary>
    /// HLS: <see cref="Completed"/>, and no segment from the mount's start held media (native/bale-core/src/segment_media.rs).
    /// Silo answers segments past a finished transcode's real end with no media (silo-server 4bee8f8), so the mount started
    /// past the media's end: the title ended there.
    /// </summary>
    public virtual bool EndedWithoutMedia => false;

    /// <summary>Downloaded bytes not yet handed to the demuxer.</summary>
    public abstract long BufferedBytes { get; }

    /// <summary>Actual HTTP body bytes received; same meaning as MediaRangeStream.DownloadedBytes.</summary>
    public abstract long DownloadedBytes { get; }

    /// <summary>Time inside HTTP body reads, excluding time the pump waited for a full buffer to drain.</summary>
    public abstract TimeSpan DownloadTime { get; }

    /// <summary>
    /// A WinRT IInputStream that reads without managed code and never faults, as a raw ABI pointer
    /// owning one reference; 0 when this stream has none (the C# stream).
    /// </summary>
    public virtual nint CreateNativeInputStream() => 0;
}
