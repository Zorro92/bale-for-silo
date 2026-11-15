#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Native;

namespace Bale.Xbox.Services;

/// <summary>
/// `bale-debug.sh rusthttp`: the Rust HTTP spike (docs/rust-port.md). Downloads the speed-test probe
/// with bale_core's Rust stack and the exact headers the C# client would send, for 10 s after the
/// first byte. Compare with `speed test` (C# SocketsHttpHandler), and run `threads` alongside each.
/// </summary>
internal static class DebugRustHttp
{
    internal static async Task<(string Status, string Body)> RunAsync()
    {
        if (Session.Client is not { } client) return ("409 Conflict", "no server\n");
        if (client.Playback.Speed.Schedule is not { } schedule) return ("409 Conflict", "no speed-test schedule (not read yet)\n");
        // The accurate test's size; the 10 s limit ends it first on any link under 800 Mbps.
        var builder = new UriBuilder(schedule.ProbeUrl);
        var query = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("size=", StringComparison.Ordinal));
        builder.Query = string.Join('&', query.Append("size=" + 1_000_000_000L.ToString(CultureInfo.InvariantCulture)));
        var uri = builder.Uri;
        var headers = (await client.CaptureRequestHeadersAsync(uri, CancellationToken.None)).ToList();
        headers.Add(new("Accept", "application/json"));
        headers.Add(new("Cache-Control", "no-cache, no-store"));
        // Its own thread: the probe blocks, and its CPU report measures that thread alone.
        var report = await Task.Factory.StartNew(() => RustHttpProbe.Run(uri, headers, 10_000),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return ("200 OK", report + "\n");
    }
}
#endif
