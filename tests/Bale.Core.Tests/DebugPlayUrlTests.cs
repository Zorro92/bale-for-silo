using System.Runtime.InteropServices;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using static Bale.Core.Tests.RustRangeStreamTests;

namespace Bale.Core.Tests;

public class DebugPlayUrlTests
{
    [Theory]
    [InlineData("https://example.invalid/clip.mp4?key=private", null)]
    [InlineData("http://example.invalid/a%20clip.mkv", "12.5")]
    [InlineData("https://example.invalid/clip.webm", "0")]
    public void ParsesHttpFilesAndSourceSecondsWithoutRewritingTheUrl(string url, string? seconds)
    {
        var query = new Dictionary<string, string> { ["url"] = url };
        if (seconds is not null) query["start"] = seconds;
        Assert.True(DebugPlayUrlRequest.TryParse(query, out var request));
        Assert.Equal(url, request.Url.AbsoluteUri);
        Assert.Equal(seconds is null ? null : double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture), request.StartSeconds);
        Assert.DoesNotContain(url, request.ToString());
        Assert.Contains("[redacted-url]", request.ToString());
    }

    [Theory]
    [InlineData("file:///clip.mkv")]
    [InlineData("ftp://example.invalid/clip.mp4")]
    [InlineData("/clip.mp4")]
    [InlineData("//example.invalid/clip.mp4")]
    [InlineData("http://user:pass@example.invalid/clip.mp4")]
    [InlineData("http://example.invalid/clip.mp4#private")]
    [InlineData("https://example.invalid/clip.m3u8")]
    [InlineData("https://example.invalid/clip.mp4\n")]
    [InlineData("https://example.invalid/a clip.mp4")]
    [InlineData("https:\\example.invalid\\clip.mp4")]
    [InlineData("")]
    public void RejectsNonFileSchemesCredentialsFragmentsAndMalformedUrls(string url) =>
        Assert.False(DebugPlayUrlRequest.TryParse(new Dictionary<string, string> { ["url"] = url }, out _));

    [Theory]
    [InlineData("start", "-1")]
    [InlineData("start", "NaN")]
    [InlineData("start", "Infinity")]
    [InlineData("start", "1e3")]
    [InlineData("start", "1,5")]
    [InlineData("start", "999999999999999999999999")]
    [InlineData("id", "content")]
    [InlineData("file", "file")]
    public void RejectsInvalidSecondsAndAnyServerSelection(string key, string value) =>
        Assert.False(DebugPlayUrlRequest.TryParse(new Dictionary<string, string> { ["url"] = "https://example.invalid/clip.mp4", [key] = value }, out _));

    [Fact]
    public void RequiresUrlAndBoundsItsLength()
    {
        Assert.False(DebugPlayUrlRequest.TryParse(new Dictionary<string, string>(), out _));
        Assert.False(DebugPlayUrlRequest.TryParse(new Dictionary<string, string> { ["url"] = "https://example.invalid/" + new string('a', 8192) }, out _));
    }

    [Fact]
    public void Probed4K60Av1UsesTheRealRav1gConfigurationAndItsTextureBudget()
    {
        var request = new DebugPlayUrlRequest(new("https://example.invalid/clip"), 23.5);
        var probe = Facts("av1", 3840, 2160, 60, "bt709");
        var plan = request.Plan(probe);
        var detail = request.Detail(probe);
        var route = PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true, versions: detail.Versions);
        var rav1g = Rav1gPolicy.Route("rav1g", route, plan, detail.Versions, (1920, 1080), true, false);
        var config = FfmpegSource.Configuration(route, new DebugFfmpegInputTuning(), rav1g);

        Assert.Equal("original_http", plan.Delivery);
        Assert.Null(plan.Stream!.Headers);
        Assert.Null(plan.PlanId);
        Assert.Equal(3840, route.VideoWidth);
        Assert.Equal(2160, route.VideoHeight);
        Assert.True(route.SoftwareVideo);
        Assert.True(rav1g.Rav1g);
        Assert.Equal(60, rav1g.Source.FrameRate);
        Assert.Equal(21, rav1g.OutputTextures);
        Assert.Equal(250, rav1g.BufferMs);
        Assert.Equal("1", config.FFmpegOptions["bale_av1_decoder"]);
        Assert.Equal("1080", config.FFmpegOptions["bale_av1_fallback_max_height"]);
        Assert.Equal("21", config.FFmpegOptions["bale_gpu_output_textures"]);
        Assert.Equal(23.5, PlaybackTimeline.From(plan.Timeline).PlayerForSource(23.5));
        Assert.Equal("Debug file", detail.Title);
        Assert.DoesNotContain("example.invalid", probe.Describe());
    }

    [Theory]
    [InlineData("hevc", false)]
    [InlineData("av1", true)]
    public void ProbedHdrConfiguresTheSameToneMapAndOutputGuardRoute(string codec, bool gpuAv1)
    {
        var request = new DebugPlayUrlRequest(new("https://example.invalid/clip"), null);
        var probe = Facts(codec, 3840, 2160, 60, "smpte2084");
        var plan = request.Plan(probe);
        var versions = request.Detail(probe).Versions;
        var route = PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true, versions: versions);
        var rav1g = gpuAv1 ? Rav1gPolicy.Route("rav1g", route, plan, versions, (1920, 1080), true, false) : null;
        var config = FfmpegSource.Configuration(route, new DebugFfmpegInputTuning(), rav1g);

        Assert.True(route.ConsoleToneMap);
        Assert.True(DisplayHdrPolicy.RequiresHdr10(plan));
        Assert.Equal("hdr10", plan.Source!.DynamicRange);
        Assert.Equal("1", config.FFmpegOptions["bale_tonemap"]);
        Assert.Equal(gpuAv1 ? FFmpegInteropX.VideoDecoderMode.ForceFFmpegSoftwareDecoder : FFmpegInteropX.VideoDecoderMode.Automatic,
            config.Video.VideoDecoderMode);
        if (gpuAv1) Assert.Equal((uint)15, rav1g!.Source.Transfer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothNormalRangeReadersSendNoSiloHeadersAndCanSeekAnExternalFile(bool native)
    {
        var payload = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        using var server = new LoopbackMediaServer(payload);
        var uri = new Uri(server.Uri, "/clip.mp4");
        var request = new DebugPlayUrlRequest(uri, null);
        var media = PlaybackMedia.OpenDebugFile(uri, request.Plan());
        Assert.Empty(await media.NativeHeadersAsync(default));
        Assert.Empty(await media.NativeHeadersAsync(default, "rejected"));
        await using Stream stream = native ? await media.OpenNativeStreamAsync(default) : await media.OpenStreamAsync(default);
        stream.Position = 55;
        var bytes = new byte[12];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(payload[55..67], bytes);
        Assert.Equal(0, server.Refreshes);
        Assert.NotEmpty(server.Headers);
        Assert.DoesNotContain(server.Headers, header => header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || header.Key.StartsWith("X-Silo-", StringComparison.OrdinalIgnoreCase)
            || header.Key.StartsWith("X-Profile-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TransportRejectsStreamHeadersEvenForAValidatedUrl()
    {
        var request = new DebugPlayUrlRequest(new("https://example.invalid/clip.mp4"), null);
        var plan = request.Plan();
        plan.Stream!.Headers = new();
        Assert.Throws<ArgumentException>(() => PlaybackMedia.OpenDebugFile(request.Url, plan));
    }

    [Fact]
    public void ADebugFileKeepsTheWrappersDefaultAudioStreamWhileSiloPlaysKeepTheStrictRule()
    {
        // play-url's synthetic detail has no audio catalog and its plan names no audio track: Silo's rule, which maps the
        // plan's catalog ordinal onto the mounted streams, refuses any file with audio ("player: opened
        // type=PlaybackAudioSelectionException"). The rule itself stays as it is for every real Silo play.
        var request = new DebugPlayUrlRequest(new("https://example.invalid/clip.mkv"), null);
        var probe = Facts("h264", 1920, 1080, 30, "bt709");
        var plan = request.Plan(probe);
        var catalog = request.Detail(probe).Versions.Single(version => version.FileId == plan.Source!.MediaFileId).AudioTracks;
        Assert.Equal("original_http", plan.Delivery);
        Assert.Null(plan.SelectedTracks?.Audio?.Index);
        Assert.True(catalog is null or { Count: 0 });
        MountedPlaybackAudioTrack[] mounted = [new(1, "en", "aac", 2, null, true)];
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(null, catalog, mounted, ffmpeg: true));
        Assert.Throws<PlaybackAudioSelectionException>(() => PlaybackOriginalAudioSelection.ResolveIndex(null, catalog, mounted, ffmpeg: false));
        // So a debug-file attempt, in DEBUG_SERVER builds only, keeps the wrapper's default stream before the mapping runs.
        var media = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlayerPage.Media.cs.txt")).ReplaceLineEndings("\n");
        var start = media.IndexOf("private void SelectOriginalAudioTrack(", StringComparison.Ordinal);
        var end = media.IndexOf("private void OnAudioTrackOpenFailed(", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var select = media[start..end];
        var at = -1;
        foreach (var part in new[] { "#if DEBUG_SERVER", "if (attempt.DebugUrl is not null && planIndex is null)",
            "Log.Info(\"player: debug file keeps the default audio stream\");", "return;", "#endif",
            "PlaybackOriginalAudioSelection.Select(planIndex, catalog, mounted, ffmpeg is not null," })
        {
            var next = select.IndexOf(part, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, "out of order or missing: " + part);
            at = next;
        }
        Assert.Equal(1, select.Split("#if DEBUG_SERVER").Length - 1);
    }

    internal static DebugFileProbe Facts(string codec, int width, int height, double fps, string transfer) => new("matroska", new VideoTrack
    {
        Codec = codec, Width = width, Height = height, BitDepth = 10, Profile = codec == "hevc" ? "Main 10" : "Main",
        PixelFormat = "yuv420p10le", FrameRate = fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ColorTransfer = transfer, ColorPrimaries = transfer == "smpte2084" ? "bt2020" : "bt709",
        ColorSpace = transfer == "smpte2084" ? "bt2020nc" : "bt709",
    }, "aac", fps, 90);
}

public class DebugFileProbeIntegrationTests
{
    // One synthetic black AV1 picture, IVF at 3840x2160/60. No file name, tags or media content.
    // Generated with color=black:s=3840x2160:r=60, libaom-av1, cpu-used=8, threads=4, crf=63, frames:v=1.
    private const string BlackFrame = "REtJRgAAIABBVjAxAA9wCDwAAAABAAAAAQAAAAAAAAA3AAAAAAAAAAAAAAASAAoMAAAAau+/4b3K+QBAMiUQAJ/gAACwAAADrRnO4wlW/3/WI/hTOWNO34U5+qGt1L/AmUog";

    [Fact]
    public void WhenFfmpeg9IsInstalledTheActualDemuxProbeReads4K60Av1WithoutAUrlOrPlayer()
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libavformat.so.63", out _)) return;
        NativeLibrary.SetDllImportResolver(typeof(DebugFileProbeReader).Assembly, (name, _, _) => name switch
        {
            "avformat-63.dll" => NativeLibrary.Load("libavformat.so.63"),
            "avcodec-63.dll" => NativeLibrary.Load("libavcodec.so.63"),
            "avutil-61.dll" => NativeLibrary.Load("libavutil.so.61"),
            _ => 0,
        });
        using var stream = new MemoryStream(Convert.FromBase64String(BlackFrame));
        var probe = DebugFileProbeReader.Read(stream, default);
        Assert.Equal("av1", probe.Video.Codec);
        Assert.Equal(3840, probe.Video.Width);
        Assert.Equal(2160, probe.Video.Height);
        Assert.Equal(8, probe.Video.BitDepth);
        Assert.Equal(60, probe.FrameRate);
        Assert.Equal(0, stream.Position);
        Assert.Null(probe.AudioCodec);
        // Explicit AV1 sequence-header color parameters (aom-params 9/16/9), not just container tags.
        using var hdrStream = new MemoryStream(Convert.FromBase64String(
            "REtJRgAAIABBVjAxIAAgADwAAAABAAAAAQAAAAAAAAAfAAAAAAAAAAAAAAASAAoNAAAAAif+bXzoSIBIIDIMEAD/gAACwAAAAuXr"));
        var hdr = DebugFileProbeReader.Read(hdrStream, default);
        Assert.Equal(10, hdr.Video.BitDepth);
        Assert.Equal("smpte2084", hdr.Video.ColorTransfer);
        Assert.Equal("bt2020", hdr.Video.ColorPrimaries);
        Assert.Equal("bt2020nc", hdr.Video.ColorSpace);
        Assert.Equal("hdr10", hdr.DynamicRange);
        using var bad = new MemoryStream("not a media file"u8.ToArray());
        Assert.Throws<DebugPlayUrlException>(() => DebugFileProbeReader.Read(bad, default));
        Assert.Equal(0, bad.Position);
    }
}
