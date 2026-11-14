using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugStreamAdapterSwitchTests
{
    [Fact]
    public void SwitchDefaultsToPooledAndPublishesOnlyValidatedMemoryOnlyValues()
    {
        var previous = DebugStreamAdapterSwitch.Mode;
        try
        {
            Assert.True(DebugStreamAdapterSwitch.Pooled);
            foreach (var mode in new[] { "builtin", "pooled" })
            {
                Assert.True(DebugStreamAdapterSwitch.TrySet(mode));
                Assert.Equal(mode, DebugStreamAdapterSwitch.Mode);
                Assert.Equal(mode == "pooled", DebugStreamAdapterSwitch.Pooled);
            }
            foreach (var invalid in new string?[] { null, "", "status", "on", "POOLED", " pooled", "builtin ", "pooled&extra=1" })
            {
                Assert.False(DebugStreamAdapterSwitch.TrySet(invalid));
                Assert.Equal("pooled", DebugStreamAdapterSwitch.Mode);
            }
        }
        finally { DebugStreamAdapterSwitch.TrySet(previous); }
        var source = Read("DebugStreamAdapterSwitch.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("private static int _pooled = 1;", source);
        Assert.DoesNotContain("Settings", source);
        Assert.DoesNotContain("Storage", source);
    }

    [Fact]
    public void PlayerCapturesTheDebugSwitchPerAttemptAndUsesPooledOnlyForFfmpeg()
    {
        var player = Read("PlayerPage.cs.txt");
        var capture = player.IndexOf("public bool UsePooledStreamAdapter { get; } = DebugStreamAdapterSwitch.Pooled;", StringComparison.Ordinal);
        Assert.True(capture >= 0);
        Assert.True(player.LastIndexOf("#if DEBUG_SERVER", capture, StringComparison.Ordinal)
            > player.LastIndexOf("#endif", capture, StringComparison.Ordinal));
        Assert.Contains("#else\n        public bool UsePooledStreamAdapter => true;", player);
        Assert.Contains("public bool StreamAdapterLogged { get; set; }", player);
        var media = Read("PlayerPage.Media.cs.txt");
        var branch = media.IndexOf("if (media.Route.UseFfmpeg)", StringComparison.Ordinal);
        var pooled = media.IndexOf("new RangeRandomAccessStream(range)", StringComparison.Ordinal);
        var create = media.IndexOf("FFmpegMediaSource.CreateFromStreamAsync", StringComparison.Ordinal);
        Assert.True(branch >= 0 && branch < pooled && pooled < create);
        Assert.Contains("media.RandomStream = attempt.UsePooledStreamAdapter", media[branch..create]);
        Assert.Contains("if (!attempt.StreamAdapterLogged)", media[branch..create]);
        Assert.Contains("player: stream adapter={(attempt.UsePooledStreamAdapter ? \"pooled\" : \"builtin\")}", media[branch..create]);
        Assert.Contains("attempt.StreamAdapterLogged = true;", media[branch..create]);
        Assert.Contains("media.RandomStream = media.Stream!.AsRandomAccessStream();\n                media.Source = MediaSource.CreateFromStream", media);
        Assert.DoesNotContain("DebugStreamAdapterSwitch", media);
        Assert.DoesNotContain("RangeRandomAccessStream", Read("DebugServer.Ffmpeg.cs.txt"));
    }

    [Fact]
    public void RoutesRejectUnexpectedAndDuplicateQueryKeysAndDocumentBothMethods()
    {
        var routes = Read("DebugServer.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", routes);
        var duplicateGuard = routes.Split('\n').Single(line => line.Contains("duplicateQueryKeys && path is", StringComparison.Ordinal));
        Assert.Contains("\"/stream-adapter\"", duplicateGuard);
        var get = routes[routes.IndexOf("case (\"GET\", \"/stream-adapter\")", StringComparison.Ordinal)
            ..routes.IndexOf("case (\"POST\", \"/stream-adapter\")", StringComparison.Ordinal)];
        Assert.Contains("if (query.Count != 0)", get);
        Assert.Contains("\"400 Bad Request\"", get);
        Assert.Contains("stream-adapter={DebugStreamAdapterSwitch.Mode}\\n", get);
        Assert.DoesNotContain("TrySet", get);
        var post = routes[routes.IndexOf("case (\"POST\", \"/stream-adapter\")", StringComparison.Ordinal)
            ..routes.IndexOf("case (\"GET\", \"/display-match\")", StringComparison.Ordinal)];
        Assert.Contains("query.Count != 1 || !DebugStreamAdapterSwitch.TrySet(query.GetValueOrDefault(\"mode\"))", post);
        Assert.Contains("\"400 Bad Request\"", post);
        Assert.Contains("stream-adapter={DebugStreamAdapterSwitch.Mode}\\n", post);
        Assert.Contains("applies to the next playback attempt", post);
        Assert.Contains("///   GET  /stream-adapter", routes);
        Assert.Contains("///   POST /stream-adapter?mode=pooled", routes);
        var notFound = routes.Split('\n').Single(line => line.Contains("\"routes: ", StringComparison.Ordinal));
        Assert.Contains("GET /stream-adapter, POST /stream-adapter?mode={pooled|builtin}", notFound);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
