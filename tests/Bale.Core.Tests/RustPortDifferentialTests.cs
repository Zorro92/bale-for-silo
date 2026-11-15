using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Reference = Bale.Core.Tests.Reference;

namespace Bale.Core.Tests;

/// <summary>
/// The Rust port (docs/rust-port.md) against the C# originals kept in Reference/: random inputs
/// drawn from the values that matter (aliases, casing, padding, nulls, NaN), same answers expected.
/// </summary>
public class RustPortDifferentialTests
{
    private const int Runs = 4000;

    private static readonly string?[] VideoCodecs =
        [null, "", " ", "hevc", "HEVC", " h265 ", "x265", "h264", "AVC", "vp9", "VP09", "av1", "mpeg4", "vc1", "mpeg2video", "dvhe", "İx"];
    private static readonly string?[] AudioCodecs =
        [null, "", "aac", "ac3", "eac3", "E-AC-3", "dts", "truehd", "MLP FBA", "pcm_s24le", "PCM_BLURAY", "flac", "mp3", "opus", "mp2"];
    private static readonly string?[] Deliveries =
        [null, "", "original_http", "ORIGINAL_HTTP", "progressive", "server_remux_progressive", "hls", "server_transcode_hls", "remux_hls", "other"];
    private static readonly string?[] Protocols = [null, "hls", "HLS", "http"];
    private static readonly string?[] Containers = [null, "", "mkv", " MKV ", "webm", "mp4", "avi", " AVI", "matroska,webm", "mka", "ts"];
    private static readonly string?[] Ranges = [null, "", "sdr", "SDR", " sdr ", "hdr10", "HDR10", " hdr10", "hlg", "dv", "hdr10_plus"];
    private static readonly string?[] Reasons =
    [
        null, PlaybackProtocol.ClientDolbyVision8BaseLayerReason, "CLIENT_DV8_BASE_LAYER", PlaybackProtocol.ClientDolbyVision7ToHdr10,
        PlaybackProtocol.ClientManagedDynamicRangeReason, "other",
    ];
    private static readonly string?[] Transfers = [null, "", "smpte2084", " SMPTE2084", "16", "arib-std-b67", "18", "bt709"];
    private static readonly string?[] Primaries = [null, "", "bt2020", "BT2020", "9", "bt709", "1", "smpte432"];
    private static readonly string?[] Matrices = [null, "", "bt2020nc", "9", "bt709", "1", "bt2020c"];
    private static readonly string?[] PixelFormats = [null, "", " yuv420p10le", "p010le", "yuv422p10le", "yuv420p"];
    private static readonly string?[] FileIds = [null, "a", "b", "c"];
    private static readonly long?[] Depths = [null, 8, 10, 12];
    private static readonly long?[] Profiles = [null, 0, 5, 7, 8];
    private static readonly long?[] Compat = [null, 0, 1, 2, 4, 6];
    private static readonly bool?[] Flags = [null, true, false];
    private static readonly double[] Rates = [0, 23.976, 24, 24.5, 25, 29.97, 30, 47.952, 48, 50, 59.94, 60, 72, 120, -1, double.NaN, double.PositiveInfinity];
    private static readonly uint[] Widths = [0, 720, 1280, 1920, 2560, 3840, 4096];
    private static readonly uint[] Heights = [0, 480, 720, 1080, 1440, 2160];

    private static readonly HardwareDecodeLimits Series = new(H264: new(1920, 1080), HevcMain: new(7680, 4320),
        HevcMain10: new(7680, 4320), Vp9Profile0: new(3840, 2160), Vp9Profile2: new(3840, 2160), Vc1: new(1920, 1080),
        Mpeg2: new(1920, 1080));

