using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugFrameDeliverySwitchTests
{
    [Fact]
    public void SwitchDefaultsToNativeAndPublishesOnlyValidatedMemoryOnlyValues()
    {
        var previous = DebugFrameDeliverySwitch.Mode;
        try
        {
            Assert.Equal("native", previous);
            foreach (var mode in new[] { "off", "managed", "native", "on" })
            {
                Assert.True(DebugFrameDeliverySwitch.TrySet(mode));
                Assert.Equal(mode == "on" ? "native" : mode, DebugFrameDeliverySwitch.Mode);
            }
            foreach (var invalid in new string?[] { null, "", "status", "true", "ON", "Native", "MANAGED", " on", "off ", "on&extra=1" })
            {
                Assert.False(DebugFrameDeliverySwitch.TrySet(invalid));
                Assert.Equal("native", DebugFrameDeliverySwitch.Mode);
            }
        }
        finally { DebugFrameDeliverySwitch.TrySet(previous); }
        var source = Read("DebugFrameDeliverySwitch.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("private static string _mode = \"native\";", source);
        Assert.DoesNotContain("Settings", source);
        Assert.DoesNotContain("Storage", source);
    }

    [Fact]
    public void PlayerCapturesTheModePerAttemptAndSkipsAttachingWhenOff()
    {
        var player = Read("PlayerPage.cs.txt");
        var capture = player.IndexOf("public string FrameDeliveryMode { get; } = DebugFrameDeliverySwitch.Mode;", StringComparison.Ordinal);
        Assert.True(capture >= 0);
        Assert.True(player.LastIndexOf("#if DEBUG_SERVER", capture, StringComparison.Ordinal)
            > player.LastIndexOf("#endif", capture, StringComparison.Ordinal));
        var media = Read("PlayerPage.Media.cs.txt");
        var guard = media.IndexOf("if (attempt.FrameDeliveryMode != \"off\")", StringComparison.Ordinal);
        var observer = media.IndexOf("media.FrameDelivery = attempt.FrameDeliveryMode == \"managed\"", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < observer);
        Assert.True(media.LastIndexOf("#if DEBUG_SERVER", guard, StringComparison.Ordinal)
            > media.LastIndexOf("#endif", guard, StringComparison.Ordinal));
        Assert.Contains("Log.Info($\"player: frame delivery observer={attempt.FrameDeliveryMode}\");", media);
        Assert.Contains("if (media.Route.SoftwareVideo && media.FfmpegSource is { } softwareSource)", media[guard..observer]);
        Assert.Contains("? new DebugFrameDelivery(softwareSource,", media[observer..]);
        Assert.Contains(": new NativeFrameDelivery(softwareSource,", media[observer..]);
        Assert.Contains("public IDebugFrameDelivery? FrameDelivery", media);
        Assert.DoesNotContain("DebugFrameDeliverySwitch", media);
        var probe = Read("DebugServer.Ffmpeg.cs.txt");
        Assert.DoesNotContain("DebugFrameDeliverySwitch", probe);
        Assert.Contains("new DebugFrameDelivery(source,", probe);
        Assert.DoesNotContain("NativeFrameDelivery", probe);
    }

    [Fact]
    public void RoutesRejectUnexpectedAndDuplicateQueryKeysAndDocumentBothMethods()
    {
        var routes = Read("DebugServer.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", routes);
        var duplicateGuard = routes.Split('\n').Single(line => line.Contains("duplicateQueryKeys && path is", StringComparison.Ordinal));
        Assert.Contains("\"/frame-delivery\"", duplicateGuard);
        var get = routes[routes.IndexOf("case (\"GET\", \"/frame-delivery\")", StringComparison.Ordinal)
            ..routes.IndexOf("case (\"POST\", \"/frame-delivery\")", StringComparison.Ordinal)];
        Assert.Contains("if (query.Count != 0)", get);
        Assert.Contains("\"400 Bad Request\"", get);
        Assert.Contains("frame-delivery={DebugFrameDeliverySwitch.Mode}\\n", get);
        Assert.DoesNotContain("TrySet", get);
        var post = routes[routes.IndexOf("case (\"POST\", \"/frame-delivery\")", StringComparison.Ordinal)
            ..routes.IndexOf("case (\"GET\", \"/display-match\")", StringComparison.Ordinal)];
        Assert.Contains("query.Count != 1 || !DebugFrameDeliverySwitch.TrySet(query.GetValueOrDefault(\"mode\"))", post);
        Assert.Contains("\"400 Bad Request\"", post);
        Assert.Contains("frame-delivery={DebugFrameDeliverySwitch.Mode}\\n", post);
        Assert.Contains("applies to the next playback attempt", post);
        Assert.Contains("///   GET  /frame-delivery", routes);
        Assert.Contains("///   POST /frame-delivery?mode=native", routes);
        Assert.Contains("on aliases native", routes);
        var notFound = routes.Split('\n').Single(line => line.Contains("\"routes: ", StringComparison.Ordinal));
        Assert.Contains("GET /frame-delivery, POST /frame-delivery?mode={native|managed|off}", notFound);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
