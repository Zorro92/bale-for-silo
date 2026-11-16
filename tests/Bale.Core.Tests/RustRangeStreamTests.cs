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

    private static SiloClient Client(LoopbackMediaServer server)
    {
        var client = new SiloClient(server.Uri, PlaybackTestSupport.Identity);
        client.SignIn(new TokenPair { AccessToken = "test-access", ExpiresIn = 3600, User = new Account { Id = "account" } },
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
        // A fresh stream: the read above already prefetched blocks 7 and 8.
        await using var fresh = await client.Playback.OpenMedia(Plan()).OpenNativeStreamAsync(default);
        server.Faults.Enqueue((8 << 20, Fault.WrongRange));
        server.Faults.Enqueue((8 << 20, Fault.WrongRange));
        fresh.Position = 8 << 20;
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
        foreach (var name in new[] { "authorization", "x-profile-id", "x-profile-token", "x-silo-device-id", "x-silo-client" })
        {
            Assert.Contains(expected, header => header.Key == name);
            Assert.Equal(expected.First(header => header.Key == name).Value, actual.First(header => header.Key == name).Value);
        }
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

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
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
                        string? range = null;
                        string? header;
                        while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(_stop.Token)))
                        {
                            var colon = header.IndexOf(':');
                            var name = header[..colon].Trim().ToLowerInvariant();
                            var value = header[(colon + 1)..].Trim();
                            Headers.Enqueue(new(name, value));
                            if (name == "range") range = value;
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
