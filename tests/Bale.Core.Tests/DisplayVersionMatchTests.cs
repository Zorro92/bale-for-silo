using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>
/// Display resolution matching for Auto (docs/player-plan.md). Every case passes its match to
/// SelectVersion; none sets DisplayVersionMatch.Current, which other test classes read in parallel.
/// </summary>
public class DisplayVersionMatchTests
{
    private static readonly VideoDecodeCapability Hevc4K = new("hevc", "test", [], [], [8, 10], 3840, 2160, 60, null, true);
    private static readonly VideoDecodeCapability Hevc1080 = new("hevc", "test", [], [], [8, 10], 1920, 1080, 60, 25000, true);
    private static readonly VideoDecodeCapability H2641080 = new("h264", "test", [], [], [8], 1920, 1080, 60, 25000, true);
    private static readonly VideoDecodeCapability Av1Main1080 = new("av1", "test", ["main"], [], [8, 10], 1920, 1080, 60, null, false);

    [Fact]
    public void A1080DisplayTakesThe1080VersionOverThe4KOne()
    {
        var versions = new[] { Hevc("4k", 3840, 2160), Hevc("1080", 1920, 1080), Hevc("720", 1280, 720) };
        Assert.Same(versions[1], Select(versions, 1080, Hevc4K));
        Assert.Same(versions[1], Select(versions, 1080, Hevc1080));
        Assert.Same(versions[1], PlaybackFormatting.SelectVersion(versions, Match(1080, Hevc4K), preferredQuality: " Auto "));
    }

    [Fact]
    public void A4KDisplayTakesThe4KVersionOnlyWhenAClaimAdmitsIt()
    {
        var versions = new[] { Hevc("1080", 1920, 1080), Hevc("4k", 3840, 2160) };
        Assert.Same(versions[1], Select(versions, 2160, Hevc4K));
        Assert.Same(versions[0], Select(versions, 2160, Hevc1080));
    }

    [Fact]
    public void WithNothingAtOrBelowTheDisplayTheSmallestVersionAboveItWins()
    {
        var versions = new[] { Hevc("4k", 3840, 2160), Hevc("1080", 1920, 1080) };
        Assert.Same(versions[1], Select(versions, 720, Hevc4K));
        Assert.Same(versions[1], Select(versions, 720));
        // The smaller version wins even when only the larger one decodes; decodability
        // only breaks a tie between versions of the same height.
        Assert.Same(versions[1], Select(versions, 720, new VideoDecodeCapability("hevc", "test", [], [], [8, 10], 3840, 2160, 60, 90000, true)));
        var same = new[] { Version("mpeg2", "mpeg2video", 1920, 1080, bitrate: 2), Hevc("hevc", 1920, 1080, bitrate: 1) };
        Assert.Same(same[1], Select(same, 720, Hevc1080));
        Assert.Same(same[0], Select(same, 720));
    }

    [Fact]
    public void AFittingVersionTheClaimsDoNotAdmitStillBeatsOneAboveTheDisplay()
    {
        var versions = new[] { Hevc("4k", 3840, 2160), Version("720", "mpeg2video", 1280, 720) };
        Assert.Same(versions[1], Select(versions, 1080, Hevc4K));
        // With nothing decodable at or below the display, the official order decides.
        var fitting = new[] { Version("720", "vc1", 1280, 720), Version("1080", "mpeg2video", 1920, 1080) };
        Assert.Same(fitting[1], Select(fitting, 1080, Hevc4K));
    }

    [Fact]
    public void AVersionWithAnotherCodecLosesToOneAClaimAdmits()
    {
        var versions = new[] { Version("vc1", "vc1", 1920, 1080), Version("h264", "h264", 1280, 720, bitDepth: 8) };
        Assert.Same(versions[1], Select(versions, 1080, H2641080, Hevc1080));
        // Codec aliases count as the claimed codec (SoftwareDecodePolicy.NormalizeCodec).
        var alias = new[] { Version("h265", "H265", 1920, 1080), Version("h264", "avc", 1280, 720, bitDepth: 8) };
        Assert.Same(alias[0], Select(alias, 1080, H2641080, Hevc1080));
    }

    [Fact]
    public void AVersionWithAnUnclaimedBitDepthLosesToOneAClaimAdmits()
    {
        var versions = new[] { Version("hi10", "h264", 1920, 1080, bitDepth: 10), Version("8bit", "h264", 1280, 720, bitDepth: 8) };
        Assert.Same(versions[1], Select(versions, 1080, H2641080));
        versions[0].VideoTracks![0].BitDepth = 8;
        Assert.Same(versions[0], Select(versions, 1080, H2641080));
        // An unknown bit depth doesn't reject.
        versions[0].VideoTracks![0].BitDepth = null;
        Assert.Same(versions[0], Select(versions, 1080, H2641080));
    }

