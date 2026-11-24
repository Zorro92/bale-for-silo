using Bale.Xbox.Services;

namespace Bale.Core.Tests;

[Collection("Display HDR")]
public sealed class DebugRav1gTuningTests
{
    [Fact]
    public void KnobsMapToTheForksKeysAndClearWithDefault()
    {
        foreach (var key in new[] { "inflight", "perf", "parse", "outtex", "images", "display" }) Assert.True(DebugRav1gTuning.TrySet(key, "default"));
        Assert.Empty(DebugRav1gTuning.Options());
        Assert.True(DebugRav1gTuning.TrySet("inflight", "4"));
        Assert.True(DebugRav1gTuning.TrySet("perf", "on"));
        Assert.True(DebugRav1gTuning.TrySet("parse", "2"));
        Assert.True(DebugRav1gTuning.TrySet("outtex", "16"));
        Assert.True(DebugRav1gTuning.TrySet("images", "14"));
        Assert.Equal(
            [new("bale_gpu_decode_inflight", "4"), new("bale_rav1g_perf", "1"), new("bale_rav1g_parse", "2"), new("bale_gpu_output_textures", "16"), new KeyValuePair<string, string>("bale_gpu_output_images", "14")],
            DebugRav1gTuning.Options());
        Assert.Equal("rav1g-tune inflight=4 perf=on parse=2 outtex=16 images=14 display=default", DebugRav1gTuning.Describe());
        // Out of range or unknown: refused, nothing changes.
        Assert.False(DebugRav1gTuning.TrySet("inflight", "9"));
        Assert.False(DebugRav1gTuning.TrySet("inflight", "0"));
        Assert.False(DebugRav1gTuning.TrySet("inflight", "-1"));
        Assert.False(DebugRav1gTuning.TrySet("parse", "3"));
        Assert.False(DebugRav1gTuning.TrySet("perf", "yes"));
        Assert.False(DebugRav1gTuning.TrySet("perf", "2"));
        Assert.False(DebugRav1gTuning.TrySet("slots", "3"));
        Assert.False(DebugRav1gTuning.TrySet("outtex", "65"));
        Assert.False(DebugRav1gTuning.TrySet("images", "17"));
        Assert.True(DebugRav1gTuning.TrySet("outtex", "0"));
        Assert.True(DebugRav1gTuning.TrySet("outtex", "16"));
        Assert.Equal("rav1g-tune inflight=4 perf=on parse=2 outtex=16 images=14 display=default", DebugRav1gTuning.Describe());
        foreach (var key in new[] { "inflight", "perf", "parse", "outtex", "images", "display" }) Assert.True(DebugRav1gTuning.TrySet(key, "default"));
        Assert.Equal("rav1g-tune inflight=default perf=default parse=default outtex=default images=default display=default", DebugRav1gTuning.Describe());
        // perf=frames (wrapper bale.72): one stage line per frame.
        Assert.True(DebugRav1gTuning.TrySet("perf", "frames"));
        Assert.Equal([new KeyValuePair<string, string>("bale_rav1g_perf", "2")], DebugRav1gTuning.Options());
        Assert.Equal("rav1g-tune inflight=default perf=frames parse=default outtex=default images=default display=default", DebugRav1gTuning.Describe());
        Assert.True(DebugRav1gTuning.TrySet("perf", "off"));
        Assert.Equal([new KeyValuePair<string, string>("bale_rav1g_perf", "0")], DebugRav1gTuning.Options());
        Assert.True(DebugRav1gTuning.TrySet("perf", "default"));
        // display (fork bale.74): off forces the stage, on asks for rav1g's display output.
        Assert.False(DebugRav1gTuning.TrySet("display", "1"));
        Assert.True(DebugRav1gTuning.TrySet("display", "off"));
        Assert.Equal([new KeyValuePair<string, string>("bale_rav1g_display", "0")], DebugRav1gTuning.Options());
        Assert.EndsWith(" display=off", DebugRav1gTuning.Describe());
        Assert.True(DebugRav1gTuning.TrySet("display", "on"));
        Assert.Equal([new KeyValuePair<string, string>("bale_rav1g_display", "1")], DebugRav1gTuning.Options());
        Assert.True(DebugRav1gTuning.TrySet("display", "default"));
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FfmpegSource.cs.txt"));
        Assert.Contains("config.FFmpegOptions[\"bale_rav1g_display\"] = \"1\";", source);
        // The debug endpoint must accept the key (it keeps its own key list).
        var server = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.cs.txt"));
        Assert.Contains("key is not (\"inflight\" or \"perf\" or \"parse\" or \"outtex\" or \"images\" or \"display\")", server);
    }
}
