using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class DisplayHdrPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    public void OnlyNonStereoPqModesClaimHdr10(bool pq, bool metadata, bool stereo, bool expected)
    {
        var (hdr, evidence) = DisplayHdrPolicy.FromModes(true, [new(pq, metadata, stereo)]);
        Assert.Equal(expected, hdr.Hdr10);
        Assert.False(hdr.Hdr10Plus);
        Assert.False(hdr.Hlg);
        Assert.Empty(hdr.DolbyVisionProfiles);
        Assert.Equal("exact", evidence);
    }

    [Fact]
    public void AnyUsableModeIsEnoughEvenWhenTheCurrentOutputIsSdr()
    {
        var (hdr, evidence) = DisplayHdrPolicy.FromModes(true,
            [new(false, false, false), new(true, true, true), new(true, false, false)]);
        Assert.True(hdr.Hdr10);
        Assert.Equal("exact", evidence);
    }

    [Theory]
    [InlineData(true, "exact")]
    [InlineData(false, "unknown")]
    public void EmptyModesNeverClaimHdr(bool succeeded, string expectedEvidence)
    {
        var (hdr, evidence) = DisplayHdrPolicy.FromModes(succeeded, []);
        AssertNoHdr(hdr);
        Assert.Equal(expectedEvidence, evidence);
    }

    [Fact]
    public void AFailedReadDiscardsEvenHdrFactsAndTheBaselineIsUnknown()
    {
        var (hdr, evidence) = DisplayHdrPolicy.FromModes(false, [new(true, true, false)]);
        AssertNoHdr(hdr);
        Assert.Equal("unknown", evidence);
        AssertNoHdr(ConsoleEvidence.Default.Hdr);
        Assert.Equal("unknown", ConsoleEvidence.Default.DisplayHdrEvidence);
    }

    [Theory]
    [InlineData(true, true, "exact", true)]
    [InlineData(true, false, "exact", false)]
    [InlineData(false, true, "unknown", false)]
    public void DetectedDisplayIsSerializedInAllCapabilityLocations(bool succeeded, bool pq, string evidence, bool expectedHdr)
    {
        var display = DisplayHdrPolicy.FromModes(succeeded, [new(pq, true, false)]);
        var device = ConsoleEvidence.Default with { Hdr = display.Hdr, DisplayHdrEvidence = display.Evidence };
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", 0, PlaybackSelection.Auto,
            device, PlaybackTestSupport.Identity);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(start, PlaybackJson.Default.PlaybackStartRequestV3));
        var caps = json.RootElement.GetProperty("client_capabilities");
        Assert.Equal(expectedHdr, caps.GetProperty("hdr").GetBoolean());
        Assert.Equal(expectedHdr, caps.GetProperty("hdr_details").GetProperty("hdr10").GetBoolean());
        var context = json.RootElement.GetProperty("client_playback_context");
        var output = context.GetProperty("output");
        Assert.Equal(expectedHdr, output.GetProperty("hdr_details").GetProperty("hdr10").GetBoolean());
        Assert.Equal(evidence, output.GetProperty("display").GetProperty("hdr_evidence").GetString());
        Assert.Equal(expectedHdr, output.GetProperty("display").GetProperty("hdr_types").GetProperty("hdr10").GetBoolean());
        foreach (var delivery in context.GetProperty("deliveries").EnumerateObject())
            Assert.Equal(expectedHdr, delivery.Value.GetProperty("hdr_details").GetProperty("hdr10").GetBoolean());
    }

    [Fact]
    public void SelectionKeepsCurrentResolutionAndRefreshAndPrefersMetadataOnATie()
    {
        var current = Mode(1920, 1080, 59.94, pq: false);
        DisplayModeFacts[] modes =
        [
            Mode(3840, 2160, 59.94), Mode(1920, 1080, 60), Mode(1920, 1080, 59.94),
            Mode(1920, 1080, 59.94, metadata: true), Mode(1920, 1080, 50),
        ];
        Assert.Equal(3, DisplayHdrPolicy.PickHdr10Mode(current, modes));
    }

    [Fact]
    public void SelectionFallsBackToClosestHoldingResolutionThenRefresh()
    {
        var current = Mode(1920, 1080, 59.94);
        DisplayModeFacts[] modes =
        [
            Mode(1280, 720, 59.94), Mode(1920, 1080, 59.94, stereo: true), Mode(1920, 1080, 59.94, pq: false),
            Mode(4096, 2160, 59.94), Mode(3840, 2160, 60), Mode(3840, 2160, 59.94),
        ];
        Assert.Equal(5, DisplayHdrPolicy.PickHdr10Mode(current, modes));
        Assert.Equal(0, DisplayHdrPolicy.PickHdr10Mode(current, [Mode(1920, 1080, 50), Mode(3840, 2160, 59.94)]));
    }

    [Fact]
    public void SelectionFailsRatherThanShrinkingOrUsingStereoOrSdr()
    {
        var current = Mode(1920, 1080, 60);
        Assert.Null(DisplayHdrPolicy.PickHdr10Mode(current, []));
        Assert.Null(DisplayHdrPolicy.PickHdr10Mode(current,
        [
            Mode(1280, 720, 60), Mode(2560, 720, 60), Mode(1920, 1080, 60, pq: false),
            Mode(1920, 1080, 60, stereo: true),
        ]));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void SelectionRejectsInvalidRefreshRates(double refresh)
    {
        Assert.Null(DisplayHdrPolicy.PickHdr10Mode(Mode(1920, 1080, refresh), [Mode(1920, 1080, 60)]));
        Assert.Null(DisplayHdrPolicy.PickHdr10Mode(Mode(1920, 1080, 60), [Mode(1920, 1080, refresh)]));
        Assert.Null(DisplayHdrPolicy.PickHdr10Mode(Mode(0, 1080, 60), [Mode(1920, 1080, 60)]));
    }

    [Theory]
    [InlineData("original_http", "hdr10", null, true)]
    [InlineData("progressive", "HDR10", null, true)]
    [InlineData("server_remux_progressive", "hdr10", null, true)]
    [InlineData("original_http", "hdr10", "sdr", false)]
    [InlineData("progressive", "sdr", "hdr10", true)]
    [InlineData("server_transcode_hls", "hdr10", "sdr", false)]
    [InlineData("server_transcode_hls", "hdr10", null, false)]
    [InlineData("server_remux_hls", "hdr10", null, false)]
    [InlineData("original_http", "sdr", null, false)]
    [InlineData("original_http", "dolby_vision", null, false)]
    [InlineData("original_http", "hlg", null, false)]
    [InlineData("original_http", null, null, false)]
    [InlineData(null, "hdr10", null, false)]
    public void OnlyHdr10DirectPicturesRequestHdrOutput(string? delivery, string? sourceRange, string? outputRange, bool expected)
    {
        var plan = new PlaybackPlan
        {
            Delivery = delivery,
            Source = new PlaybackSource { DynamicRange = sourceRange },
            EffectiveRecipe = new EffectiveRecipeV3 { DynamicRange = outputRange },
        };
        Assert.Equal(expected, DisplayHdrPolicy.RequiresHdr10(plan));
        plan.Stream = new StreamV3 { Protocol = "hls" };
        Assert.False(DisplayHdrPolicy.RequiresHdr10(plan));
    }

    [Theory]
    [InlineData("hdr10_plus")]
    [InlineData("hdr_unknown")]
    public void ServerValidatedHdr10FallbackAlsoSwitchesOutput(string sourceRange)
    {
        var plan = new PlaybackPlan { Delivery = "original_http", Source = new PlaybackSource { DynamicRange = sourceRange } };
        Assert.False(DisplayHdrPolicy.RequiresHdr10(plan));
        plan.Claims = new ValidationClaimsV3 { Video = new VideoClaimsV3 { Hdr10 = true } };
        Assert.True(DisplayHdrPolicy.RequiresHdr10(plan));
    }

    private static DisplayModeFacts Mode(uint width, uint height, double refresh, bool pq = true, bool stereo = false, bool metadata = false) =>
        new(pq, metadata, stereo) { Width = width, Height = height, RefreshRate = refresh };

    private static void AssertNoHdr(HdrCapabilities hdr)
    {
        Assert.False(hdr.Hdr10);
        Assert.False(hdr.Hdr10Plus);
        Assert.False(hdr.Hlg);
        Assert.Empty(hdr.DolbyVisionProfiles);
    }
}
