using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bale.Core.Playback;

namespace Bale.Core.Native;

/// <summary>
/// Debug only: the Rust HTTP spike's latency test (docs/rust-port.md). Replays HttpRangeStream's fetch
/// pattern (1 MiB range requests, a few in flight) through the player's own C# transport or through
/// bale_core's Rust stack, and summarizes time-to-first-byte and block times. Reports hold timings
/// only, never URLs, offsets or headers.
/// </summary>
public static class FetchPatternBenchmark
{
    public const int BlockSize = 1024 * 1024; // HttpRangeStream's block size.

    public sealed record Result(IReadOnlyList<(double TtfbMs, double TotalMs)> Blocks, IReadOnlyList<string> Errors, double WallMs);

    public static async Task<Result> RunCSharpAsync(PlaybackMedia media, long start, int blocks, int concurrency)
    {
        var samples = new List<(double, double)>();
        var errors = new List<string>();
        var next = -1;
        var wall = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            int index;
            while ((index = Interlocked.Increment(ref next)) < blocks)
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    using var response = await media.SendAsync(media.Uri, start + (long)index * BlockSize, BlockSize,
                        HttpCompletionOption.ResponseHeadersRead, CancellationToken.None).ConfigureAwait(false);
                    var ttfb = clock.Elapsed.TotalMilliseconds;
                    if ((int)response.StatusCode != 206) { lock (errors) errors.Add("status"); continue; }
                    await using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    while (await body.ReadAsync(buffer).ConfigureAwait(false) > 0) { }
                    lock (samples) samples.Add((ttfb, clock.Elapsed.TotalMilliseconds));
                }
                catch (Exception) { lock (errors) errors.Add("io"); }
            }
        }))).ConfigureAwait(false);
        return new(samples, errors, wall.Elapsed.TotalMilliseconds);
    }

    /// <summary>Blocks the calling thread; run it on its own thread.</summary>
    public static Result RunRust(Uri url, IEnumerable<KeyValuePair<string, string>> headers, long start, int blocks, int concurrency)
    {
        var report = BaleCoreNative.Call("http_probe.fetch_pattern", writer =>
        {
            writer.WriteString("url", url.AbsoluteUri);
            writer.WriteStartArray("headers");
            foreach (var (name, value) in headers)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(name);
                writer.WriteStringValue(value);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteNumber("start", start);
            writer.WriteNumber("blocks", blocks);
            writer.WriteNumber("block_size", BlockSize);
            writer.WriteNumber("concurrency", concurrency);
        }, BaleCoreJson.Default.JsonElement);
        var samples = report.GetProperty("blocks").EnumerateArray()
            .Select(block => (block.GetProperty("ttfb_ms").GetDouble(), block.GetProperty("total_ms").GetDouble())).ToList();
        var errors = report.GetProperty("errors").EnumerateArray().Select(error => error.GetString() ?? "?").ToList();
        return new(samples, errors, report.GetProperty("wall_ms").GetDouble());
    }

    /// <summary>One line: median, 95th percentile and worst time-to-first-byte and block time, slow-block counts.</summary>
    public static string Summarize(string name, Result result)
    {
        static double Percentile(IReadOnlyList<double> sorted, double p) =>
            sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
        var ttfb = result.Blocks.Select(block => block.TtfbMs).Order().ToArray();
        var total = result.Blocks.Select(block => block.TotalMs).Order().ToArray();
        var mbps = result.WallMs > 0 ? result.Blocks.Count * (double)BlockSize * 8 / result.WallMs / 1000 : 0;
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{name,-4} blocks={result.Blocks.Count} errors={result.Errors.Count}");
        if (result.Errors.Count > 0) text.Append('(').Append(string.Join(",", result.Errors.Distinct())).Append(')');
        text.Append(CultureInfo.InvariantCulture,
            $" ttfb p50={Percentile(ttfb, 0.5):0} p95={Percentile(ttfb, 0.95):0} max={(ttfb.Length > 0 ? ttfb[^1] : double.NaN):0} ms");
        text.Append(CultureInfo.InvariantCulture,
            $" | block p50={Percentile(total, 0.5):0} p95={Percentile(total, 0.95):0} max={(total.Length > 0 ? total[^1] : double.NaN):0} ms");
        text.Append(CultureInfo.InvariantCulture,
            $" | >250ms={total.Count(t => t > 250)} >500ms={total.Count(t => t > 500)} | {mbps:0} Mbps\n");
        return text.ToString();
    }
}