    [Fact]
    public void AVersionAboveTheClaimedBitrateLosesToOneAClaimAdmits()
    {
        var versions = new[] { Hevc("remux", 1920, 1080, bitrate: 40000), Hevc("web", 1920, 1080, bitrate: 8000) };
        Assert.Same(versions[1], Select(versions, 1080, Hevc1080));
        Assert.Same(versions[0], Select(versions, 1080, Hevc4K)); // No bitrate bound.
        versions[0].Bitrate = 25000;
        Assert.Same(versions[0], Select(versions, 1080, Hevc1080));
    }

    [Fact]
    public void TheVideoTracksOwnBitrateCountsBeforeTheFilesWhichIncludesAudio()
    {
        // silo-server SourceVideoKbps: 24 Mbps of video in a 29 Mbps file fits a 25 Mbps claim.
        var versions = new[] { Hevc("remux", 1920, 1080, bitrate: 29000), Hevc("web", 1920, 1080, bitrate: 8000) };
        versions[0].VideoTracks![0].Bitrate = 24000;
        Assert.Same(versions[0], Select(versions, 1080, Hevc1080));
        versions[0].VideoTracks![0].Bitrate = 26000;
        Assert.Same(versions[1], Select(versions, 1080, Hevc1080));
    }

    [Fact]
    public void ABitrateInBitsPerSecondIsReadAsKbpsLikeTheServerDoes()
    {
        // silo-server normalizeBitrateKbpsV3: above 10,000,000 the value is bits per second.
        var versions = new[] { Hevc("remux", 1920, 1080, bitrate: 24_000_000), Hevc("web", 1920, 1080, bitrate: 8000) };
        Assert.Same(versions[0], Select(versions, 1080, Hevc1080));
        versions[0].Bitrate = 26_000_000;
        Assert.Same(versions[1], Select(versions, 1080, Hevc1080));
    }

    [Theory]
    [InlineData("120", false)]
    [InlineData("60000/1001", true)]
    [InlineData("59.94 fps", true)]
    [InlineData("60", true)]
    [InlineData("60.02", false)]
    [InlineData("120000/1001", false)]
    [InlineData(null, true)]
    [InlineData("unknown", true)]
    [InlineData("24/0", true)]
    public void AVersionAboveTheClaimedFrameRateLosesToOneAClaimAdmits(string? frameRate, bool admitted)
    {
        var versions = new[] { Hevc("fast", 1920, 1080, frameRate: frameRate), Hevc("film", 1280, 720, frameRate: "24000/1001") };
        Assert.Same(versions[admitted ? 0 : 1], Select(versions, 1080, Hevc1080));
    }

