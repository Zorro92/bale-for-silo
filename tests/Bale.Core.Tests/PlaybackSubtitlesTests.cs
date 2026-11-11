using System.Net;
using System.Text;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class PlaybackSubtitlesTests
{
    private static readonly Uri Server = new("https://silo.example.com");
    private const string AssUrl = "/api/v2/stream/session/subtitles/3.ass?file_id=42";
    private const string FontsUrl = "/api/v2/stream/session/subtitles/3/fonts?file_id=42";

    private static PlaybackPlan Plan(string mode = "render", string? url = AssUrl, string? format = "ass",
        string? mime = "text/x-ssa", string? fonts = FontsUrl, double? origin = 0)
    {
        var plan = PlaybackMediaTestSupport.Plan();
        plan.Subtitle = new SubtitleDecisionV3
        {
            Mode = mode,
            TrackId = "file:42:subtitle:3",
            Artifact = url is null ? null : new SubtitleArtifactV3 { Url = url, Format = format, MimeType = mime, TimingOriginSeconds = origin },
            Inventory =
            [
                new SubtitleInventoryItemV3 { TrackId = "file:42:subtitle:2", FontBundleUrl = "/api/v2/stream/session/subtitles/2/fonts?file_id=42" },
                new SubtitleInventoryItemV3 { TrackId = "file:42:subtitle:3", FontBundleUrl = fonts },
            ],
        };
        return plan;
    }

    [Theory]
    [InlineData("render")]
    [InlineData("convert")]
    public void ARenderedAssTrackResolvesItsSidecarAndItsOwnFontBundle(string mode)
    {
        var source = PlaybackSubtitleResolver.Resolve(Plan(mode, origin: 1.5), Server)!;
        Assert.Equal(new Uri("https://silo.example.com" + AssUrl), source.Uri);
        Assert.Equal(new Uri("https://silo.example.com" + FontsUrl), source.FontBundle);
        Assert.Equal(PlaybackSubtitleSource.Ass, source.Format);
        Assert.Equal("ass", source.FormatName);
        Assert.Equal(mode, source.Mode);
        Assert.Equal(1.5, source.TimingOriginSeconds);
        Assert.Equal(source.CacheKey, PlaybackSubtitleResolver.Resolve(Plan(mode), Server)!.CacheKey);
        Assert.NotEqual(source.CacheKey, PlaybackSubtitleResolver.Resolve(Plan(mode, fonts: null), Server)!.CacheKey);
        Assert.NotEqual(source.CacheKey,
            PlaybackSubtitleResolver.Resolve(Plan(mode, url: "/api/v2/stream/session/subtitles/4.ass?file_id=42"), Server)!.CacheKey);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("burn_in")]
    [InlineData("native")]
    [InlineData(null)]
    public void OtherModesNeedNoOverlay(string? mode) => Assert.Null(PlaybackSubtitleResolver.Resolve(Plan(mode!), Server));

    [Fact]
    public void MissingPlansArtifactsAndEmbeddedDecisionsResolveToNothing()
    {
        Assert.Null(PlaybackSubtitleResolver.Resolve(null, Server));
        Assert.Null(PlaybackSubtitleResolver.Resolve(PlaybackMediaTestSupport.Plan(), Server));
        Assert.Null(PlaybackSubtitleResolver.Resolve(Plan(url: null), Server));
        var embedded = Plan();
        embedded.Subtitle!.Embedded = new EmbeddedSubtitleV3 { StreamIndex = 2 };
        Assert.Null(PlaybackSubtitleResolver.Resolve(embedded, Server));
    }

    private static PlaybackPlan EmbeddedPlan(string? mode, long? index)
    {
        var plan = PlaybackMediaTestSupport.Plan();
        plan.Subtitle = new SubtitleDecisionV3
        {
            Mode = mode,
            TrackId = "file:42:subtitle:1",
            Embedded = new EmbeddedSubtitleV3 { StreamIndex = index, ContainerTrackId = "3" },
        };
        return plan;
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(3L)]
    [InlineData(65535L)]
    public void RenderedEmbeddedDecisionsResolveToTheirStreamIndex(long index)
    {
        var plan = EmbeddedPlan("render", index);
        Assert.Equal(new PlaybackEmbeddedSubtitle((int)index), PlaybackSubtitleResolver.ResolveEmbedded(plan));
        // No sidecar: the text path stays out of it.
        Assert.Null(PlaybackSubtitleResolver.Resolve(plan, Server));
    }

    [Theory]
    [InlineData("off", 3L)]
    [InlineData("burn_in", 3L)]
    [InlineData("convert", 3L)]
    [InlineData(null, 3L)]
    [InlineData("render", -1L)]
    [InlineData("render", 65536L)]
    [InlineData("render", 4294967299L)]
    [InlineData("render", null)]
    public void OtherEmbeddedDecisionsResolveToNothing(string? mode, long? index) =>
        Assert.Null(PlaybackSubtitleResolver.ResolveEmbedded(EmbeddedPlan(mode, index)));

    [Fact]
    public void PlansWithoutAnEmbeddedStreamResolveToNoEmbeddedTrack()
    {
        Assert.Null(PlaybackSubtitleResolver.ResolveEmbedded(null));
        Assert.Null(PlaybackSubtitleResolver.ResolveEmbedded(PlaybackMediaTestSupport.Plan()));
        // A text sidecar decision carries no embedded stream.
        Assert.Null(PlaybackSubtitleResolver.ResolveEmbedded(Plan()));
    }

    [Theory]
    [InlineData("ass", null, 0u)]
    [InlineData("SSA", null, 0u)]
    [InlineData(null, "text/x-ssa", 0u)]
    [InlineData("srt", "application/x-subrip", 1u)]
    [InlineData("subrip", null, 1u)]
    [InlineData(null, "application/x-subrip", 1u)]
    [InlineData("vtt", "text/vtt", 2u)]
    [InlineData("webvtt", null, 2u)]
    [InlineData(null, "text/vtt; charset=utf-8", 2u)]
    [InlineData("mystery", "text/vtt", 2u)]
    [InlineData("sup", "application/octet-stream", 3u)]
    [InlineData("pgs", null, 3u)]
    [InlineData(null, "application/pgs", 3u)]
    public void FormatAndMimeTypePickTheOverlayFormat(string? format, string? mime, uint expected) =>
        Assert.Equal(expected, PlaybackSubtitleResolver.Resolve(Plan(format: format, mime: mime), Server)!.Format);

    [Theory]
    [InlineData("dvd_subtitle", "application/octet-stream")]
    [InlineData("sub", null)]
    [InlineData("idx", null)]
    [InlineData("dvb_subtitle", null)]
    [InlineData(null, null)]
    public void UnknownFormatsAreNotLoaded(string? format, string? mime) =>
        Assert.Null(PlaybackSubtitleResolver.Resolve(Plan(format: format, mime: mime), Server));

    [Theory]
    [InlineData("/api/v2/stream/session/subtitles/3.srt?file_id=42&original=1")]
    [InlineData("/api/v2/stream/session/subtitles/3.vtt?file_id=42&downloaded_subtitle_id=7")]
    [InlineData("/api/v2/stream/session/subtitles/0.vtt?file_id=407&embedded_stream_index=3")]
    [InlineData("/api/v2/stream/session/subtitles/0.srt?file_id=42&original=1&external_subtitle_key=0123456789abcdefaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("/api/v2/stream/session/subtitles/2/fonts?file_id=42&embedded_stream_index=5")]
    [InlineData("/api/v2/stream/session/subtitles/3")]
    [InlineData("/api/v2/stream/session/subtitles/0.sup?file_id=42&embedded_stream_index=3&windowed=1&position=120.5&duration=180")]
    public void ServerSubtitleUrlShapesAreAccepted(string url) =>
        Assert.Equal(new Uri("https://silo.example.com" + url), PlaybackMediaUrl.ResolveSubtitle(url, Server));

    [Theory]
    [InlineData("https://silo.example.com/api/v2/stream/session/subtitles/3.ass")]
    [InlineData("//elsewhere.example.com/api/v2/stream/session/subtitles/3.ass")]
    [InlineData("/api/v2/stream/session/file.mp4")]
    [InlineData("/api/v2/stream/session/subtitles/3/fonts/extra")]
    [InlineData("/api/v2/stream/session/subtitles/../../admin")]
    [InlineData("/api/v2/items/subtitles/3.ass")]
    [InlineData("/api/v2/stream/session/subtitles/3.ass?file_id=42&file_id=43")]
    [InlineData("/api/v2/stream/session/subtitles/3.ass?file_id=abc")]
    [InlineData("/api/v2/stream/session/subtitles/0.srt?file_id=42&external_subtitle_key=../x")]
    [InlineData("/api/v2/stream/session/subtitles/3.ass?original=0")]
    [InlineData("/api/v2/stream/session/subtitles/3.ass?redirect=1")]
    [InlineData("/api/v2/stream/session/subtitles/3.ass#x")]
    [InlineData("/api/v2/stream/session/subtitles/0.sup?windowed=2")]
    [InlineData("/api/v2/stream/session/subtitles/0.sup?position=-1")]
    [InlineData("/api/v2/stream/session/subtitles/0.sup?position=NaN")]
    [InlineData("/api/v2/stream/session/subtitles/0.sup?position=Infinity")]
    [InlineData("/api/v2/stream/session/subtitles/0.sup?duration=0")]
    [InlineData("/api/v2/stream/session/subtitles/0.sup?duration=3601")]
    [InlineData("/api/v2/stream/session/subtitles/0.sup?windowed=1&windowed=1")]
    public void OtherSubtitleUrlsAreRefused(string url)
    {
        Assert.Null(PlaybackMediaUrl.ResolveSubtitle(url, Server));
        Assert.Null(PlaybackSubtitleResolver.Resolve(Plan(url: url), Server));
    }

    [Fact]
    public void TheMediaResolverStillRefusesSubtitleQueries() =>
        Assert.Null(PlaybackMediaUrl.Resolve(AssUrl, Server));

    [Fact]
    public void FontsNeedAnAssTrackItsOwnInventoryRowAndAValidUrl()
    {
        Assert.Null(PlaybackSubtitleResolver.Resolve(Plan(url: "/api/v2/stream/session/subtitles/3.vtt", format: "vtt", mime: "text/vtt"), Server)!.FontBundle);
        Assert.Null(PlaybackSubtitleResolver.Resolve(Plan(fonts: "https://elsewhere.example.com/fonts"), Server)!.FontBundle);
        Assert.Null(PlaybackSubtitleResolver.Resolve(Plan(fonts: "/api/v2/stream/session/subtitles/3.ass"), Server)!.FontBundle);
        var other = Plan();
        other.Subtitle!.TrackId = "file:42:subtitle:9";
        Assert.Null(PlaybackSubtitleResolver.Resolve(other, Server)!.FontBundle);
    }

    [Fact]
    public void BothFontBundleShapesParse()
    {
        var first = Convert.ToBase64String([1, 2, 3]);
        var second = Convert.ToBase64String([4, 5]);
        var v2 = SubtitleFontBundle.Parse(Encoding.UTF8.GetBytes(
            $$"""{"count":2,"items":[{"name":"A.ttf","mime":"font/ttf","data":"{{first}}"},{"data":"{{second}}","name":"B.otf"}]}"""));
        var v1 = SubtitleFontBundle.Parse(Encoding.UTF8.GetBytes(
            $$"""[{"name":"A.ttf","data":"{{first}}"},{"name":"B.otf","data":"{{second}}"}]"""));
        foreach (var fonts in new[] { v2, v1 })
        {
            Assert.Equal(new[] { "A.ttf", "B.otf" }, fonts.Select(font => font.Name));
            Assert.Equal(new byte[] { 1, 2, 3 }, fonts[0].Data);
            Assert.Equal(new byte[] { 4, 5 }, fonts[1].Data);
        }
    }

    [Fact]
    public void UnusableFontEntriesAreSkipped()
    {
        var good = Convert.ToBase64String([9]);
        var fonts = SubtitleFontBundle.Parse(Encoding.UTF8.GetBytes(
            $$"""{"items":[{"name":"bad","data":"!!not base64!!"},{"name":"none"},{"name":"empty","data":""},7,{"name":"number","data":5},{"data":"{{good}}"}]}"""));
        var font = Assert.Single(fonts);
        Assert.Equal(string.Empty, font.Name);
        Assert.Equal(new byte[] { 9 }, font.Data);
        Assert.Empty(SubtitleFontBundle.Parse("{}"u8));
        Assert.Empty(SubtitleFontBundle.Parse("""{"items":null}"""u8));
        Assert.Empty(SubtitleFontBundle.Parse("[]"u8));
        Assert.Empty(SubtitleFontBundle.Parse("\"text\""u8));
    }

    [Fact]
    public void FontCapsAreEnforced()
    {
        var one = Convert.ToBase64String([1]);
        var many = "[" + string.Join(",", Enumerable.Repeat($$"""{"name":"f","data":"{{one}}"}""", SubtitleFontBundle.MaxFonts + 5)) + "]";
        Assert.Equal(SubtitleFontBundle.MaxFonts, SubtitleFontBundle.Parse(Encoding.UTF8.GetBytes(many)).Count);

        var big = Convert.ToBase64String(new byte[12 * 1024 * 1024]);
        var heavy = "[" + string.Join(",", Enumerable.Repeat($$"""{"name":"f","data":"{{big}}"}""", 3)) + "]";
        Assert.Equal(2, SubtitleFontBundle.Parse(Encoding.UTF8.GetBytes(heavy)).Count);
    }

    [Fact]
    public void MalformedFontJsonFailsWithoutQuotingThePayload()
    {
        var error = Assert.Throws<InvalidDataException>(() => SubtitleFontBundle.Parse("""{"items":[{"name":"secret-name","data":"""u8));
        Assert.DoesNotContain("secret-name", error.Message);
    }

    [Fact]
    public void ByteOrderMarksAreRemovedForTheOverlay()
    {
        var text = "1\n00:00:01,000 --> 00:00:02,000\nHé\n";
        var utf8 = Encoding.UTF8.GetBytes(text);
        Assert.Same(utf8, PlaybackSubtitleText.ToUtf8(utf8));
        Assert.Equal(utf8, PlaybackSubtitleText.ToUtf8([0xEF, 0xBB, 0xBF, .. utf8]));
        Assert.Equal(utf8, PlaybackSubtitleText.ToUtf8([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)]));
        Assert.Equal(utf8, PlaybackSubtitleText.ToUtf8([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)]));
    }

    [Fact]
    public async Task SubtitleAndFontsAreFetchedAsThePlansViewerWithoutStreamHeaders()
    {
        var fontJson = $$"""{"items":[{"name":"A.ttf","data":"{{Convert.ToBase64String([1, 2, 3])}}"}]}""";
        var stub = new StubHandler().Enqueue("[Script Info]\n").Enqueue(fontJson);
        using var client = Create(stub);
        var plan = Plan();
        plan.Stream!.Headers = new StreamV3_headers { AdditionalData = { ["X-Stream-Grant"] = "grant" } };
        var media = client.Playback.OpenMedia(plan);
        var source = client.Playback.ResolveSubtitles(plan)!;

        var text = await media.GetSubtitleAsync(source, CancellationToken.None);
        var fonts = await media.GetSubtitleFontsAsync(source, CancellationToken.None);

        Assert.Equal("[Script Info]\n", Encoding.UTF8.GetString(text));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(fonts).Data);
        Assert.Equal(new[] { source.Uri, source.FontBundle }, stub.Requests.Select(request => request.RequestUri));
        Assert.All(stub.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer test-access", request.Headers.Authorization!.ToString());
            Assert.Equal("profile", Assert.Single(request.Headers.GetValues("X-Profile-Id")));
            Assert.False(request.Headers.Contains("X-Stream-Grant"));
        });
    }

    [Fact]
    public async Task PgsWindowsAreBinaryAuthenticatedCappedAndReplaceExistingWindowQueries()
    {
        // UTF-16-looking bytes must remain binary. This used to be a text-only fetch path.
        var bytes = new byte[] { 0xff, 0xfe, 0, 1, 0, 2 };
        var stub = new PgsHandler(bytes);
        using var client = Create(stub);
        var plan = Plan(format: "sup", mime: "application/octet-stream",
            url: "/api/v2/stream/session/subtitles/3.sup?file_id=42&windowed=1&position=0&duration=180");
        plan.Stream!.Headers = new StreamV3_headers { AdditionalData = { ["X-Stream-Grant"] = "grant" } };
        var media = client.Playback.OpenMedia(plan);
        var source = client.Playback.ResolveSubtitles(plan)!;
        Assert.True(source.IsBitmap);
        Assert.Equal("sup", source.FormatName);
        Assert.Null(source.FontBundle);
        var range = PgsSubtitleRange.At(1500);
        Assert.Equal(bytes, await media.GetPgsSubtitlesAsync(source, range, CancellationToken.None));
        var request = Assert.Single(stub.Requests);
        Assert.Equal("?file_id=42&windowed=1&position=1470&duration=180", request.RequestUri!.Query);
        Assert.Equal("Bearer test-access", request.Headers.Authorization!.ToString());
        Assert.Equal("profile", Assert.Single(request.Headers.GetValues("X-Profile-Id")));
        Assert.False(request.Headers.Contains("X-Stream-Grant"));
        Assert.Equal(request.RequestUri, PlaybackMediaUrl.ResolveSubtitle(request.RequestUri.PathAndQuery, Server));
        await Assert.ThrowsAsync<ArgumentException>(() => media.GetSubtitleAsync(source, CancellationToken.None));
    }

    [Fact]
    public async Task AFailedSubtitleFetchThrowsAndNoBundleMeansNoRequest()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.NotFound, "{}");
        using var client = Create(stub);
        var plan = Plan(fonts: null);
        var media = client.Playback.OpenMedia(plan);
        var source = client.Playback.ResolveSubtitles(plan)!;

        await Assert.ThrowsAsync<HttpRequestException>(() => media.GetSubtitleAsync(source, CancellationToken.None));
        Assert.Empty(await media.GetSubtitleFontsAsync(source, CancellationToken.None));
        Assert.Single(stub.Requests);
    }
    [Fact]
    public void AutoKeepsRenderableEmbeddedPgsAndSelectsForcedPgsInTheAudioLanguage()
    {
        VersionSubtitleTrack[] tracks =
        [
            new() { Codec = "srt", Language = "en", External = true },
            new() { Codec = "hdmv_pgs_subtitle", Language = "eng", External = false },
            new() { Codec = "hdmv_pgs_subtitle", Language = "eng", Forced = true },
        ];
        Assert.Equal(1, PlaybackFormatting.AutoResolvedSubtitleOrdinal(tracks, new("en", "always")));
        Assert.Equal(2, PlaybackFormatting.AutoResolvedSubtitleOrdinal(tracks, new("en", "auto", true, "en")));
        Assert.Null(PlaybackFormatting.AutoResolvedSubtitleOrdinal(tracks, new("en", "auto", false, "en")));
        Assert.Equal(2, PlaybackFormatting.AutoResolvedSubtitleOrdinal(tracks, new("en", "off", true, "en")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PgsFetchCapsKnownLengthAndChunkedResponses(bool contentLength)
    {
        using var body = new CountingSubtitleStream(PgsSubtitleRange.MaxBytes + 1);
        var stub = new PgsHandler([]) { Content = contentLength
            ? new ByteArrayContent(new byte[PgsSubtitleRange.MaxBytes + 1]) : new StreamContent(body) };
        using var client = Create(stub);
        var plan = Plan(format: "sup", mime: "application/octet-stream");
        var media = client.Playback.OpenMedia(plan);
        await Assert.ThrowsAsync<InvalidDataException>(() => media.GetPgsSubtitlesAsync(
            client.Playback.ResolveSubtitles(plan)!, PgsSubtitleRange.At(1500), CancellationToken.None));
        Assert.True(body.BytesRead <= PgsSubtitleRange.MaxBytes + 81920);
    }

    private sealed class CountingSubtitleStream(int length) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, length - BytesRead);
            buffer.AsSpan(offset,read).Clear();
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..read].Clear(); BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count) => throw new NotSupportedException();
    }

    private sealed class PgsHandler(byte[] bytes) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public HttpContent? Content { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Content ?? new ByteArrayContent(bytes) });
        }
    }

}
