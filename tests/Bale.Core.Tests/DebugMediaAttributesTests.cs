using Bale.Xbox.Services;
using Xunit;

namespace Bale.Core.Tests;

public sealed class DebugMediaAttributesTests
{
    [Fact]
    public void NamesKnownAttributesAndFormatsPackedSubtypeAndBlobValues()
    {
        var properties = new Dictionary<Guid, object>
        {
            [new Guid("1652c33d-d6b2-4012-b834-72030849a37d")] = (1920UL << 32) | 1080UL, // FRAME_SIZE
            [new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5")] = new Guid("43564548-0000-0010-8000-00aa00389b71"), // SUBTYPE
            [new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036")] = 2u, // VIDEO_PROFILE (alias MPEG2_PROFILE)
            [new Guid("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3")] = new byte[] { 0, 0, 1, 0x40 }, // MPEG_SEQUENCE_HEADER
            [new Guid("01234567-89ab-cdef-0123-456789abcdef")] = true,
        };
        var text = DebugMediaAttributes.Describe(properties);
        Assert.StartsWith("attrs(5) ", text);
        Assert.Contains("FRAME_SIZE=1920:1080", text);
        Assert.Contains("SUBTYPE=HEVC", text);
        Assert.Contains("VIDEO_PROFILE=2", text);
        Assert.Contains("MPEG_SEQUENCE_HEADER=blob[4]00000140", text);
        Assert.Contains("01234567-89ab-cdef-0123-456789abcdef=true", text);
        Assert.Equal("attrs=unavailable", DebugMediaAttributes.Describe(null));
    }
}