    [Theory]
    [InlineData("Main", true)]
    [InlineData(" main ", true)]
    [InlineData("High", false)]
    [InlineData("Professional", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Av1NeedsTheClaimedMainProfile(string? profile, bool admitted)
    {
        var versions = new[]
        {
            Version("av1", "av1", 1920, 1080, bitDepth: 10, profile: profile),
            Version("h264", "h264", 1280, 720, bitDepth: 8, profile: "High"),
        };
        Assert.Same(versions[admitted ? 0 : 1], Select(versions, 1080, H2641080, Av1Main1080));
    }

    [Fact]
    public void AVersionWiderOrTallerThanTheClaimLosesToOneAClaimAdmits()
    {
        // A 1080p label on a file wider than the claim: the track's dimensions decide.
        var versions = new[] { Version("wide", "hevc", 2560, 1080, resolution: "1080p", bitDepth: 8), Hevc("720", 1280, 720) };
        Assert.Same(versions[1], Select(versions, 1080, Hevc1080));
        Assert.Same(versions[0], Select(versions, 1080, Hevc4K));
    }

    [Fact]
    public void WithoutVideoTracksTheResolutionLabelAndFileCodecDecide()
    {
        var versions = new[]
        {
            new FileVersion { FileId = "4k", Resolution = "4K", CodecVideo = "hevc" },
            new FileVersion { FileId = "1080", Resolution = "1080p", CodecVideo = "hevc" },
            new FileVersion { FileId = "none", Resolution = "1080p", Bitrate = 90000 },
        };
        Assert.Same(versions[1], Select(versions, 1080, Hevc1080));
        Assert.Same(versions[1], Select(versions, 2160, Hevc1080));
        Assert.Same(versions[0], Select(versions, 2160, Hevc4K));
        // No label: the first video track's height is the version's height.
        var unlabelled = new[] { Version("4k", "hevc", 3840, 1600, resolution: null), Version("1080", "hevc", 1920, 800, resolution: null) };
        Assert.Same(unlabelled[1], Select(unlabelled, 1080, Hevc4K));
        Assert.Same(unlabelled[0], Select(unlabelled, 2160, Hevc4K));
    }

    [Fact]
    public void TiesKeepTheOfficialBitrateThenSizeOrder()
    {
        var versions = new[]
        {
            Hevc("4k", 3840, 2160, bitrate: 20000),
            Hevc("low", 1920, 1080, bitrate: 4000, size: 9),
            Hevc("small", 1920, 1080, bitrate: 8000, size: 1),
            Hevc("large", 1920, 1080, bitrate: 8000, size: 2),
        };
        Assert.Same(versions[3], Select(versions, 1080, Hevc1080));
        Assert.Same(versions[3], Select(versions, 1080));
        Assert.Same(versions[3], Select(versions[1..], 720, Hevc1080));
    }

    [Fact]
    public void AnExplicitPickAndTheLastPlayedFileStillComeFirst()
    {
        var versions = new[] { Hevc("4k", 3840, 2160), Hevc("1080", 1920, 1080), Hevc("720", 1280, 720) };
        var match = Match(1080, Hevc1080);
        Assert.Same(versions[0], PlaybackFormatting.SelectVersion(versions, match, selectedFileId: "4k"));
        Assert.Same(versions[0], PlaybackFormatting.SelectVersion(versions, match, lastFileId: "4k"));
        Assert.Same(versions[2], PlaybackFormatting.SelectVersion(versions, match, "720", "4k", "auto"));
        Assert.Same(versions[1], PlaybackFormatting.SelectVersion(versions, match, "missing", "missing"));
    }

    [Theory]
    [InlineData("original")]
    [InlineData(" Original ")]
    [InlineData("source")]
    [InlineData("max")]
    public void AnOriginalStyleChoiceIgnoresTheDisplay(string quality)
    {
        var versions = new[] { Hevc("1080", 1920, 1080), Hevc("4k", 3840, 2160) };
        Assert.Same(versions[1], PlaybackFormatting.SelectVersion(versions, Match(1080, Hevc1080), preferredQuality: quality));
    }

    [Fact]
    public void AResolutionCapKeepsTheOfficialCapLogic()
    {
        var versions = new[] { Hevc("1080", 1920, 1080), Hevc("4k", 3840, 2160), Hevc("720", 1280, 720) };
        var match = Match(2160, Hevc4K);
        Assert.Same(versions[2], PlaybackFormatting.SelectVersion(versions, match, preferredQuality: "720p"));
        Assert.Same(versions[0], PlaybackFormatting.SelectVersion(versions, match, preferredQuality: "1080p"));
        // An unsatisfied cap still falls back to the best version, not to the display's.
        Assert.Same(versions[1], PlaybackFormatting.SelectVersion(versions, Match(1080, Hevc1080), preferredQuality: "480p"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("original")]
    [InlineData("720p")]
    public void ANullMatchIsTheOfficialHighestFirstChoice(string? quality)
    {
        var versions = new[] { Hevc("1080", 1920, 1080), Hevc("4k", 3840, 2160), Hevc("720", 1280, 720) };
        var expected = quality == "720p" ? versions[2] : versions[1];
        Assert.Same(expected, PlaybackFormatting.SelectVersion(versions, null, preferredQuality: quality));
        Assert.Same(versions[0], PlaybackFormatting.SelectVersion(versions, null, "1080", "4k", quality));
        Assert.Same(versions[2], PlaybackFormatting.SelectVersion(versions, null, lastFileId: "720", preferredQuality: quality));
    }

    [Fact]
    public void NoVersionsGiveNoPick()
    {
        Assert.Null(PlaybackFormatting.SelectVersion([], Match(1080, Hevc1080)));
        Assert.Null(PlaybackFormatting.SelectVersion([], null));
    }

    [Fact]
    public void TheProductionClaimsWorkAsTheClaimList()
    {
        var claims = ConsoleEvidence.Default.VideoDecode;
        var hevc = new[] { Hevc("4k", 3840, 2160, bitDepth: 10), Hevc("1080", 1920, 1080, bitDepth: 10) };
        // Production claims stop at 1080p, so even a 4K display plays the 1080p file as it is
        // rather than a transcoded 4K one; a title with only a 4K file still gets that file.
        Assert.Same(hevc[1], PlaybackFormatting.SelectVersion(hevc, new DisplayVersionMatch(1080, claims)));
        Assert.Same(hevc[1], PlaybackFormatting.SelectVersion(hevc, new DisplayVersionMatch(2160, claims)));
        Assert.Same(hevc[0], PlaybackFormatting.SelectVersion(hevc[..1], new DisplayVersionMatch(2160, claims)));

        var av1 = new[]
        {
            Version("av1-60", "av1", 1920, 1080, bitDepth: 10, bitrate: 5, frameRate: "60000/1001", profile: "Main"),
            Version("h264", "h264", 1920, 1080, bitDepth: 8, bitrate: 1, profile: "High"),
            Version("av1-high", "av1", 1920, 1080, bitDepth: 10, bitrate: 9, profile: "High"),
        };
        Assert.Same(av1[0], PlaybackFormatting.SelectVersion(av1, new DisplayVersionMatch(1080, claims)));
        // Without software video (Xbox One), AV1 loses to a version the system decoders take.
        var hardware = SoftwareDecodePolicy.WithoutSoftwareVideo(ConsoleEvidence.Default).VideoDecode;
        Assert.Same(av1[1], PlaybackFormatting.SelectVersion(av1, new DisplayVersionMatch(1080, hardware)));
        // The debug 4K AV1 bound is a separate 30 fps entry.
        var debug = ConsoleEvidence.BuildVideoDecode(3840, 2160);
        var av14K = new[]
        {
            Version("4k60", "av1", 3840, 2160, bitDepth: 10, bitrate: 2, frameRate: "60", profile: "Main"),
            Version("4k24", "av1", 3840, 2160, bitDepth: 10, bitrate: 1, frameRate: "24000/1001", profile: "Main"),
        };
        Assert.Same(av14K[1], PlaybackFormatting.SelectVersion(av14K, new DisplayVersionMatch(2160, debug)));
    }

    [Fact]
    public void TheDisplayHeightIsItsLargestSupportedMode()
    {
        var claims = ConsoleEvidence.Default.VideoDecode;
        var match = DisplayVersionMatch.FromModes([Mode(1920, 1080), Mode(3840, 2160), Mode(1280, 720)], claims);
        Assert.Equal(2160, match?.DisplayMaxHeight);
        Assert.Same(claims, match?.VideoDecode);
        Assert.Equal(1080, DisplayVersionMatch.FromModes([Mode(1920, 1080)], claims)?.DisplayMaxHeight);
    }

    [Fact]
    public void NoReadableModeGivesNoMatch()
    {
        Assert.Null(DisplayVersionMatch.FromModes([], ConsoleEvidence.Default.VideoDecode));
        Assert.Null(DisplayVersionMatch.FromModes([Mode(0, 0)], ConsoleEvidence.Default.VideoDecode));
    }

    private static FileVersion? Select(IReadOnlyList<FileVersion> versions, int displayHeight, params VideoDecodeCapability[] claims) =>
        PlaybackFormatting.SelectVersion(versions, Match(displayHeight, claims));

    private static DisplayVersionMatch Match(int displayHeight, params VideoDecodeCapability[] claims) => new(displayHeight, claims);

    private static DisplayModeFacts Mode(uint width, uint height) => new(false, false, false) { Width = width, Height = height, RefreshRate = 60 };

    private static FileVersion Hevc(string id, int width, int height, long? bitrate = null, long? size = null,
        long? bitDepth = 8, string? frameRate = "24000/1001") =>
        Version(id, "hevc", width, height, bitrate: bitrate, size: size, bitDepth: bitDepth, frameRate: frameRate);

    private static FileVersion Version(string id, string codec, int width, int height, string? resolution = "",
        long? bitrate = null, long? size = null, long? bitDepth = null, string? frameRate = null, string? profile = null) => new()
    {
        FileId = id,
        // The server's label follows the frame's width class; these cases only need its height.
        Resolution = resolution == "" ? (width > 1920 ? "2160p" : width > 1280 ? "1080p" : "720p") : resolution,
        CodecVideo = codec,
        Bitrate = bitrate,
        FileSize = size,
        VideoTracks = [new VideoTrack { Codec = codec, Width = width, Height = height, BitDepth = bitDepth, FrameRate = frameRate, Profile = profile }],
    };
}
