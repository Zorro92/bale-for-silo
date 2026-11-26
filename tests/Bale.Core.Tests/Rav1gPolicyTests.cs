using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public sealed class Rav1gPolicyTests
{
    private static PlaybackPlan Plan(long? depth = 10) => new()
    {
        Source = new() { MediaFileId = "file", VideoCodec = "av1", BitDepth = depth },
    };

    private static FileVersion Version(string primaries = "bt709", string transfer = "bt709", string space = "bt709",
        string rate = "24000/1001") => new()
    {
        FileId = "file", CodecVideo = "av1", Container = "mkv",
        VideoTracks = [new() { Codec = "av1", ColorPrimaries = primaries, ColorTransfer = transfer, ColorSpace = space,
            FrameRate = rate, Width = 3840, Height = 2160 }],
    };

    private static PlaybackRouteDecision Route(bool software = true, string codec = "av1", long width = 3840, long height = 2160) =>
        new(UseFfmpeg: true, SoftwareVideo: software, VideoCodec: codec, AudioCodec: "opus", AudioOrdinal: null,
            VideoWidth: width, VideoHeight: height);

    private static Rav1gRouteDecision Decide(string selected = "rav1g", PlaybackRouteDecision? route = null,
        (int, int)? display = null, bool sdr = true, bool withdrawn = false, FileVersion? version = null, bool usable = true) =>
        Rav1gPolicy.Route(selected, route ?? Route(), Plan(), [version ?? Version()], display ?? (1920, 1080), sdr, withdrawn, usable);

    [Fact]
    public void OnlyTheSwitchOnASoftwareAv1RouteTakesRav1g()
    {
        Assert.True(Decide().Rav1g);
        Assert.False(Decide("dav1d").Rav1g);
        // No usable rav1g.dll: the mount is dav1d's, with dav1d's own tuning.
        Assert.Equal(Decide("dav1d"), Decide(usable: false));
        Assert.False(Decide(withdrawn: true).Rav1g);
        Assert.False(Decide(route: Route(software: false)).Rav1g);
        Assert.False(Decide(route: Route(codec: "mpeg4")).Rav1g);
        var dav1d = Decide("dav1d");
        Assert.Null(dav1d.BufferMs);
        Assert.Null(dav1d.DecodeAheadFrames);
        Assert.Null(dav1d.OutputWidth);
        Assert.False(dav1d.ToneMap);
    }

    [Fact]
    public void Rav1gRouteBuffersLikeDav1dAndDownscalesThroughTheStage()
    {
        var decision = Decide();
        // 250 ms at every resolution (owner, 2026-11-18); the texture cap follows the frame rate (23.976 fps here).
        Assert.Equal(250, decision.BufferMs);
        Assert.Equal(250, Decide(route: Route(width: 1920, height: 1080)).BufferMs);
        Assert.Equal(12, decision.OutputTextures);
        Assert.Equal(8, decision.DecodeAheadFrames);
        Assert.Equal(8, Decide(route: Route(width: 1920, height: 1080)).DecodeAheadFrames);
        Assert.Equal((1920, 1080), (decision.OutputWidth, decision.OutputHeight));
        Assert.True(decision.ToneMap);
        Assert.Equal(new Av1SourceFacts(10, 2, 5, 1, 24000.0 / 1001), decision.Source);
        // 60 fps: the same 250 ms, a larger cap so the buffer never waits for a texture (15 + 4 + 2).
        Assert.Equal(250, Decide(version: Version(rate: "60")).BufferMs);
        Assert.Equal(21, Decide(version: Version(rate: "60")).OutputTextures);
        var fits = Decide(route: Route(width: 1920, height: 1080), sdr: false);
        Assert.Null(fits.OutputWidth);
        Assert.Null(fits.OutputHeight);
        Assert.False(fits.ToneMap);
        // SDR larger than the display is rav1g's downscale stage; PQ is the tone map's or passes through.
        Assert.True(decision.DownscaleStage);
        Assert.False(fits.DownscaleStage);
        Assert.False(Decide(version: Version("bt2020", "smpte2084", "bt2020nc")).DownscaleStage);
    }

    private static Rav1gOutputFacts Direct(string subtype, uint? primaries = 2, uint? transfer = 5, uint? matrix = 1) =>
        new(subtype, Rav1gMark: true, ToneMapMark: false, primaries, transfer, matrix, 2);

    private static Av1SourceFacts Sdr(long? depth) => new(depth, 2, 5, 1, null);

    [Theory]
    [InlineData(8L, "NV12", "direct")]
    [InlineData(10L, "P010", "direct")]
    [InlineData(10L, "NV12", "reject")]
    [InlineData(8L, "P010", "reject")]
    [InlineData(12L, "P010", "direct")]
    [InlineData(null, "NV12", "direct")]
    [InlineData(null, "RGB32", "reject")]
    public void DirectOutputMustKeepTheSourceDepth(long? depth, string subtype, string expected) =>
        Assert.Equal(expected, Rav1gPolicy.OutputVerdict(Sdr(depth), Direct(subtype), sdrDisplay: true, allowNv12Stage: false).Kind);

    [Fact]
    public void DirectOutputMustCarryTheSourceColour()
    {
        Assert.Equal("colour does not match the source",
            Rav1gPolicy.OutputVerdict(Sdr(8), Direct("NV12", matrix: 2), true, false).Reason);
        Assert.False(Rav1gPolicy.OutputVerdict(Sdr(8), Direct("NV12", primaries: null), true, false).Accepted);
        var hdr = new Av1SourceFacts(10, 9, 15, 4, null);
        // The HDR policy may leave BT.2020 primaries and PQ out on an HDR display; never on SDR (the stage declined).
        Assert.Equal("direct", Rav1gPolicy.OutputVerdict(hdr, Direct("P010", null, null, 4), false, false).Kind);
        Assert.Equal("HDR on an SDR display", Rav1gPolicy.OutputVerdict(hdr, Direct("P010", 9, 15, 4), true, false).Reason);
    }

    [Fact]
    public void StageOutputIsMarkedLimitedBt709P010()
    {
        var stage = new Rav1gOutputFacts("P010", false, true, 2, 5, 1, 2);
        Assert.Equal("stage", Rav1gPolicy.OutputVerdict(Sdr(10), stage, true, false).Kind);
        Assert.Equal("reject", Rav1gPolicy.OutputVerdict(Sdr(10), stage with { Subtype = "NV12" }, true, false).Kind);
        Assert.Equal("stage", Rav1gPolicy.OutputVerdict(Sdr(10), stage with { Subtype = "NV12" }, true, true).Kind);
        // Dithered 8-bit output Bale asked for on an 8-bit SDR display (owner, 2026-11-26: AV1 at display depth too);
        // P010 stays accepted (HDR passthrough, a 30+ bpp display, a wrapper ignoring the key).
        Assert.Equal("stage", Rav1gPolicy.OutputVerdict(Sdr(10), stage with { Subtype = "NV12" }, true, false, nv12Requested: true).Kind);
        Assert.Equal("stage", Rav1gPolicy.OutputVerdict(Sdr(10), stage, true, false, nv12Requested: true).Kind);
        // A request never excuses unmarked or direct NV12 from a 10-bit source.
        Assert.Equal("reject", Rav1gPolicy.OutputVerdict(Sdr(10), Direct("NV12"), true, false, nv12Requested: true).Kind);
        Assert.Equal("reject", Rav1gPolicy.OutputVerdict(Sdr(10), stage with { Range = 1 }, true, false).Kind);
        Assert.Equal("both marks", Rav1gPolicy.OutputVerdict(Sdr(10), stage with { Rav1gMark = true }, true, false).Reason);
    }

    [Fact]
    public void UnmarkedOutputIsTheOpensDav1dFallbackAndAccepted()
    {
        var verdict = Rav1gPolicy.OutputVerdict(Sdr(10), new("P010", false, false, null, null, null, null), true, false);
        Assert.Equal(new Rav1gVerdict("dav1d", null), verdict);
        Assert.True(verdict.Accepted);
    }

    [Theory]
    [InlineData(true, true, false, false, false, false, true)]
    [InlineData(false, true, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(true, true, true, false, false, false, false)]
    [InlineData(true, true, false, true, false, false, false)]
    [InlineData(true, true, false, false, true, false, false)]
    [InlineData(true, true, false, false, false, true, false)]
    public void FailedRav1gMountRemountsOnceOnDav1d(bool route, bool active, bool remounted, bool hdr, bool subtitle,
        bool budget, bool expected) =>
        Assert.Equal(expected, Rav1gPolicy.RemountOnDav1d(route, active, remounted, hdr, subtitle, budget, 1080));

    [Theory]
    [InlineData(720L, true)]
    [InlineData(1080L, true)]
    [InlineData(1081L, false)]
    [InlineData(2160L, false)]
    [InlineData(null, false)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    public void Dav1dRemountRequiresAKnownSourceAtMost1080(long? height, bool expected) =>
        Assert.Equal(expected, Rav1gPolicy.RemountOnDav1d(true, true, false, false, false, false, height));

    [Theory]
    [InlineData("Bale rav1g: loaded rav1g 0.4.0 (interface 1)\n", "loaded")]
    [InlineData("Bale rav1g: active 3840x2160 bits=10 slots=3 parse=1 threads=2 device=501.5 MiB", "active")]
    [InlineData("Bale rav1g: fallback to dav1d (library missing, status=0x8007007E)", "fallback")]
    [InlineData("Bale rav1g: stream failed (decode, hr=0x80042B23)", "failed")]
    [InlineData("Bale rav1g: output 1920x1080 P010 processed hdr=0", "output")]
    [InlineData("Bale rav1g: destroyed (frames=10 discarded=0 images created=8 peak in use=8 blocked=3 output created=5)", "destroyed")]
    [InlineData("Bale rav1g: the renderer device is on another adapter", "other")]
    public void ParsesTheForksLogLines(string line, string kind)
    {
        var parsed = Rav1gPolicy.ParseLog(line);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(line[Rav1gPolicy.LogPrefix.Length..].TrimEnd('\n'), parsed.Detail);
    }

    [Fact]
    public void OtherLinesAreNotRav1g()
    {
        Assert.Null(Rav1gPolicy.ParseLog("Bale tone map: request ignored"));
        Assert.Null(Rav1gPolicy.ParseLog(""));
    }

    [Fact]
    public void DescribesWhichDecoderTheOpenUsed()
    {
        Assert.Equal("dav1d · switch dav1d", Rav1gPolicy.Describe("dav1d", false, null, false, null, null, null, null, false));
        Assert.Equal("rav1g direct · output 1920x1080 NV12 direct hdr=0",
            Rav1gPolicy.Describe("rav1g", true, null, true, "direct", null, null, "output 1920x1080 NV12 direct hdr=0", false));
        Assert.Equal("dav1d (rav1g fallback to dav1d (library missing, status=0x8007007E)) · rav1g.dll not packaged",
            Rav1gPolicy.Describe("rav1g", true, "rav1g.dll not packaged", false, "dav1d", "fallback to dav1d (library missing, status=0x8007007E)",
                null, null, false));
        Assert.Equal("rav1g failed: stream failed (decode, hr=0x80042B23)",
            Rav1gPolicy.Describe("rav1g", true, null, true, null, null, "stream failed (decode, hr=0x80042B23)", null, false));
        Assert.Equal("dav1d (remounted after a rav1g failure) · switch rav1g",
            Rav1gPolicy.Describe("rav1g", false, null, false, null, null, null, null, true));
        // Without a usable library the route never asked for rav1g (release and debug alike).
        Assert.Equal("dav1d (rav1g unavailable) · switch rav1g · rav1g.dll not packaged",
            Rav1gPolicy.Describe("rav1g", false, "rav1g.dll not packaged", false, null, null, null, null, false));
    }

    [Fact]
    public void TheLibraryIsUsableOnlyAsTheForkLoadsIt()
    {
        Assert.Equal(new Rav1gLibraryStatus(true, "rav1g.dll usable (interface 3)"), Rav1gPolicy.Library(true, true, null, 3));
        Assert.Equal(new Rav1gLibraryStatus(false, "rav1g.dll not packaged"), Rav1gPolicy.Library(false, false, null, null));
        Assert.Equal(new Rav1gLibraryStatus(false, "rav1g.dll failed to load"), Rav1gPolicy.Library(true, false, null, null));
        Assert.Equal(new Rav1gLibraryStatus(false, "rav1g.dll lacks rav1g_decoder_drain"),
            Rav1gPolicy.Library(true, true, "rav1g_decoder_drain", 3));
        Assert.Equal(new Rav1gLibraryStatus(false, "rav1g.dll interface 2, expected 3"), Rav1gPolicy.Library(true, true, null, 2));
        Assert.False(Rav1gPolicy.Library(true, true, null, null).Usable);
        Assert.Equal(3u, Rav1gPolicy.LibraryAbiVersion);
        Assert.Equal(12, Rav1gPolicy.LibraryExports.Count);
    }

    [Theory]
    [InlineData("2.1.0-bale.48", true)]
    [InlineData("2.1.0-bale.52", true)]
    [InlineData("2.1.0-bale.47", false)]
    [InlineData("2.1.0", false)]
    [InlineData("", false)]
    public void PrewarmNeedsTheBale48Export(string version, bool supported) =>
        Assert.Equal(supported, Rav1gPolicy.PrewarmSupported(version));

    [Theory]
    [InlineData(0, "started")]
    [InlineData(1, "already started")]
    [InlineData(-1, "failed (could not start its thread)")]
    [InlineData(2, "unexpected return 2")]
    public void PrewarmReturnReadsAsTheForkDocumentsIt(int code, string expected) =>
        Assert.Equal(expected, Rav1gPolicy.PrewarmResult(code));

    private static FileVersion DetailVersion(string codec, long height, long? kbps, string? profile) => new()
    {
        FileId = "file", CodecVideo = codec, Container = "mkv",
        VideoTracks = [new() { Codec = codec, Profile = profile, BitDepth = 10, Width = height * 16 / 9, Height = height,
            FrameRate = "24000/1001", Bitrate = kbps }],
    };

    [Theory]
    // 4K inside rav1g's 2160p60 40 Mbps entry, an unknown bitrate (passes, as at the server), and 1080p at any bitrate:
    // dav1d's entry admits it, and the route still decodes it on rav1g.
    [InlineData("av1", 2160, 30_000L, "Main", true)]
    [InlineData("av1", 2160, null, "Main", true)]
    [InlineData("av1", 1080, 80_000L, "Main", true)]
    // The server transcodes these, so they never reach rav1g: above the soft ceiling, 8K, High profile, other codecs.
    [InlineData("av1", 2160, 45_000L, "Main", false)]
    [InlineData("av1", 4320, 20_000L, "Main", false)]
    [InlineData("av1", 1080, 8_000L, "High", false)]
    [InlineData("hevc", 2160, 30_000L, "Main 10", false)]
    public void TheDetailPrewarmsOnlyAVersionThatWillPlayOnRav1g(string codec, long height, long? kbps, string profile, bool prewarm)
    {
        var version = DetailVersion(codec, height, kbps, profile);
        Assert.Equal(prewarm, Rav1gPolicy.PrewarmCandidate("rav1g", rav1gClaim: true, libraryUsable: true, softwareVideo: true, version));
        // Never with an unusable rav1g.dll, on a console without software video (no AV1 claim), with the dav1d switch, or
        // before the page has a version.
        Assert.False(Rav1gPolicy.PrewarmCandidate("rav1g", true, libraryUsable: false, softwareVideo: true, version));
        Assert.False(Rav1gPolicy.PrewarmCandidate("rav1g", true, true, softwareVideo: false, version));
        Assert.False(Rav1gPolicy.PrewarmCandidate("dav1d", true, true, true, version));
        Assert.False(Rav1gPolicy.PrewarmCandidate("rav1g", true, true, true, null));
    }

    [Fact]
    public void WithoutRav1gsClaimOnly1080pAv1StillPlaysOnRav1g()
    {
        // Debug av1-claim 1080: 4K AV1 is transcoded; 1080p is admitted by dav1d's entry and decodes on rav1g.
        Assert.False(Rav1gPolicy.PrewarmCandidate("rav1g", rav1gClaim: false, true, true, DetailVersion("av1", 2160, 30_000, "Main")));
        Assert.True(Rav1gPolicy.PrewarmCandidate("rav1g", rav1gClaim: false, true, true, DetailVersion("av1", 1080, 8_000, "Main")));
    }

    [Fact]
    public void OutputMarkIsTheForksGuid() =>
        Assert.Equal(new Guid(0x6d553633, 0x9287, 0x4856, 0xbc, 0x9f, 0x0c, 0x23, 0xaf, 0x00, 0xe1, 0x84), Rav1gPolicy.OutputMark);
}
