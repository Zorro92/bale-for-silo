using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

/// <summary>
/// rav1g's display output tone-maps AV1 HDR on the console (debug rav1g route), so the original-file range promise covers
/// AV1 only when the app says so; HEVC/VP9 keep their hardware-decode rule and AV1 Dolby Vision is never promised.
/// </summary>
public sealed class Av1ToneMapPromiseTests
{
    private static FileVersion Av1(int? dvProfile = null) => new()
    {
        FileId = "file", CodecVideo = "av1", Container = "mkv",
        VideoTracks = [new() { Codec = "av1", Profile = "main", BitDepth = 10, PixelFormat = "yuv420p10le",
            ColorTransfer = "smpte2084", ColorPrimaries = "bt2020", ColorSpace = "bt2020nc",
            Width = 3840, Height = 2024, FrameRate = "23.976", DvProfile = dvProfile,
            DvConfigPresent = dvProfile is not null, DvBlPresent = dvProfile is not null }],
    };

    private static DeviceCapabilities Device(bool av1Mapping) => ConsoleEvidence.Default with
    {
        DisplayHdrEvidence = "exact",
        OriginalDynamicRangeMappingSupported = true,
        VideoDecode = ConsoleEvidence.BuildVideoDecode(3840, 2160),
        Av1DynamicRangeMappingSupported = av1Mapping,
    };

    [Fact]
    public void Av1HdrIsPromisedOnlyWhenTheAppMapsAv1()
    {
        Assert.False(ConsoleToneMapPolicy.CanManage(Av1(), Device(false)));
        Assert.True(ConsoleToneMapPolicy.CanManage(Av1(), Device(true)));
    }

    [Fact]
    public void Av1NeedsTheGeneralMappingSwitchAndAnSdrDisplay()
    {
        Assert.False(ConsoleToneMapPolicy.CanManage(Av1(), Device(true) with { OriginalDynamicRangeMappingSupported = false }));
        Assert.False(ConsoleToneMapPolicy.CanManage(Av1(), Device(true) with { DisplayHdrEvidence = "unknown" }));
    }

    [Fact]
    public void Av1DolbyVisionIsNeverPromised()
        => Assert.False(ConsoleToneMapPolicy.CanManage(Av1(dvProfile: 10), Device(true)));

    [Fact]
    public void Av1AboveTheDecodeClaimIsNotPromised()
    {
        var device = Device(true) with { VideoDecode = ConsoleEvidence.BuildVideoDecode(1920, 1080) };
        Assert.False(ConsoleToneMapPolicy.CanManage(Av1(), device));
    }
}
