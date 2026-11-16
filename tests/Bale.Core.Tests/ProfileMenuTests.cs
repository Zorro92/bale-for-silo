using Bale.Core.Profiles;

namespace Bale.Core.Tests;

/// <summary>ProfileMenuRowTest.kt: the profile row by profile count and max_profiles.</summary>
public class ProfileMenuTests
{
    [Theory]
    [InlineData(0, null, ProfileMenuRow.SwitchProfile)]
    [InlineData(2, 1L, ProfileMenuRow.SwitchProfile)]
    [InlineData(5, 5L, ProfileMenuRow.SwitchProfile)]
    [InlineData(1, null, ProfileMenuRow.AddProfile)]
    [InlineData(1, 6L, ProfileMenuRow.AddProfile)]
    [InlineData(1, 1L, ProfileMenuRow.None)]
    [InlineData(1, 0L, ProfileMenuRow.None)]
    public void TheRowFollowsTheProfileCountAndLimit(int count, long? max, ProfileMenuRow expected) =>
        Assert.Equal(expected, ProfileMenu.Row(count, max));
}
