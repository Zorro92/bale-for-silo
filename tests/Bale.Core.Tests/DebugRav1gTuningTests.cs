using Bale.Xbox.Services;

namespace Bale.Core.Tests;

[Collection("Display HDR")]
public sealed class DebugRav1gTuningTests
{
    [Fact]
    public void KnobsMapToTheForksKeysAndClearWithDefault()
    {
        foreach (var key in new[] { "inflight", "perf", "parse", "outtex" }) Assert.True(DebugRav1gTuning.TrySet(key, "default"));
        Assert.Empty(DebugRav1gTuning.Options());
        Assert.True(DebugRav1gTuning.TrySet("inflight", "4"));
        Assert.True(DebugRav1gTuning.TrySet("perf", "on"));
        Assert.True(DebugRav1gTuning.TrySet("parse", "2"));
        Assert.True(DebugRav1gTuning.TrySet("outtex", "16"));
        Assert.Equal(
            [new("bale_gpu_decode_inflight", "4"), new("bale_rav1g_perf", "1"), new("bale_rav1g_parse", "2"), new KeyValuePair<string, string>("bale_gpu_output_textures", "16")],
            DebugRav1gTuning.Options());
        Assert.Equal("rav1g-tune inflight=4 perf=on parse=2 outtex=16", DebugRav1gTuning.Describe());
        // Out of range or unknown: refused, nothing changes.
        Assert.False(DebugRav1gTuning.TrySet("inflight", "9"));
        Assert.False(DebugRav1gTuning.TrySet("inflight", "0"));
        Assert.False(DebugRav1gTuning.TrySet("inflight", "-1"));
        Assert.False(DebugRav1gTuning.TrySet("parse", "3"));
        Assert.False(DebugRav1gTuning.TrySet("perf", "yes"));
        Assert.False(DebugRav1gTuning.TrySet("slots", "3"));
        Assert.False(DebugRav1gTuning.TrySet("outtex", "65"));
        Assert.True(DebugRav1gTuning.TrySet("outtex", "0"));
        Assert.True(DebugRav1gTuning.TrySet("outtex", "16"));
        Assert.Equal("rav1g-tune inflight=4 perf=on parse=2 outtex=16", DebugRav1gTuning.Describe());
        foreach (var key in new[] { "inflight", "perf", "parse", "outtex" }) Assert.True(DebugRav1gTuning.TrySet(key, "default"));
        Assert.Equal("rav1g-tune inflight=default perf=default parse=default outtex=default", DebugRav1gTuning.Describe());
    }
}