    [Fact]
    public void RouteAndToneMapDecisionsMatch()
    {
        var random = new Random(1);
        for (var run = 0; run < Runs; run++)
        {
            var plan = Plan(random);
            var versions = random.Next(4) == 0 ? null : Enumerable.Range(0, random.Next(4)).Select(_ => Version(random)).ToList();
            var (isHls, ffmpeg, force, sdr) = (random.Next(5) == 0, random.Next(6) != 0, random.Next(8) == 0, random.Next(2) == 0);
            Assert.Equal(Reference.PlaybackRoutePolicy.Decide(plan, isHls, ffmpeg, force, sdr, versions),
                PlaybackRoutePolicy.Decide(plan, isHls, ffmpeg, force, sdr, versions));
            Assert.Equal(Reference.ConsoleToneMapPolicy.RequiresToneMap(plan, sdr, versions),
                ConsoleToneMapPolicy.RequiresToneMap(plan, sdr, versions));
            Assert.Equal(Reference.DisplayHdrPolicy.RequiresHdr10(plan), DisplayHdrPolicy.RequiresHdr10(plan));
            Assert.Equal(Reference.DolbyVision7Policy.RequiresBaseLayer(plan), DolbyVision7Policy.RequiresBaseLayer(plan));
            Assert.Equal(Outcome(() => Reference.DolbyVision7Policy.ValidatePlan(plan)),
                Outcome(() => DolbyVision7Policy.ValidatePlan(plan)));
        }
    }

    [Fact]
    public void ToneMapEligibilityMatches()
    {
        var random = new Random(2);
        for (var run = 0; run < Runs; run++)
        {
            var version = Version(random);
            var device = Device(random);
            Assert.Equal(Reference.ConsoleToneMapPolicy.CanManage(version, device), ConsoleToneMapPolicy.CanManage(version, device));
            Assert.Equal(Reference.ConsoleToneMapPolicy.HasHdrOutput(device.Hdr), ConsoleToneMapPolicy.HasHdrOutput(device.Hdr));
        }
        foreach (var wrapper in new[] { "2.1.0-bale.29", "2.1.0-bale.28", "2.1.0-bale.100", "2.1.0-bale.", "2.1.0-bale.+30",
                     "2.1.0-bale. 30", "2.1.0-bale.99999999999", "2.1.0-bale.٣٠", "2.1.0-BALE.30", "" })
        {
            Assert.Equal(Reference.ConsoleToneMapPolicy.WrapperSupported(wrapper), ConsoleToneMapPolicy.WrapperSupported(wrapper));
        }
    }

    [Fact]
    public void TrackIndicesMatch()
    {
        var random = new Random(3);
        for (var run = 0; run < Runs; run++)
        {
            var indices = Enumerable.Range(0, random.Next(5)).Select(_ => random.Next(-1, 6)).ToArray();
            long? ordinal = random.Next(5) == 0 ? null : random.Next(-1, 6);
            int? expected = random.Next(3) == 0 ? null : random.Next(0, 6);
            Assert.Equal(Outcome(() => Reference.PlaybackRoutePolicy.AudioTrackIndex(ordinal, indices, expected)),
                Outcome(() => PlaybackRoutePolicy.AudioTrackIndex(ordinal, indices, expected)));
            Assert.Equal(Outcome(() => Reference.DolbyVision7Policy.BaseLayerTrackIndex(indices, expected)),
                Outcome(() => DolbyVision7Policy.BaseLayerTrackIndex(indices, expected)));
        }
    }

    [Fact]
    public void DisplayModeChoicesMatch()
    {
        var random = new Random(4);
        for (var run = 0; run < Runs; run++)
        {
            var modes = Enumerable.Range(0, random.Next(8)).Select(_ => Mode(random)).ToArray();
            // The current mode is often one of the listed ones, as on a real console.
            var current = modes.Length > 0 && random.Next(2) == 0 ? modes[random.Next(modes.Length)] : Mode(random);
            long width = Pick(random, Widths), height = Pick(random, Heights);
            var rate = Pick(random, Rates);
            var hdr = random.Next(2) == 0;
            Assert.Equal(Reference.DisplayMatchPolicy.PickMode(current, modes, width, height, rate, hdr),
                DisplayMatchPolicy.PickMode(current, modes, width, height, rate, hdr));
            Assert.Equal(Reference.DisplayHdrPolicy.PickHdr10Mode(current, modes), DisplayHdrPolicy.PickHdr10Mode(current, modes));
            var read = random.Next(4) != 0;
            var (expectedHdr, expectedEvidence) = Reference.DisplayHdrPolicy.FromModes(read, modes);
            var (actualHdr, actualEvidence) = DisplayHdrPolicy.FromModes(read, modes);
            Assert.Equal((expectedHdr.Hdr10, expectedHdr.Hdr10Plus, expectedHdr.Hlg, expectedHdr.DolbyVisionProfiles.Count, expectedEvidence),
                (actualHdr.Hdr10, actualHdr.Hdr10Plus, actualHdr.Hlg, actualHdr.DolbyVisionProfiles.Count, actualEvidence));
        }
    }

