#if DEBUG_SERVER
using Bale.Core.Native;

namespace Bale.Xbox.Services;

/// <summary>
/// `bale-debug.sh fetchbench [blocks]`: the Rust HTTP spike's latency test (docs/rust-port.md).
/// While an original plays, fetches 1 MiB ranges three at a time from four separate regions of the
/// same file, in the order C#, Rust, Rust, C#, so neither stack gets the other's server cache.
/// </summary>
internal static class DebugFetchBench
{
    internal static async Task<(string Status, string Body)> RunAsync(Bale.Core.Playback.PlaybackMedia media, long length, int blocks)
    {
        if (Session.Client is not { } client) return ("409 Conflict", "no server\n");
        if (length < 8L * blocks * FetchPatternBenchmark.BlockSize) return ("409 Conflict", "file too small for four regions\n");
        var headers = (await client.CaptureRequestHeadersAsync(media.Uri, CancellationToken.None))
            .Concat(media.DebugStreamHeaders).ToList();
        long Region(double fraction) => (long)(length * fraction) / FetchPatternBenchmark.BlockSize * FetchPatternBenchmark.BlockSize;
        Task<FetchPatternBenchmark.Result> Rust(long start) => Task.Factory.StartNew(
            () => FetchPatternBenchmark.RunRust(media.Uri, headers, start, blocks, 3),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var body = FetchPatternBenchmark.Summarize("C#", await FetchPatternBenchmark.RunCSharpAsync(media, Region(0.20), blocks, 3))
            + FetchPatternBenchmark.Summarize("Rust", await Rust(Region(0.35)))
            + FetchPatternBenchmark.Summarize("Rust", await Rust(Region(0.50)))
            + FetchPatternBenchmark.Summarize("C#", await FetchPatternBenchmark.RunCSharpAsync(media, Region(0.65), blocks, 3));
        return ("200 OK", body);
    }
}
#endif
