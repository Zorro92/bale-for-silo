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
        (int, int)? display = null, bool sdr = true, bool withdrawn = false, FileVersion? version = null) =>
        Rav1gPolicy.Route(selected, route ?? Route(), Plan(), [version ?? Version()], display ?? (1920, 1080), sdr, withdrawn);

    [Fact]
    public void OnlyTheSwitchOnASoftwareAv1RouteTakesRav1g()
    {
        Assert.True(Decide().Rav1g);
        Assert.False(Decide("dav1d").Rav1g);
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
        // Since fork bale.46 the buffer no longer competes with rav1g's pool: 500 ms above 1080p, 1000 ms up to it.
        Assert.Equal(500, decision.BufferMs);
        Assert.Equal(1000, Decide(route: Route(width: 1920, height: 1080)).BufferMs);
        Assert.Equal(8, decision.DecodeAheadFrames);
        Assert.Equal(8, Decide(route: Route(width: 1920, height: 1080)).DecodeAheadFrames);
        Assert.Equal((1920, 1080), (decision.OutputWidth, decision.OutputHeight));
        Assert.True(decision.ToneMap);
        Assert.Equal(new Av1SourceFacts(10, 2, 5, 1, 24000.0 / 1001), decision.Source);
        // The frame rate no longer matters: the buffer follows the picture size only (fork bale.46+).
        Assert.Equal(500, Decide(version: Version(rate: "60")).BufferMs);
        var fits = Decide(route: Route(width: 1920, height: 1080), sdr: false);
        Assert.Null(fits.OutputWidth);
        Assert.Null(fits.OutputHeight);
        Assert.False(fits.ToneMap);
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
        Assert.Equal(expected, Rav1gPolicy.RemountOnDav1d(route, active, remounted, hdr, subtitle, budget));

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
        Assert.Equal("dav1d · switch dav1d", Rav1gPolicy.Describe("dav1d", false, true, false, null, null, null, null, false));
        Assert.Equal("rav1g direct · output 1920x1080 NV12 direct hdr=0",
            Rav1gPolicy.Describe("rav1g", true, true, true, "direct", null, null, "output 1920x1080 NV12 direct hdr=0", false));
        Assert.Equal("dav1d (rav1g fallback to dav1d (library missing, status=0x8007007E)) · rav1g.dll not packaged",
            Rav1gPolicy.Describe("rav1g", true, false, false, "dav1d", "fallback to dav1d (library missing, status=0x8007007E)",
                null, null, false));
        Assert.Equal("rav1g failed: stream failed (decode, hr=0x80042B23)",
            Rav1gPolicy.Describe("rav1g", true, true, true, null, null, "stream failed (decode, hr=0x80042B23)", null, false));
        Assert.Equal("dav1d (remounted after a rav1g failure) · switch rav1g",
            Rav1gPolicy.Describe("rav1g", false, true, false, null, null, null, null, true));
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

    [Fact]
    public void OutputMarkIsTheForksGuid() =>
        Assert.Equal(new Guid(0x6d553633, 0x9287, 0x4856, 0xbc, 0x9f, 0x0c, 0x23, 0xaf, 0x00, 0xe1, 0x84), Rav1gPolicy.OutputMark);
}
