using System.Buffers.Binary;
using System.Globalization;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public sealed class DebugVideoColourTests
{
    [Fact]
    public void AbsentAttributesAreUnsetAndExistingColourPrefixIsPreserved()
    {
        Assert.Equal("matrix=unset transfer=unset primaries=unset range=unset maxCLL=unset maxFALL=unset"
            + " minMasteringLuminance=unset maxMasteringLuminance=unset customPrimaries=unset",
            DebugVideoColour.Describe(new Dictionary<Guid, object>()));
    }

    [Fact]
    public void Hdr10AttributesAndCustomPrimariesAreInvariantAndUseTheSdkKeys()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            float[] primaries = [0.68f, 0.32f, 0.265f, 0.69f, 0.15f, 0.06f, 0.3127f, 0.329f];
            var bytes = new byte[32];
            for (var i = 0; i < primaries.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), primaries[i]);
            var properties = new Dictionary<Guid, object>
            {
                [new("3e23d450-2c75-4d25-a00e-b91670d12327")] = 4u,
                [new("5fb0fce9-be5c-4935-a811-ec838f8eed93")] = 15u,
                [new("dbfbe4d7-0740-4ee0-8192-850ab0e21935")] = 9u,
                [new("c21b8ee5-b956-4071-8daf-325edf5cab11")] = 2u,
                [new("50253128-c110-4de4-98ae-46a324fae6da")] = 1000u,
                [new("58d4bf57-6f52-4733-a195-a9e29ecf9e27")] = 400u,
                [new("839a4460-4e7e-4b4f-ae79-cc08905c7b27")] = 50u,
                [new("d6c6b997-272f-4ca1-8d00-8042111a0ff6")] = 1000u,
                [new("47537213-8cfb-4722-aa34-fbc9e24d77b8")] = bytes,
            };
            Assert.Equal("matrix=4(BT2020_10) transfer=15(2084/PQ) primaries=9(BT2020) range=2(limited)"
                + " maxCLL=1000 maxFALL=400 minMasteringLuminance=50 maxMasteringLuminance=1000"
                + " customPrimaries=[0.68,0.32,0.265,0.69,0.15,0.06,0.3127,0.329]",
                DebugVideoColour.Describe(properties));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void UnexpectedPrimariesBlobSizesStillReportPresence(int size)
    {
        Assert.EndsWith(" customPrimaries=present", DebugVideoColour.Describe(new Dictionary<Guid, object>
        {
            [new("47537213-8cfb-4722-aa34-fbc9e24d77b8")] = new byte[size],
        }));
    }

    [Fact]
    public void UnknownAndMalformedValuesCannotEchoArbitraryMetadata()
    {
        var properties = new Dictionary<Guid, object>
        {
            [new("3e23d450-2c75-4d25-a00e-b91670d12327")] = 123u,
            [new("5fb0fce9-be5c-4935-a811-ec838f8eed93")] = "private-metadata",
            [new("50253128-c110-4de4-98ae-46a324fae6da")] = -1,
            [new("58d4bf57-6f52-4733-a195-a9e29ecf9e27")] = 0u,
            [new("47537213-8cfb-4722-aa34-fbc9e24d77b8")] = "private-metadata",
        };
        var description = DebugVideoColour.Describe(properties);
        Assert.StartsWith("matrix=123 transfer=? primaries=unset range=unset maxCLL=? maxFALL=0 ", description);
        Assert.EndsWith(" customPrimaries=present", description);
        Assert.DoesNotContain("private-metadata", description);
    }
}
