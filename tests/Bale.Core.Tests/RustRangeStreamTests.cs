using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>
/// Slice 7 of the Rust port (docs/rust-port.md): the Rust range stream against <see cref="HttpRangeStream"/>
/// over a real loopback HTTP server, since the Rust side can't use an in-memory .NET handler.
/// </summary>
public sealed class RustRangeStreamTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 9 * 1024 * 1024 + 12345).Select(i => (byte)(i * 31 % 251)).ToArray();

    private static SiloClient Client(LoopbackMediaServer server, long expiresIn = 3600)
    {
        var client = new SiloClient(server.Uri, PlaybackTestSupport.Identity);
        client.SignIn(new TokenPair { AccessToken = "test-access", RefreshToken = "test-refresh", ExpiresIn = expiresIn,
                User = new Account { Id = "account" } },
            profileId: "profile", profileToken: "test-profile-proof");
        return client;
    }

    private static PlaybackPlan Plan() => PlaybackMediaTestSupport.Plan();

    /// <summary>Reads the same random seek/read sequence and checks every byte against the payload.</summary>
    private static void ReadRandomly(Stream stream, int seed, byte[] payload)
    {
        var random = new Random(seed);
        var buffer = new byte[3 * 1024 * 1024];
        for (var step = 0; step < 120; step++)
        {
            if (random.Next(4) == 0) stream.Position = random.NextInt64(0, payload.Length + 10);
            var size = random.Next(4) switch { 0 => random.Next(1, 64), 1 => 64 * 1024, 2 => random.Next(1, 1_500_000), _ => buffer.Length };
            var at = stream.Position;
            var count = stream.Read(buffer, 0, size);
            if (at >= payload.Length) Assert.Equal(0, count);
            else
            {
                Assert.InRange(count, 1, size);
                Assert.True(buffer.AsSpan(0, count).SequenceEqual(payload.AsSpan((int)at, count)), $"seed {seed} step {step} at {at}");
            }
        }
    }

    [Fact]
    public async Task RandomReadsMatchThePayloadForBothStacks()
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        for (var seed = 0; seed < 3; seed++)
        {
            await using (var csharp = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default)) ReadRandomly(csharp, seed, Payload);
            await using var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
            Assert.True(rust.SupportsRanges);
            Assert.Equal(Payload.Length, rust.Length);
            ReadRandomly(rust, seed, Payload);
        }
    }

    [Fact]
    public async Task SequentialReadsFetchEachBlockOnceLikeTheCSharpStream()
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        var blocks = (Payload.Length + (1 << 20) - 1) >> 20;
        async Task<(long Fetches, long Hits, long Bytes, long PrefetchHits)> Run(bool rust, int pauseMs = 0)
        {
            var media = client.Playback.OpenMedia(Plan());
            Stream stream = rust ? await media.OpenNativeStreamAsync(default) : await media.OpenStreamAsync(default);
            await using (stream)
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    Assert.True(buffer.AsSpan(0, count).SequenceEqual(Payload.AsSpan((int)total, count)));
                    total += count;
                    // Playback consumes far slower than memcpy: give speculation time to land.
                    if (pauseMs > 0) Thread.Sleep(pauseMs);
                }
                return stream switch
                {
                    RustRangeStream r => (r.BlockFetches, r.CacheHits, r.BytesRead, r.PrefetchHits),
                    HttpRangeStream h => (h.BlockFetches, h.CacheHits, h.BytesRead, h.PrefetchHits),
                    _ => throw new InvalidOperationException(),
                };
            }
        }
        var csharp = await Run(rust: false, pauseMs: 2);
        var rust = await Run(rust: true, pauseMs: 2);
        var (fastCSharp, fastRust) = (await Run(rust: false), await Run(rust: true));
        Console.WriteLine($"prefetch hits: paced C# {csharp.PrefetchHits} Rust {rust.PrefetchHits}; unpaced C# {fastCSharp.PrefetchHits} Rust {fastRust.PrefetchHits} of {blocks} blocks");
        Assert.Equal(blocks, csharp.Fetches);
        Assert.Equal(blocks, rust.Fetches);
        Assert.Equal(Payload.Length, rust.Bytes);
        Assert.Equal(csharp.Bytes, rust.Bytes);
        Assert.True(rust.Hits > 0);
        // Reading straight through, both stacks find most blocks already fetched ahead.
        Assert.True(csharp.PrefetchHits > blocks / 2, $"C# prefetch hits {csharp.PrefetchHits}");
        Assert.True(rust.PrefetchHits > blocks / 2, $"Rust prefetch hits {rust.PrefetchHits}");
    }

    /// <summary>
    /// Block fetches must reuse pooled connections. On the console a fresh connection per block (TCP and
    /// TLS handshakes, then slow start) cut Rust's per-block rate ~10x and multiplied stream waits.
    /// </summary>
    [Fact]
    public async Task BlockFetchesReuseConnections()
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        await using (var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default))
        {
            var buffer = new byte[64 * 1024];
            while (rust.Read(buffer, 0, buffer.Length) > 0) { }
            Assert.Equal(10, rust.BlockFetches);
        }
        // The open-ended probe's connection closes unread; the three workers then keep theirs.
        Assert.InRange(server.Connections, 1, 5);
    }

    [Fact]
    public async Task AServerWithoutRangesGetsTheSequentialReader()
    {
        using var server = new LoopbackMediaServer(Payload[..(2 * 1024 * 1024)]) { IgnoreRanges = true };
        using var client = Client(server);
        await using var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
        Assert.False(rust.SupportsRanges);
        ReadRandomly(rust, 7, Payload[..(2 * 1024 * 1024)]);
    }

    [Fact]
    public async Task ADemandBlockFailingOnceIsRetried()
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        await using var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
        server.Faults.Enqueue((4 << 20, Fault.Status500));
        server.Faults.Enqueue((5 << 20, Fault.Truncate));
        rust.Position = 4 << 20;
        var buffer = new byte[2 << 20];
        var count = rust.Read(buffer, 0, buffer.Length);
        Assert.True(buffer.AsSpan(0, count).SequenceEqual(Payload.AsSpan(4 << 20, count)));
        rust.Position = 5 << 20;
        count = rust.Read(buffer, 0, buffer.Length);
        Assert.True(buffer.AsSpan(0, count).SequenceEqual(Payload.AsSpan(5 << 20, count)));
    }

    [Fact]
    public async Task ABlockFailingTwiceOrAWrongRangeFailsTheRead()
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        await using var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
        server.Faults.Enqueue((6 << 20, Fault.Status500));
        server.Faults.Enqueue((6 << 20, Fault.Status500));
        rust.Position = 6 << 20;
        await Assert.ThrowsAsync<HttpRequestException>(() => Task.Run(() => rust.Read(new byte[100], 0, 100)));
        // A fresh stream, at a block the first one never fetches: its prefetch of blocks 7 and 8 can
        // still be in flight and would take faults queued for them.
        await using var fresh = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
        server.Faults.Enqueue((3 << 20, Fault.WrongRange));
        server.Faults.Enqueue((3 << 20, Fault.WrongRange));
        fresh.Position = 3 << 20;
        await Assert.ThrowsAsync<IOException>(() => Task.Run(() => fresh.Read(new byte[100], 0, 100)));
    }

    [Fact]
    public async Task DisposingWakesAStalledRead()
    {
        using var server = new LoopbackMediaServer(Payload) { StallBlockRequests = true };
        using var client = Client(server);
        var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
        var read = Task.Run(() => rust.Read(new byte[100], 0, 100));
        await Task.Delay(300);
        Assert.False(read.IsCompleted);
        rust.Dispose();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<ObjectDisposedException>(error);
    }

    [Fact]
    public async Task RustSendsTheSameIdentityAndCredentialHeaders()
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        await using (var csharp = await client.Playback.OpenMedia(Plan()).OpenStreamAsync(default)) csharp.ReadByte();
        var expected = server.Headers.ToArray();
        server.Headers.Clear();
        await using (var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default)) rust.ReadByte();
        var actual = server.Headers.ToArray();
        foreach (var name in new[] { "authorization", "x-profile-id", "x-profile-token", "x-silo-device-id", "x-silo-client", "user-agent" })
        {
            Assert.Contains(expected, header => header.Key == name);
            Assert.Equal(expected.First(header => header.Key == name).Value, actual.First(header => header.Key == name).Value);
        }
    }

    [Fact]
    public async Task A401MidStreamRefreshesOnceAndRetries()
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        await using var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default, prefetchBlocks: 4);
        Assert.Equal(1, rust.Read(new byte[1], 0, 1));
        // The server revokes the bearer; the refresh endpoint hands out its replacement.
        server.AcceptedBearer = server.RefreshedBearer = "rotated-access";
        rust.Position = 5 << 20;
        var buffer = new byte[2 << 20];
        var count = rust.Read(buffer, 0, buffer.Length);
        Assert.True(buffer.AsSpan(0, count).SequenceEqual(Payload.AsSpan(5 << 20, count)));
        // The demand fetch and its prefetches all met the 401; one refresh served them.
        Assert.Equal(1, server.Refreshes);
        Assert.Equal("rotated-access", client.AccessToken);
        Assert.True(server.Rejections >= 1);
    }

    [Fact]
    public async Task A401AfterTheRefreshFailsTheRead()
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        await using var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
        server.AcceptedBearer = "nothing-the-refresh-returns";
        server.RefreshedBearer = "rotated-access";
        rust.Position = 3 << 20;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Task.Run(() => rust.Read(new byte[100], 0, 100))
            .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.True(server.Refreshes >= 1);
    }

    [Fact]
    public async Task A401AtOpenRefreshesAndRetries()
    {
        using var server = new LoopbackMediaServer(Payload) { AcceptedBearer = "rotated-access", RefreshedBearer = "rotated-access" };
        using var client = Client(server);
        await using var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
        Assert.Equal(Payload.Length, rust.Length);
        Assert.Equal(Payload[0], rust.ReadByte());
        Assert.Equal(1, server.Refreshes);
    }

    [Fact]
    public async Task TheHeaderCheckHandsOverATokenRefreshedAheadOfExpiry()
    {
        using var server = new LoopbackMediaServer(Payload) { RefreshedBearer = "first-refresh", RefreshExpiresIn = 60 };
        // Inside the session's two-minute refresh margin: opening refreshes once.
        using var client = Client(server, expiresIn: 60);
        await using var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
        Assert.Equal(1, server.Refreshes);
        server.AcceptedBearer = server.RefreshedBearer = "second-refresh";
        await rust.CheckHeadersAsync(default);
        Assert.Equal(2, server.Refreshes);
        rust.Position = 4 << 20;
        Assert.Equal(Payload[4 << 20], rust.ReadByte());
        Assert.Equal(0, server.Rejections);
    }

    [Fact]
    public async Task ARefreshForStaleHeadersIsIgnored()
    {
        using var server = new LoopbackMediaServer(Payload) { RefreshedBearer = "first-refresh", RefreshExpiresIn = 60 };
        using var client = Client(server, expiresIn: 60);
        await using var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
        server.RefreshedBearer = "second-refresh";
        await rust.CheckHeadersAsync(default);
        var refreshes = server.Refreshes;
        // Generation 0's headers were already replaced: a late callback for them must not refresh again.
        await rust.RefreshAfterRejectionAsync(0);
        Assert.Equal(refreshes, server.Refreshes);
    }

    private static (string? Attempt, long Ms)[] RequestsFor(LoopbackMediaServer server, string range) =>
        server.Requests.Where(r => r.Range == range)
            .Select(r => (r.Headers.Where(h => h.Key == "retry-attempt").Select(h => h.Value).FirstOrDefault(), r.Ms)).ToArray();

    /// <summary>
    /// Busy answers (429/503/504) are retried as Kiota's RetryHandler retries the C# stream's: after
    /// Retry-After, with Retry-Attempt numbered from 1. The last block, so no prefetch shares the script.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyAnswersAreRetriedAfterRetryAfter(bool rust)
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        var media = client.Playback.OpenMedia(Plan());
        await using Stream stream = rust ? await media.OpenNativeStreamAsync(default) : await media.OpenStreamAsync(default);
        server.ScriptedBlockReplies.Enqueue("429 Too Many Requests\r\nRetry-After: 1");
        server.ScriptedBlockReplies.Enqueue("503 Service Unavailable\r\nRetry-After: 0");
        stream.Position = 9 << 20;
        var buffer = new byte[100];
        Assert.Equal(100, await Task.Run(() => stream.Read(buffer, 0, 100)));
        Assert.True(buffer.AsSpan().SequenceEqual(Payload.AsSpan(9 << 20, 100)));
        var requests = RequestsFor(server, $"bytes={9 << 20}-{Payload.Length - 1}");
        Assert.Equal([null, "1", "2"], requests.Select(r => r.Attempt));
        Assert.InRange(requests[1].Ms - requests[0].Ms, 900, 2500);
    }

    /// <summary>After three retries the read fails, without the stream's own demand retry (as in C#).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARequestStillBusyAfterThreeRetriesFailsTheRead(bool rust)
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        var media = client.Playback.OpenMedia(Plan());
        await using Stream stream = rust ? await media.OpenNativeStreamAsync(default) : await media.OpenStreamAsync(default);
        for (var i = 0; i < 6; i++) server.ScriptedBlockReplies.Enqueue("504 Gateway Timeout\r\nRetry-After: 0");
        stream.Position = 9 << 20;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => stream.Read(new byte[100], 0, 100)));
        if (rust) Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, Assert.IsType<HttpRequestException>(error).StatusCode);
        Assert.Equal([null, "1", "2", "3"], RequestsFor(server, $"bytes={9 << 20}-{Payload.Length - 1}").Select(r => r.Attempt));
    }

    [Fact]
    public async Task DisposingCutsARetryWaitShort()
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        var rust = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
        server.ScriptedBlockReplies.Enqueue("503 Service Unavailable\r\nRetry-After: 60");
        rust.Position = 9 << 20;
        var read = Task.Run(() => rust.Read(new byte[100], 0, 100));
        await Task.Delay(500);
        Assert.False(read.IsCompleted);
        rust.Dispose();
        Assert.IsType<ObjectDisposedException>(await Assert.ThrowsAnyAsync<Exception>(() => read.WaitAsync(TimeSpan.FromSeconds(5))));
    }

    internal enum Fault { Status500, Truncate, WrongRange }

    /// <summary>A minimal HTTP/1.1 server with byte ranges, keep-alive and injectable faults.</summary>
    internal sealed class LoopbackMediaServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _payload;

        public LoopbackMediaServer(byte[] payload)
        {
            _payload = payload;
            _listener.Start();
            Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _ = AcceptAsync();
        }

        public Uri Uri { get; }
        public bool IgnoreRanges { get; init; }
        /// <summary>Block (ranged, non-open) requests never get a response.</summary>
        public bool StallBlockRequests { get; init; }
        /// <summary>Faults for the next ranged request starting at an offset, in order.</summary>
        public ConcurrentQueue<(long Start, Fault Fault)> Faults { get; } = new();
        public ConcurrentQueue<KeyValuePair<string, string>> Headers { get; } = new();
        /// <summary>The only bearer media requests accept, once set; others get 401.</summary>
        public volatile string? AcceptedBearer;
        /// <summary>The access token <c>/api/v2/auth/refresh</c> returns.</summary>
        public volatile string RefreshedBearer = "refreshed-access";
        public long RefreshExpiresIn { get; init; } = 3600;
        /// <summary>Requests without a Range (the progressive open) get 409.</summary>
        public bool RefuseUnranged { get; init; }
        /// <summary>Unranged bodies stop halfway and the connection closes.</summary>
        public bool TruncateUnranged { get; init; }
        /// <summary>Status lines (with extra headers) for the next ranged block requests, in order.</summary>
        public ConcurrentQueue<string> ScriptedBlockReplies { get; } = new();
        /// <summary>Status lines for the next unranged media requests (progressive opens), in order.</summary>
        public ConcurrentQueue<string> ScriptedUnrangedReplies { get; } = new();
        /// <summary>Every request's arrival time (ms since start), range and headers.</summary>
        public ConcurrentQueue<(long Ms, string? Range, KeyValuePair<string, string>[] Headers)> Requests { get; } = new();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        /// <summary>Unranged bodies send this many bytes, then nothing more.</summary>
        public int? StallUnrangedAfter { get; init; }
        private int _refreshes, _rejections;
        private int _healthChecks;
        public int HealthChecks => Volatile.Read(ref _healthChecks);
        public int Refreshes => Volatile.Read(ref _refreshes);
        /// <summary>Media requests answered 401.</summary>
        public int Rejections => Volatile.Read(ref _rejections);
        private int _connections;
        /// <summary>TCP connections accepted so far.</summary>
        public int Connections => Volatile.Read(ref _connections);

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _connections);
                    _ = ServeAsync(socket);
                }
            }
            catch (Exception) { }
        }

        private async Task ServeAsync(TcpClient socket)
        {
            using (socket)
            {
                var stream = socket.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(_stop.Token);
                        if (string.IsNullOrEmpty(line)) return;
                        string? range = null, authorization = null;
                        var contentLength = 0;
                        var requestHeaders = new List<KeyValuePair<string, string>>();
                        string? header;
                        while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(_stop.Token)))
                        {
                            var colon = header.IndexOf(':');
                            var name = header[..colon].Trim().ToLowerInvariant();
                            var value = header[(colon + 1)..].Trim();
                            Headers.Enqueue(new(name, value));
                            requestHeaders.Add(new(name, value));
                            if (name == "range") range = value;
                            if (name == "authorization") authorization = value;
                            if (name == "content-length") contentLength = int.Parse(value);
                        }
                        if (contentLength > 0) await reader.ReadBlockAsync(new char[contentLength], _stop.Token);
                        Requests.Enqueue((_clock.ElapsedMilliseconds, range, requestHeaders.ToArray()));
                        if (range is null && ScriptedUnrangedReplies.TryDequeue(out var unranged))
                        {
                            await WriteAsync(stream, $"HTTP/1.1 {unranged}\r\nContent-Length: 0\r\n\r\n");
                            continue;
                        }
                        if (range is not null && range.Contains('-') && !range.EndsWith('-') && ScriptedBlockReplies.TryDequeue(out var scripted))
                        {
                            await WriteAsync(stream, $"HTTP/1.1 {scripted}\r\nContent-Length: 0\r\n\r\n");
                            continue;
                        }
                        if (line.Contains("/api/v2/auth/refresh", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref _refreshes);
                            var json = $$$"""{"access_token":"{{{RefreshedBearer}}}","refresh_token":"rt-{{{Refreshes}}}","expires_in":{{{RefreshExpiresIn}}}}""";
                            await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\n\r\n{json}");
                            continue;
                        }
                        if (line.Contains("/api/v1/health", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref _healthChecks);
                            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 15\r\n\r\n{\"status\":\"ok\"}");
                            continue;
                        }
                        if (AcceptedBearer is { } accepted && authorization != $"Bearer {accepted}")
                        {
                            Interlocked.Increment(ref _rejections);
                            await WriteAsync(stream, "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n");
                            continue;
                        }
                        if (!await RespondAsync(stream, range)) return;
                    }
                }
                catch (Exception) { }
            }
        }

        /// <returns>False when the connection must close.</returns>
        private async Task<bool> RespondAsync(NetworkStream stream, string? range)
        {
            long total = _payload.Length, start = 0, end = total - 1;
            var ranged = !IgnoreRanges && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal);
            if (ranged)
            {
                var parts = range![6..].Split('-');
                start = long.Parse(parts[0]);
                if (parts[1].Length > 0) end = Math.Min(long.Parse(parts[1]), total - 1);
                if (parts[1].Length > 0 && StallBlockRequests)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }
            }
            if (range is null && RefuseUnranged)
            {
                await WriteAsync(stream, "HTTP/1.1 409 Conflict\r\nContent-Length: 0\r\n\r\n");
                return true;
            }
            Fault? fault = null;
            if (ranged && Faults.TryPeek(out var next) && next.Start == start && Faults.TryDequeue(out next)) fault = next.Fault;
            if (fault == Fault.Status500)
            {
                await WriteAsync(stream, "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n");
                return true;
            }
            var length = end - start + 1;
            var head = new StringBuilder(ranged ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            if (ranged)
            {
                var reportedStart = fault == Fault.WrongRange ? start + 1 : start;
                head.Append($"Content-Range: bytes {reportedStart}-{end}/{total}\r\n");
            }
            head.Append($"Content-Length: {length}\r\nContent-Type: video/mp4\r\n\r\n");
            await WriteAsync(stream, head.ToString());
            if (!ranged && StallUnrangedAfter is { } stall)
            {
                await stream.WriteAsync(_payload.AsMemory(0, stall), _stop.Token);
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }
            if (!ranged && TruncateUnranged)
            {
                await stream.WriteAsync(_payload.AsMemory(0, (int)(length / 2)), _stop.Token);
                return false;
            }
            var sent = fault == Fault.Truncate ? length / 2 : length;
            await stream.WriteAsync(_payload.AsMemory((int)start, (int)sent), _stop.Token);
            return fault != Fault.Truncate;
        }

        private Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token).AsTask();

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
