using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public sealed class DebugPlayerBackendSwitchTests
{
    [Fact]
    public void DefaultsToMediaPlayerAndTakesOnlyItsTwoModes()
    {
        var previous = DebugPlayerBackendSwitch.Mode;
        try
        {
            Assert.Equal("mediaplayer", previous);
            Assert.True(DebugPlayerBackendSwitch.TrySet("engine"));
            Assert.Equal("engine", DebugPlayerBackendSwitch.Mode);
            foreach (var invalid in new string?[] { null, "", "Engine", "native", " engine", "engine&x=1" })
                Assert.False(DebugPlayerBackendSwitch.TrySet(invalid));
            Assert.Equal("engine", DebugPlayerBackendSwitch.Mode);
        }
        finally
        {
            DebugPlayerBackendSwitch.TrySet(previous);
        }
    }
}
