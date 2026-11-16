using Bale.Core.Api.Models;
using Bale.Core.Playback;
using static Bale.Core.Tests.RustRangeStreamTests;

namespace Bale.Core.Tests;

/// <summary>
/// Slice 7 step 5 of the Rust port (docs/rust-port.md): the Rust forward-only stream against
/// <see cref="ProgressiveHttpStream"/> over the loopback server. Same contract: no Range, reads never
/// throw, and Faulted tells an ended transport from the title's end.
/// </summary>
public sealed class RustProgressiveStreamTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 3 * 1024 * 1024 + 777).Select(i => (byte)(i * 17 % 253)).ToArray();

    private static SiloClient Client(LoopbackMediaServer server)
    {
        var client = new SiloClient(server.Uri, PlaybackTestSupport.Identity);
        client.SignIn(new TokenPair { AccessToken = "test-access", RefreshToken = "test-refresh", ExpiresIn = 3600,
                User = new Account { Id = "account" } },
            profileId: "profile", profileToken: "test-profile-proof");
        return client;
    }

    private static PlaybackPlan Plan() => new()
    {
        Delivery = PlaybackProgressive.Delivery,
        Stream = new StreamV3
        {
            Url = "/api/v2/stream/session?seek=118&remux_range=refuse",
            Protocol = "http_progressive", MimeType = "video/mp4", Container = "mp4",
        },
        Source = new PlaybackSource { DurationSeconds = 1800, VideoCodec = "hevc", AudioCodec = "truehd", Container = "m2ts" },
        EffectiveRecipe = new EffectiveRecipeV3 { VideoCodec = "hevc", AudioCodec = "aac" },
    };

    private static async Task<MediaProgressiveStream> Open(SiloClient client, bool rust)
    {
        var media = client.Playback.OpenMedia(Plan());
        Assert.True(media.IsProgressive);
        return rust ? await media.OpenNativeProgressiveAsync(default) : await media.OpenProgressiveAsync(default);
    }

    /// <summary>Reads in odd sizes, as FFmpeg's AVIO does, until the end of the data.</summary>
    private static async Task<byte[]> ReadAll(MediaProgressiveStream stream)
    {
        var output = new MemoryStream();
        var buffer = new byte[100_003];
        var random = new Random(5);
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(0, random.Next(1, buffer.Length)))) > 0) output.Write(buffer, 0, count);
        return output.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsTheWholeResponseWithoutARange(bool rust)
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        await using var stream = await Open(client, rust);
        var bytes = await ReadAll(stream);
        Assert.True(bytes.AsSpan().SequenceEqual(Payload));
        Assert.True(stream.Completed);
        Assert.False(stream.Faulted);
        Assert.Equal(Payload.Length, stream.DownloadedBytes);
        Assert.Equal(Payload.Length, stream.Position);
        Assert.Equal(0, stream.BufferedBytes);
        Assert.DoesNotContain(server.Headers, header => header.Key == "range");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATruncatedResponseEndsTheDataAsAFault(bool rust)
    {
        using var server = new LoopbackMediaServer(Payload) { TruncateUnranged = true };
        using var client = Client(server);
        await using var stream = await Open(client, rust);
        var bytes = await ReadAll(stream);
        Assert.True(bytes.AsSpan().SequenceEqual(Payload.AsSpan(0, bytes.Length)));
        Assert.True(bytes.Length < Payload.Length);
        Assert.True(stream.Faulted);
        Assert.False(stream.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A409IsARefusedRange(bool rust)
    {
        using var server = new LoopbackMediaServer(Payload) { RefuseUnranged = true };
        using var client = Client(server);
        await Assert.ThrowsAsync<ProgressiveRangeRefusedException>(() => Open(client, rust));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABusyOpenIsRetried(bool rust)
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        server.ScriptedUnrangedReplies.Enqueue("503 Service Unavailable\r\nRetry-After: 0");
        await using var stream = await Open(client, rust);
        Assert.True((await ReadAll(stream)).AsSpan().SequenceEqual(Payload));
        Assert.Equal([null, "1"], server.Requests.Where(r => r.Range is null && !r.Headers.Any(h => h.Key == "content-length"))
            .Where(r => r.Headers.Any(h => h.Key == "x-profile-id"))
            .Select(r => r.Headers.Where(h => h.Key == "retry-attempt").Select(h => h.Value).FirstOrDefault()));
    }

    [Fact]
    public async Task A401AtOpenRefreshesAndRetries()
    {
        using var server = new LoopbackMediaServer(Payload) { AcceptedBearer = "rotated-access", RefreshedBearer = "rotated-access" };
        using var client = Client(server);
        await using var stream = await Open(client, rust: true);
        Assert.True((await ReadAll(stream)).AsSpan().SequenceEqual(Payload));
        Assert.Equal(1, server.Refreshes);
    }

    [Fact]
    public async Task DisposingEndsABlockedReadAndMarksTheFault()
    {
        using var server = new LoopbackMediaServer(Payload) { StallUnrangedAfter = 1000 };
        using var client = Client(server);
        var stream = await Open(client, rust: true);
        var buffer = new byte[4096];
        var first = 0;
        while (first < 1000) first += stream.Read(buffer, first, 1000 - first);
        var blocked = Task.Run(() => stream.Read(buffer, 0, buffer.Length));
        await Task.Delay(300);
        Assert.False(blocked.IsCompleted);
        stream.Dispose();
        Assert.Equal(0, await blocked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stream.Faulted);
        Assert.False(stream.Completed);
        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
    }

    /// <summary>
    /// A seek drops the remux's connection; the warm-up done during the replan must leave one in the
    /// pool for the next open, or it pays TCP and TLS handshakes (~0.4 s on the console).
    /// </summary>
    [Fact]
    public async Task AWarmedConnectionServesTheNextOpen()
    {
        using var server = new LoopbackMediaServer(Payload);
        using var client = Client(server);
        RustTransport.WarmConnection(server.Uri);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.HealthChecks == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Task.Delay(100);
        Assert.Equal(1, server.HealthChecks);
        Assert.Equal(1, server.Connections);
        await using (var stream = await Open(client, rust: true))
        {
            // The open took the warmed connection.
            Assert.True((await ReadAll(stream)).AsSpan().SequenceEqual(Payload));
        }
        // The open then warmed a spare: a new connection, or the first one when the read had already
        // finished and returned it. Either way the second open needs no new connection.
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.HealthChecks < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Task.Delay(100);
        Assert.Equal(2, server.HealthChecks);
        var connections = server.Connections;
        Assert.InRange(connections, 1, 2);
        await using var second = await Open(client, rust: true);
        // The server sees the spare's health check before the client returns that connection to the
        // pool, so under load the second open can still find it busy and dial once more. Never a third.
        Assert.InRange(server.Connections, connections, 2);
    }

    [Fact]
    public async Task ReadAheadStopsAtSixteenMegabytes()
    {
        var large = new byte[24 * 1024 * 1024];
        using var server = new LoopbackMediaServer(large);
        using var client = Client(server);
        await using var stream = await Open(client, rust: true);
        // 256 chunks of at most 64 KiB (a network read may fill a chunk only partly).
        const long limit = 256L * 64 * 1024;
        long last = -1;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (stream.BufferedBytes != last && DateTime.UtcNow < deadline)
        {
            last = stream.BufferedBytes;
            await Task.Delay(300);
        }
        Assert.InRange(stream.BufferedBytes, limit / 2, limit);
        // One more chunk may sit in the pump's hands, never more.
        Assert.InRange(stream.DownloadedBytes - stream.BufferedBytes, 0, 64 * 1024);
        Assert.True(stream.DownloadedBytes < large.Length);
    }
}
