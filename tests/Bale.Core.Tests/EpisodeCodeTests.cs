using Bale.Core.Detail;

namespace Bale.Core.Tests;

public sealed class EpisodeCodeTests
{
    [Theory]
    [InlineData(1L, 1L, "S01E01")]
    [InlineData(0L, 3L, "S00E03")]
    [InlineData(12L, 104L, "S12E104")]
    [InlineData(null, 2L, "E02")]
    public void OneSpellingEverywhere(long? season, long episode, string expected) =>
        Assert.Equal(expected, EpisodeCode.Format(season, episode));
}
