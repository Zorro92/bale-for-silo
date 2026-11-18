using System.Text;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

// silo-android TvDeviceNameTest.kt.
public class DeviceNamingTests
{
    [Fact]
    public void KeepsAShortNameAndTrimsIt() => Assert.Equal("Living Room TV", DeviceNaming.Fitted("  Living Room TV "));

    [Theory]
    [InlineData(129, "a")]
    [InlineData(84, "電")]
    [InlineData(40, "\U0001F4FA")]
    public void CutsALongNameOnACharacterBoundary(int count, string unit)
    {
        var fitted = DeviceNaming.Fitted(string.Concat(Enumerable.Repeat(unit, count)));
        Assert.True(Encoding.UTF8.GetByteCount(fitted) <= DeviceNaming.MaxUtf8Bytes);
        Assert.True(fitted.Length > 0);
        Assert.Equal(0, fitted.Length % unit.Length); // never half a surrogate pair
        Assert.Equal(fitted, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(fitted)));
    }

    [Fact]
    public void FallsBackToTheModelWithoutAName()
    {
        Assert.Equal("Den", DeviceNaming.Choose(" Den ", "Xbox Series X"));
        Assert.Equal("Xbox Series X", DeviceNaming.Choose("  ", "Xbox Series X"));
        Assert.Equal("Xbox", DeviceNaming.Choose(null, ""));
    }
}
