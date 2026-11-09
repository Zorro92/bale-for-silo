namespace Bale.Core.Tests;

/// <summary>The input-idle experiments stay debug-only and never touch the player's own DisplayRequest.</summary>
public class DebugIdleTestTests
{
    [Fact]
    public void RouteIsDebugOnlyAndValidatesItsAction()
    {
        var idle = Read("DebugServer.IdleTest.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", idle);
        Assert.Contains("action is \"display-pulse\" or \"smtc-playing\" or \"smtc-stopped\";", idle);
        Assert.Contains("_idlePulse.RequestActive();", idle);
        Assert.DoesNotContain("_display", idle.Replace("PlayerPage._display", string.Empty, StringComparison.Ordinal));

        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/idle-test\")", server);
        Assert.Contains("case (\"POST\", \"/idle-test\")", server);
        Assert.Contains("if (query.Count != 1 || !IsIdleTestAction(idleAction))", server);
        Assert.Contains("or \"/idle-test\" or", server);

        var player = Read("PlayerPage.cs.txt");
        var debugBlock = player[player.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal)..];
        Assert.Contains("internal MediaPlayer? DebugMediaPlayer => _attempt?.Media?.Player;", debugBlock);

        var script = Read("bale-debug.sh");
        Assert.Contains("^(display-pulse|smtc-playing|smtc-stopped|status)$", script);
        Assert.Contains("--data-urlencode \"action=$2\"", script);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