    [Fact]
    public void DecoderProbeFoldingMatches()
    {
        var random = new Random(5);
        Guid[] profiles =
        [
            HardwareDecodeLimits.H264Profile, HardwareDecodeLimits.HevcMainProfile, HardwareDecodeLimits.HevcMain10Profile,
            HardwareDecodeLimits.Vp9Profile0Profile, HardwareDecodeLimits.Vp9Profile2Profile, HardwareDecodeLimits.Vc1Profile,
            HardwareDecodeLimits.Mpeg2Profile, Guid.Empty, new("6f3ec719-3735-42cc-8063-65cc3cb36616"),
        ];
        foreach (var profile in profiles)
        {
            Assert.Equal(Reference.HardwareDecodeLimits.ProfileName(profile), HardwareDecodeLimits.ProfileName(profile));
        }
        for (var run = 0; run < Runs; run++)
        {
            var accepted = Enumerable.Range(0, random.Next(12)).Select(_ => (Pick(random, profiles),
                Pick(random, new[] { HardwareDecodeLimits.Nv12, HardwareDecodeLimits.P010, 87 }),
                Pick(random, new[] { 0, -1, 1280, 1920, 3840, 4096, 7680 }), Pick(random, new[] { 0, 720, 1080, 2160, 4320 }))).ToArray();
            var expected = Reference.HardwareDecodeLimits.FromAcceptedSizes(accepted);
            var actual = HardwareDecodeLimits.FromAcceptedSizes(accepted);
            Assert.Equal((expected.H264, expected.HevcMain, expected.HevcMain10, expected.Vp9Profile0, expected.Vp9Profile2, expected.Vc1, expected.Mpeg2),
                (actual.H264, actual.HevcMain, actual.HevcMain10, actual.Vp9Profile0, actual.Vp9Profile2, actual.Vc1, actual.Mpeg2));
        }
    }

    [Fact]
    public void SoftwareDecodeFiltersMatch()
    {
        Assert.Equal(Reference.SoftwareDecodePolicy.VideoCodecs, SoftwareDecodePolicy.VideoCodecs);
        Assert.Equal(Reference.SoftwareDecodePolicy.OriginalOnlyVideoCodecs, SoftwareDecodePolicy.OriginalOnlyVideoCodecs);
        Assert.Equal(Reference.SoftwareDecodePolicy.FfmpegOnlyContainers, SoftwareDecodePolicy.FfmpegOnlyContainers);
        Assert.Equal(Reference.SoftwareDecodePolicy.SystemVideoCodecs, SoftwareDecodePolicy.SystemVideoCodecs);
        Assert.Equal(Reference.SoftwareDecodePolicy.DemuxedVideoCodecs, SoftwareDecodePolicy.DemuxedVideoCodecs);
        Assert.Equal(Reference.SoftwareDecodePolicy.SystemAudioCodecs, SoftwareDecodePolicy.SystemAudioCodecs);
        Assert.Equal(Reference.SoftwareDecodePolicy.AudioCodecs, SoftwareDecodePolicy.AudioCodecs);
        foreach (var form in new[] { null, "", "Xbox One", " xbox one x", "XBOX ONE S", "Xbox Series X", "Xbox", "Windows.Xbox" })
        {
            foreach (var avx2 in new[] { true, false })
            {
                Assert.Equal(Reference.SoftwareDecodePolicy.SoftwareVideoSupported(avx2, form),
                    SoftwareDecodePolicy.SoftwareVideoSupported(avx2, form));
            }
        }

        var random = new Random(6);
        for (var run = 0; run < Runs; run++)
        {
            var device = Device(random);
            Assert.Equal(Project(Reference.SoftwareDecodePolicy.WithoutSoftwareVideo(device)), Project(SoftwareDecodePolicy.WithoutSoftwareVideo(device)));
            Assert.Equal(Project(Reference.SoftwareDecodePolicy.WithoutFallbacks(device)), Project(SoftwareDecodePolicy.WithoutFallbacks(device)));
            var (supported, zeroCopy) = (random.Next(2) == 0, random.Next(2) == 0);
            Assert.Equal(Project(Reference.SoftwareDecodePolicy.ForSoftwareVideo(device, supported, zeroCopy)),
                Project(SoftwareDecodePolicy.ForSoftwareVideo(device, supported, zeroCopy)));

            var streams = Enumerable.Range(0, random.Next(3))
                .Select(_ => (Pick(random, VideoCodecs), random.Next(2) == 0, random.Next(2) == 0)).ToArray();
            var software = random.Next(2) == 0;
            var softwareCodec = Pick(random, new string?[] { null, "av1", "mpeg4", "hevc" });
            Assert.Equal(Reference.SoftwareDecodePolicy.VideoDecodersAllowed(software, streams, softwareCodec),
                SoftwareDecodePolicy.VideoDecodersAllowed(software, streams, softwareCodec));

            var start = PlaybackStartBuilder.Build("file", "profile", "attempt", 0, PlaybackSelection.Auto, device,
                PlaybackTestSupport.Identity);
            var route = PlaybackRoutePolicy.Decide(Plan(random), false, sdrOutput: random.Next(2) == 0);
            Assert.Equal(Project(Reference.SoftwareDecodePolicy.WithoutFailedFallbacks(start, route)),
                Project(SoftwareDecodePolicy.WithoutFailedFallbacks(start, route)));
        }
    }

