using System.Globalization;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackMediaUrlTests
{
    private static readonly Uri Server = new("https://silo.example.com");

    [Theory]
    [InlineData("/api/v2/stream/session/file.mp4")]
    [InlineData("/api/v2/playback/transcode/session/master.m3u8")]
    [InlineData("/api/v2/stream/session/file.mp4?seek=204.5")]
    [InlineData("/api/v2/stream/session/file.mp4?seek=0")]
    [InlineData("/api/v2/stream/session/file.mp4?seek=1e2")]
    [InlineData("/api/v2/stream/session/file.mp4?%73eek=2%2E5")]
    public void ResolveAcceptsOnlyApiLocalMediaWithAnOptionalSeek(string raw) =>
        Assert.Equal(new Uri(Server.ToString().TrimEnd('/') + raw), PlaybackMediaUrl.Resolve(raw, Server));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("https://silo.example.com/api/v2/stream/session/file.mp4")]
    [InlineData("http://elsewhere.example.com/api/v2/stream/session/file.mp4")]
    [InlineData("//silo.example.com/api/v2/stream/session/file.mp4")]
    [InlineData("api/v2/stream/session/file.mp4")]
    [InlineData("/api/v2/account/me")]
    [InlineData("/api/v2/stream")]
    [InlineData("/api/v2/streaming/file.mp4")]
    [InlineData("/api/v2/stream/session/file.mp4#fragment")]
    [InlineData("/api/v2/stream/session/file.mp4#")]
    [InlineData("/api/v2/stream/session%2ffile.mp4")]
    [InlineData("/api/v2/stream/session%2Ffile.mp4")]
    [InlineData("/api/v2/stream/session%5cfile.mp4")]
    [InlineData("/api/v2/stream/session%5Cfile.mp4")]
    [InlineData("/api/v2/stream/session\\file.mp4")]
    [InlineData("/api/v2/stream/./file.mp4")]
    [InlineData("/api/v2/stream/../stream/file.mp4")]
    [InlineData("/api/v2/stream/%2e/file.mp4")]
    [InlineData("/api/v2/stream/%2E%2e/stream/file.mp4")]
    [InlineData("/api/v2/stream/.%2e/stream/file.mp4")]
    [InlineData("/api/v2/stream/file.mp4?")]
    [InlineData("/api/v2/stream/file.mp4?seek")]
    [InlineData("/api/v2/stream/file.mp4?seek=")]
    [InlineData("/api/v2/stream/file.mp4?seek=-1")]
    [InlineData("/api/v2/stream/file.mp4?seek=NaN")]
    [InlineData("/api/v2/stream/file.mp4?seek=Infinity")]
    [InlineData("/api/v2/stream/file.mp4?seek=1e999")]
    [InlineData("/api/v2/stream/file.mp4?seek=1,5")]
    [InlineData("/api/v2/stream/file.mp4?token=not-allowed")]
    [InlineData("/api/v2/stream/file.mp4?Seek=1")]
    [InlineData("/api/v2/stream/file.mp4?seek=1&seek=2")]
    [InlineData("/api/v2/stream/file.mp4?seek=1&%73eek=2")]
    [InlineData("/api/v2/stream/file.mp4?seek=1&other=2")]
    [InlineData("/api/v2/stream/file.mp4?seek=1&")]
    public void ResolveRejectsContractViolations(string? raw) => Assert.Null(PlaybackMediaUrl.Resolve(raw, Server));

    [Theory]
    [InlineData("https://h/silo")]
    [InlineData("https://h/silo/")]
    public void ResolvePreservesTheServerPathPrefixAndTrimsInput(string address)
    {
        var server = new Uri(address);
        Assert.Equal(new Uri("https://h/silo/api/v2/stream/session/file.mp4?seek=2"),
            PlaybackMediaUrl.Resolve("  /api/v2/stream/session/file.mp4?seek=2\n", server));
        Assert.Null(PlaybackMediaUrl.Resolve("/silo/api/v2/stream/session/file.mp4", server));
    }

    [Theory]
    [InlineData("ftp://h/silo")]
    [InlineData("https://user@h/silo")]
    [InlineData("https://user:password@h/silo")]
    [InlineData("https://h/silo?other=1")]
    [InlineData("https://h/silo#fragment")]
    [InlineData("/silo")]
    public void InvalidServersAreRejected(string address)
    {
        var server = new Uri(address, UriKind.RelativeOrAbsolute);
        Assert.Null(PlaybackMediaUrl.Resolve("/api/v2/stream/session/file.mp4", server));
        Assert.False(PlaybackMediaUrl.IsAllowedChild(new Uri("https://h/silo/api/v2/stream/session/file.mp4"), server));
    }

    [Fact]
    public void SeekParsingIsInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.NotNull(PlaybackMediaUrl.Resolve("/api/v2/stream/file.mp4?seek=1.5", Server));
            Assert.Null(PlaybackMediaUrl.Resolve("/api/v2/stream/file.mp4?seek=1,5", Server));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("https://h/silo/api/v2/stream/session/segment.ts")]
    [InlineData("https://H:443/silo/api/v2/stream/session/child.m3u8?sequence=2&grant=opaque&grant=other")]
    [InlineData("https://h/silo/api/v2/playback/transcode/session/segment.ts?seek=NaN")]
    public void ChildrenAllowServerMintedQueriesOnTheSameOriginAndPrefix(string raw) =>
        Assert.True(PlaybackMediaUrl.IsAllowedChild(new Uri(raw), new Uri("https://h/silo")));

    [Theory]
    [InlineData("/silo/api/v2/stream/session/segment.ts")]
    [InlineData("http://h/silo/api/v2/stream/session/segment.ts")]
    [InlineData("https://elsewhere.example.com/silo/api/v2/stream/session/segment.ts")]
    [InlineData("https://h:444/silo/api/v2/stream/session/segment.ts")]
    [InlineData("https://user@h/silo/api/v2/stream/session/segment.ts")]
    [InlineData("https://h/api/v2/stream/session/segment.ts")]
    [InlineData("https://h/silox/api/v2/stream/session/segment.ts")]
    [InlineData("https://h/silo/api/v2/account/me")]
    [InlineData("https://h/silo/api/v2/streaming/file.ts")]
    [InlineData("https://h/silo/api/v2/stream/session/segment.ts#fragment")]
    [InlineData("https://h/silo/api/v2/stream/session%2Fsegment.ts")]
    [InlineData("https://h/silo/api/v2/stream/session%5csegment.ts")]
    [InlineData("https://h/silo/api/v2/stream/session\\segment.ts")]
    [InlineData("https://h/silo/api/v2/stream/./segment.ts")]
    [InlineData("https://h/silo/api/v2/stream/../stream/segment.ts")]
    [InlineData("https://h/silo/api/v2/stream/%2E%2e/stream/segment.ts")]
    public void ChildrenCannotEscapeTheMediaBoundaryEvenAfterUriNormalization(string raw) =>
        Assert.False(PlaybackMediaUrl.IsAllowedChild(new Uri(raw, UriKind.RelativeOrAbsolute), new Uri("https://h/silo")));
}
