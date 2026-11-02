using Bale.Core.Auth;

namespace Bale.Core.Tests;

public class ProfileTilePaletteTests
{
    // MarqueeProfileAvatar.kt ProfileTilePalette.tint: known DJB2/UTF-8 mappings,
    // covering every palette slot rather than depending on runtime string hashing.
    [Theory]
    [InlineData("p1", 0xFF5C949Eu)]
    [InlineData("p2", 0xFFC7A361u)]
    [InlineData("p3", 0xFFD97561u)]
    [InlineData("p4", 0xFF668FB8u)]
    [InlineData("p5", 0xFFB08A66u)]
    [InlineData("p6", 0xFF759E85u)]
    [InlineData("p7", 0xFFC77A85u)]
    [InlineData("p8", 0xFF8F7AB8u)]
    public void KnownIdsMatchTheOfficialPalette(string profileId, uint expected) =>
        Assert.Equal(expected, ProfileTilePalette.Tint(profileId));

    [Theory]
    [InlineData("", 0xFF8F7AB8u)]
    [InlineData("niño", 0xFFC7A361u)]
    [InlineData("\U0001F467", 0xFFC77A85u)]
    [InlineData("4b921e0e-96ef-47df-914b-544394ff7c2e", 0xFF5C949Eu)]
    public void HashUsesUnsignedUtf8BytesAndWrapsAt64Bits(string profileId, uint expected) =>
        Assert.Equal(expected, ProfileTilePalette.Tint(profileId));
}
