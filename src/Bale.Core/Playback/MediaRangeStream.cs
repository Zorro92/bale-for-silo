namespace Bale.Core.Playback;

/// <summary>
/// A seekable, read-only media body with HttpRangeStream's diagnostics: the C# <see cref="HttpRangeStream"/>
/// or the Rust <see cref="RustRangeStream"/> (docs/rust-port.md, slice 7). The player and its stats read
/// either through this type.
/// </summary>
public abstract class MediaRangeStream : Stream
{
    public abstract int PrefetchBlocks { get; }
    public abstract bool SupportsRanges { get; }

    /// <summary>Reads that awaited an unfinished block fetch, including joined prefetches; excludes cache hits.</summary>
    public abstract long DemandWaits { get; }
    public abstract TimeSpan DemandWaitTime { get; }
    public abstract TimeSpan MaxDemandWaitTime { get; }

    /// <summary>Actual HTTP body bytes, including prefetch and skipped bytes; cache reads do not count.</summary>
    public abstract long DownloadedBytes { get; }

    /// <summary>Time inside HTTP body reads, summed across concurrent fetches.</summary>
    public abstract TimeSpan DownloadTime { get; }

    public abstract long BlockAllocations { get; }
    public abstract long BlockReuses { get; }

    // Diagnostics only: counts, never offsets tied to URLs. PrefetchHits counts first use of ready prefetched blocks.
    public abstract long Reads { get; }
    public abstract long BlockFetches { get; }
    public abstract long CacheHits { get; }
    public abstract long PrefetchHits { get; }
    public abstract long BytesRead { get; }
    public abstract long SmallestRead { get; }

    /// <summary>
    /// A WinRT IRandomAccessStream that reads without managed code, as a raw ABI pointer owning one
    /// reference; 0 when this stream has none (the C# stream).
    /// </summary>
    public virtual nint CreateNativeRandomAccessStream() => 0;

    /// <summary>
    /// The latest read failed (not by disposal) and no read has succeeded since. A demuxer takes a failed
    /// read for the end of the file, so the player's MediaEnded checks this before it treats the end of
    /// the media as the end of the title.
    /// </summary>
    public abstract bool LastReadFailed { get; }
}