    [Fact]
    public void ConsoleEvidenceMatches()
    {
        Assert.Equal(Reference.ConsoleEvidence.EmbeddedBitmapSubtitleCodecs, ConsoleEvidence.EmbeddedBitmapSubtitleCodecs);
        Assert.Equal(Full(Reference.ConsoleEvidence.Default), Full(ConsoleEvidence.Default));
        Assert.Equal(Full(Reference.ConsoleEvidence.ForHardware(null)), Full(ConsoleEvidence.ForHardware(null)));
        Assert.Same(ConsoleEvidence.Default, ConsoleEvidence.ForHardware(new HardwareDecodeLimits()));

        var random = new Random(7);
        DecodeSize?[] sizes = [null, null, new(1920, 1080), new(3840, 2160), new(4096, 2304), new(7680, 4320)];
        for (var run = 0; run < Runs; run++)
        {
            var limits = new HardwareDecodeLimits(Pick(random, sizes), Pick(random, sizes), Pick(random, sizes), Pick(random, sizes),
                Pick(random, sizes), Pick(random, sizes), Pick(random, sizes)) { Partial = random.Next(3) == 0 };
            var hardware = random.Next(10) == 0 ? null : limits;
            Assert.Equal(Full(Reference.ConsoleEvidence.ForHardware(hardware)), Full(ConsoleEvidence.ForHardware(hardware)));
            int width = Pick(random, new[] { 1, 1280, 1920, 2560, 3840 }), height = Pick(random, new[] { 1, 720, 1080, 1440, 2160 });
            Assert.Equal(Claims(Reference.ConsoleEvidence.BuildVideoDecode(hardware, width, height)),
                Claims(ConsoleEvidence.BuildVideoDecode(hardware, width, height)));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ConsoleEvidence.BuildVideoDecode(0, 1080));
    }

    private static string Claims(IEnumerable<VideoDecodeCapability> claims) => string.Join(" ; ", claims.Select(claim =>
        $"{claim.Codec}/{claim.DecoderName}/{string.Join(",", claim.Profiles)}/{string.Join(",", claim.Levels)}/"
        + $"{string.Join(",", claim.BitDepths)}/{claim.MaxWidth}x{claim.MaxHeight}@{claim.MaxFrameRate}/{claim.MaxBitrateKbps}/{claim.Hardware}"));

    private static string Full(DeviceCapabilities device) => string.Join(" | ",
        Project(device), Claims(device.VideoDecode), string.Join(",", device.AudioPassthroughCodecs), device.MaxChannels,
        $"{device.Hdr.Hdr10}{device.Hdr.Hdr10Plus}{device.Hdr.Hlg}{string.Join(",", device.Hdr.DolbyVisionProfiles)}",
        device.VideoEvidence, device.AudioEvidence, string.Join(",", device.HardwareVideoCodecs), device.PassthroughEntries.Count,
        device.DisplayHdrEvidence, device.OriginalAudioTrackSelectionSupported,
        $"{device.Subtitles.EmbeddedText}{device.Subtitles.SidecarText}{device.Subtitles.AssStyling}{device.Subtitles.EmbeddedBitmap}"
        + $"{device.Subtitles.SidecarBitmap}{device.Subtitles.FontAttachments}",
        string.Join(",", device.Subtitles.NativeEmbedded.Select(native =>
            $"{native.Container}:{string.Join("+", native.Codecs)}:{native.TrackIdentity}:{native.AssStyling}:{native.FontAttachments}")));

