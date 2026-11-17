using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public sealed class DebugPlayerBackendSwitchTests
{
    [Fact]
    public void DefaultsToTheEngineAndTakesOnlyItsTwoModes()
    {
        var previous = DebugPlayerBackendSwitch.Mode;
        try
        {
            Assert.Equal("engine", previous);
            Assert.True(DebugPlayerBackendSwitch.TrySet("mediaplayer"));
            Assert.Equal("mediaplayer", DebugPlayerBackendSwitch.Mode);
            foreach (var invalid in new string?[] { null, "", "Engine", "native", " engine", "engine&x=1" })
                Assert.False(DebugPlayerBackendSwitch.TrySet(invalid));
            Assert.Equal("mediaplayer", DebugPlayerBackendSwitch.Mode);
        }
        finally
        {
            DebugPlayerBackendSwitch.TrySet(previous);
        }
    }
}
