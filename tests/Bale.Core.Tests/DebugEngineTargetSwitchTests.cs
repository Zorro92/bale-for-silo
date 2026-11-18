using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public sealed class DebugEngineTargetSwitchTests
{
    [Fact]
    public void DefaultsToReleaseBgraAndMapsTheAbiCodes()
    {
        var previous = DebugEngineTargetSwitch.Mode;
        try
        {
            Assert.True(DebugEngineTargetSwitch.TrySet("bgra"));
            Assert.Equal(0u, DebugEngineTargetSwitch.Code);
            Assert.True(DebugEngineTargetSwitch.TrySet("fp16"));
            Assert.Equal(1u, DebugEngineTargetSwitch.Code);
            Assert.True(DebugEngineTargetSwitch.TrySet("rgb10"));
            Assert.Equal(2u, DebugEngineTargetSwitch.Code);
            Assert.False(DebugEngineTargetSwitch.TrySet("nv12"));
            Assert.False(DebugEngineTargetSwitch.TrySet(null));
            Assert.Equal("rgb10", DebugEngineTargetSwitch.Mode);
        }
        finally { DebugEngineTargetSwitch.TrySet(previous); }
    }
}