    private static string Outcome(Action action)
    {
        try
        {
            action();
            return "ok";
        }
        catch (InvalidDataException error)
        {
            return error.Message;
        }
    }

    private static string Outcome(Func<int> function)
    {
        try
        {
            return function().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidDataException error)
        {
            return error.Message;
        }
    }

    private static string Project(DeviceCapabilities device) => string.Join(" | ",
        string.Join(",", device.VideoCodecs), string.Join(",", device.VideoDecode.Select(d => $"{d.Codec}:{d.Hardware}:{d.MaxWidth}")),
        string.Join(",", device.AudioDecodeCodecs), string.Join(",", device.Containers),
        device.OriginalDolbyVision8BaseLayerSupported, device.OriginalDolbyVision7BaseLayerSupported,
        device.OriginalDynamicRangeMappingSupported, device.ProgressiveSupported, device.HlsSupported, device.OriginalHttpSupported);

    private static string Project(PlaybackStartRequestV3 start)
    {
        var capabilities = start.ClientCapabilities;
        return string.Join(" | ",
            string.Join(",", capabilities.CodecsVideo), string.Join(",", capabilities.CodecsVideoHardware),
            string.Join(",", capabilities.VideoDecode.Select(d => $"{d.Codec}:{d.Hardware}")), string.Join(",", capabilities.CodecsAudio),
            string.Join(",", capabilities.Containers), string.Join(",", start.ClientFeatures),
            string.Join(";", start.ClientPlaybackContext.Deliveries.Select(pair => $"{pair.Key}={string.Join(",", pair.Value.VideoCodecs)}/"
                + $"{string.Join(",", pair.Value.AudioDecodeCodecs)}/{string.Join(",", pair.Value.Containers)}/{string.Join(",", pair.Value.ValidatedClaims)}"
                + $"/{pair.Value.Enabled}/{pair.Value.Transformations.Count}")));
    }

    private static T Pick<T>(Random random, IReadOnlyList<T> values) => values[random.Next(values.Count)];

    private static PlaybackPlan Plan(Random random)
    {
        var plan = new PlaybackPlan
        {
            Delivery = Pick(random, Deliveries),
            DecisionReason = Pick(random, Reasons),
            EffectiveMediaFileId = Pick(random, FileIds),
            Source = random.Next(8) == 0 ? null : new PlaybackSource
            {
                MediaFileId = Pick(random, FileIds),
                Container = Pick(random, Containers),
                VideoCodec = Pick(random, VideoCodecs),
                AudioCodec = Pick(random, AudioCodecs),
                Width = Pick(random, new long?[] { null, 1920, 3840 }),
                Height = Pick(random, new long?[] { null, 1080, 2160 }),
                BitDepth = Pick(random, Depths),
                DynamicRange = Pick(random, Ranges),
                DolbyVisionProfile = Pick(random, Profiles),
                DvBlCompatId = Pick(random, Compat),
                Hdr10Plus = Pick(random, Flags),
                FrameRate = Pick(random, new double?[] { null, 23.976, double.NaN }),
            },
            EffectiveRecipe = random.Next(3) == 0 ? null : new EffectiveRecipeV3
            {
                VideoCodec = Pick(random, VideoCodecs),
                AudioCodec = Pick(random, AudioCodecs),
                Width = Pick(random, new long?[] { null, 1280, 1920 }),
                Height = Pick(random, new long?[] { null, 720, 1080 }),
                DynamicRange = Pick(random, Ranges),
            },
            Stream = random.Next(3) == 0 ? null : new StreamV3 { Protocol = Pick(random, Protocols), Container = Pick(random, Containers) },
            SelectedTracks = random.Next(3) == 0 ? null : new SelectedTracksV3
            {
                Audio = random.Next(4) == 0 ? null : new TrackIdentityV3 { Index = Pick(random, new long?[] { null, 0, 1, 3 }) },
            },
            Claims = random.Next(3) == 0 ? null : new ValidationClaimsV3
            {
                Video = new VideoClaimsV3
                {
                    Hdr10 = Pick(random, Flags), Hdr10Plus = Pick(random, Flags), Hlg = Pick(random, Flags), DolbyVision = Pick(random, Flags),
                },
            },
        };
        if (random.Next(3) == 0)
        {
            plan.Transformations = Enumerable.Range(0, random.Next(3)).Select(_ => new TransformationV3
            {
                Name = Pick(random, new string?[] { null, PlaybackProtocol.ClientDolbyVision7ToHdr10, "other" }),
                Executor = Pick(random, new string?[] { null, "client", "server", "Client" }),
                RecipeVersion = Pick(random, new string?[] { null, "1", "2" }),
                ValidatedClaims = random.Next(4) == 0 ? null
                    : random.Next(2) == 0 ? DolbyVision7Policy.Transformation.ValidatedClaims.Reverse().ToList()
                    : DolbyVision7Policy.Transformation.ValidatedClaims.Take(random.Next(4)).ToList(),
            }).ToList();
        }
        return plan;
    }

