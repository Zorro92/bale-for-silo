using System.Text;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using static Bale.Core.Tests.RustRangeStreamTests;

namespace Bale.Core.Tests;

/// <summary>
/// HLS through FFmpeg (docs/rust-port.md): the Rust reader (native/bale-core/src/hls.rs) turns Silo's
/// playlist into one forward-only stream: init segment, then segments from the one holding the start.
/// </summary>
public sealed class RustHlsStreamTests
{
    private const string Dir = "/api/v2/stream/session/";

    private static byte[] Bytes(string name, int length) =>
        Enumerable.Range(0, length).Select(i => (byte)((i * 7 + name.Length * 13 + name[^1]) % 251)).ToArray();

    private static readonly byte[] Init = Bytes("init", 1000);
    private static readonly byte[][] Segments = [Bytes("seg0", 300_000), Bytes("seg1", 250_001), Bytes("seg2", 123_457)];

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
        Delivery = "server_remux_hls",
        Stream = new StreamV3 { Url = Dir + "master.m3u8", Protocol = "hls", MimeType = "application/vnd.apple.mpegurl" },
        Source = new PlaybackSource { DurationSeconds = 18, VideoCodec = "hevc", AudioCodec = "aac", Container = "mkv" },
    };

    /// <summary>Silo's shape: a multivariant playlist for subtitles, a media playlist using EXT-X-DEFINE.</summary>
    private static LoopbackMediaServer Server(bool ended = true, Func<string>? media = null)
    {
        var server = new LoopbackMediaServer([1]);
        server.Files[Dir + "master.m3u8"] = () => Encoding.ASCII.GetBytes(
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"S\",URI=\"subs.m3u8\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=9000000,SUBTITLES=\"subs\"\nvideo/index.m3u8?st=token\n");
        server.Files[Dir + "video/index.m3u8"] = () => Encoding.ASCII.GetBytes(media?.Invoke() ?? MediaPlaylist(3, ended));
        server.Files[Dir + "video/init.mp4"] = () => Init;
        for (var i = 0; i < Segments.Length; i++)
        {
            var segment = Segments[i];
            server.Files[Dir + $"video/seg_{i:00000}.m4s"] = () => segment;
        }
        return server;
    }

    private static string MediaPlaylist(int count, bool ended) =>
        "#EXTM3U\n#EXT-X-VERSION:8\n#EXT-X-DEFINE:NAME=\"silo_query\",VALUE=\"st=token\"\n#EXT-X-TARGETDURATION:6\n" +
        "#EXT-X-MEDIA-SEQUENCE:0\n" + (ended ? "#EXT-X-PLAYLIST-TYPE:VOD\n" : "#EXT-X-PLAYLIST-TYPE:EVENT\n") +
        "#EXT-X-MAP:URI=\"init.mp4?{$silo_query}\"\n" +
        string.Concat(Enumerable.Range(0, count).Select(i => $"#EXTINF:6.000000,\nseg_{i:00000}.m4s?{{$silo_query}}\n")) +
        (ended ? "#EXT-X-ENDLIST\n" : "");

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        var output = new MemoryStream();
        var buffer = new byte[77_777];
        int count;
        while ((count = await stream.ReadAsync(buffer)) > 0) output.Write(buffer, 0, count);
        return output.ToArray();
    }

    private static byte[] Expected(int from) => Init.Concat(Segments.Skip(from).SelectMany(s => s)).ToArray();

    [Fact]
    public async Task PlaysInitThenEverySegmentFromTheStart()
    {
        using var server = Server();
        using var client = Client(server);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenNativeHlsAsync(0, default);
        Assert.Equal(0, stream.SegmentStartSeconds);
        Assert.True((await ReadAll(stream)).AsSpan().SequenceEqual(Expected(0)));
        Assert.True(stream.Completed);
        Assert.False(stream.Faulted);
        // Variables substituted, the variant followed, the subtitle group never fetched.
        Assert.Contains(Dir + "video/seg_00002.m4s?st=token", server.FileRequests);
        Assert.DoesNotContain(server.FileRequests, request => request.Contains("subs.m3u8"));
    }

    [Fact]
    public async Task StartsAtTheSegmentHoldingTheStartTime()
    {
        using var server = Server();
        using var client = Client(server);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenNativeHlsAsync(7.5, default);
        Assert.Equal(6, stream.SegmentStartSeconds);
        Assert.True((await ReadAll(stream)).AsSpan().SequenceEqual(Expected(1)));
        Assert.DoesNotContain(server.FileRequests, request => request.Contains("seg_00000"));
    }

    [Fact]
    public async Task AGrowingPlaylistIsReadUntilItsEnd()
    {
        var listed = 1;
        using var server = Server(media: () => MediaPlaylist(listed, ended: listed == 3));
        using var client = Client(server);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenNativeHlsAsync(0, default);
        var reading = ReadAll(stream);
        for (; listed < 3; listed++) await Task.Delay(700);
        Assert.True((await reading.WaitAsync(TimeSpan.FromSeconds(20))).AsSpan().SequenceEqual(Expected(0)));
        Assert.True(stream.Completed);
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"key\"\n")]
    [InlineData("#EXT-X-DISCONTINUITY\n")]
    public async Task APlaylistItCannotPlayKeepsWindowsHls(string tag)
    {
        using var server = Server(media: () => MediaPlaylist(3, true).Replace("#EXT-X-MAP", tag + "#EXT-X-MAP"));
        using var client = Client(server);
        await Assert.ThrowsAsync<HlsUnsupportedException>(() => client.Playback.OpenMedia(Plan()).OpenNativeHlsAsync(0, default));
    }

    [Theory]
    [InlineData("../../../users/me")]
    [InlineData("/api/v2/users/me")]
    [InlineData("https://evil.example/api/v2/stream/session/video/seg.m4s")]
    public async Task ChildrenOffTheMediaRoutesAreRefused(string uri)
    {
        using var server = Server(media: () => MediaPlaylist(3, true).Replace("init.mp4?{$silo_query}", uri));
        using var client = Client(server);
        await Assert.ThrowsAsync<HlsUnsupportedException>(() => client.Playback.OpenMedia(Plan()).OpenNativeHlsAsync(0, default));
    }

    [Fact]
    public async Task A401OnASegmentRefreshesOnceAndContinues()
    {
        using var server = Server();
        using var client = Client(server);
        await using var stream = await client.Playback.OpenMedia(Plan()).OpenNativeHlsAsync(0, default);
        server.AcceptedBearer = server.RefreshedBearer = "rotated-access";
        Assert.True((await ReadAll(stream)).AsSpan().SequenceEqual(Expected(0)));
        Assert.True(stream.Completed);
        Assert.Equal(1, server.Refreshes);
    }

    [Fact]
    public async Task BusySegmentsRetryAndAMissingOneFaultsTheStream()
    {
        using var server = Server();
        server.FileReplies[Dir + "video/seg_00001.m4s"] = new(["503 Service Unavailable\r\nRetry-After: 0"]);
        using var client = Client(server);
        await using (var stream = await client.Playback.OpenMedia(Plan()).OpenNativeHlsAsync(0, default))
            Assert.True((await ReadAll(stream)).AsSpan().SequenceEqual(Expected(0)));
        server.FileReplies[Dir + "video/seg_00001.m4s"] = new(["404 Not Found"]);
        await using var broken = await client.Playback.OpenMedia(Plan()).OpenNativeHlsAsync(0, default);
        var bytes = await ReadAll(broken);
        Assert.Equal(Init.Length + Segments[0].Length, bytes.Length);
        Assert.True(broken.Faulted);
        Assert.False(broken.Completed);
    }

    [Fact]
    public async Task DisposingEndsAReadWaitingForAGrowingPlaylist()
    {
        using var server = Server(media: () => MediaPlaylist(1, ended: false));
        using var client = Client(server);
        var stream = await client.Playback.OpenMedia(Plan()).OpenNativeHlsAsync(0, default);
        var buffer = new byte[1 << 20];
        var total = 0;
        while (total < Init.Length + Segments[0].Length) total += stream.Read(buffer, 0, buffer.Length);
        var blocked = Task.Run(() => stream.Read(buffer, 0, buffer.Length));
        await Task.Delay(500);
        Assert.False(blocked.IsCompleted);
        stream.Dispose();
        Assert.Equal(0, await blocked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stream.Faulted);
    }
}