    private static FileVersion Version(Random random) => new()
    {
        FileId = Pick(random, FileIds),
        CodecVideo = Pick(random, VideoCodecs),
        Resolution = Pick(random, new string?[] { null, "1080p", "4K", "720p" }),
        VideoTracks = random.Next(6) == 0 ? null : Enumerable.Range(0, Pick(random, new[] { 0, 1, 1, 1, 2 })).Select(_ => new VideoTrack
        {
            Codec = Pick(random, VideoCodecs),
            BitDepth = Pick(random, Depths),
            PixelFormat = Pick(random, PixelFormats),
            ColorTransfer = Pick(random, Transfers),
            ColorPrimaries = Pick(random, Primaries),
            ColorSpace = Pick(random, Matrices),
            DvProfile = Pick(random, Profiles),
            DvBlPresent = Pick(random, Flags),
            DvConfigPresent = Pick(random, Flags),
            DvElPresent = Pick(random, Flags),
            DvBlCompatId = Pick(random, Compat),
            DvBlCompatIdPresent = Pick(random, Flags),
            DolbyVision = Pick(random, new string?[] { null, "", "dvhe.08" }),
            Width = Pick(random, new long?[] { null, 1920, 3840 }),
            Height = Pick(random, new long?[] { null, 1080, 2160 }),
            Profile = Pick(random, new string?[] { null, "Main 10", "main" }),
        }).ToList(),
    };

    private static DeviceCapabilities Device(Random random)
    {
        var claims = ConsoleEvidence.ForHardware(Series);
        var decode = claims.VideoDecode.Where(_ => random.Next(4) != 0).ToArray();
        return claims with
        {
            VideoCodecs = claims.VideoCodecs.Concat(["vc1", "mpeg2video", "vp9"]).Where(_ => random.Next(4) != 0).Distinct().ToArray(),
            VideoDecode = decode,
            AudioDecodeCodecs = claims.AudioDecodeCodecs.Where(_ => random.Next(3) != 0).ToArray(),
            Containers = claims.Containers.Where(_ => random.Next(3) != 0).ToArray(),
            OriginalHttpSupported = random.Next(6) != 0,
            OriginalDynamicRangeMappingSupported = random.Next(4) != 0,
            OriginalDolbyVision7BaseLayerSupported = random.Next(2) == 0,
            OriginalDolbyVision8BaseLayerSupported = random.Next(2) == 0,
            ProgressiveSupported = random.Next(2) == 0,
            DisplayHdrEvidence = random.Next(4) == 0 ? "unknown" : "exact",
            Hdr = random.Next(3) == 0 ? new HdrCapabilities { Hdr10 = random.Next(2) == 0, Hlg = random.Next(3) == 0 } : new(),
        };
    }

    private static DisplayModeFacts Mode(Random random) =>
        new(random.Next(2) == 0, random.Next(2) == 0, random.Next(8) == 0)
        {
            Width = Pick(random, Widths), Height = Pick(random, Heights), RefreshRate = Pick(random, Rates),
        };
}
