using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

/// <summary>
/// How the Xbox player page uses the seek watchdog (PlayerPage.SeekWatchdog.cs). The page compiles only on Windows, so
/// its wiring is pinned by source here, and the debug switches are compiled and exercised directly.
/// </summary>
[Collection(nameof(DebugSeekWatchdog))]
public class PlayerSeekWatchdogWiringTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    [Fact]
    public void OnlyALocalSeekThatMovesTheClockIsWatchedAndTheTickChecksIt()
    {
        var seek = Read("PlayerPage.Seek.cs.txt");
        var local = seek[seek.IndexOf("if (local && Math.Abs(session.Position.TotalSeconds - target) > 0.000001)", StringComparison.Ordinal)..];
        var watched = local.IndexOf("if (BeginWatchedSeek(attempt, media, target, request.Seconds, paused))\n                        session.Position = TimeSpan.FromSeconds(target);", StringComparison.Ordinal);
        Assert.True(watched > 0, "the watch begins before the clock moves, inside the local branch");
        Assert.Equal(1, seek.Split("BeginWatchedSeek(").Length - 1);
        Assert.Equal(1, seek.Split("session.Position = TimeSpan.FromSeconds(").Length - 1);

        var player = Read("PlayerPage.cs.txt");
        Assert.Contains("UpdateStartBudget();\n        UpdateSeekWatchdog();", player);
        Assert.Contains("StopWatchingSeek();\n            Cleanup(\"dispose seek watchdog\", () => attempt.SeekWatchdog?.Dispose());", player);
    }

    [Fact]
    public void TheCheckReadsOnlyTheClockAndMountFieldsAndActsOnce()
    {
        var dog = Read("PlayerPage.SeekWatchdog.cs.txt");
        var update = dog[dog.IndexOf("private void UpdateSeekWatchdog()", StringComparison.Ordinal)..dog.IndexOf("private void StopWatchingSeek()", StringComparison.Ordinal)];
        Assert.Contains("var ended = !IsCurrent(attempt, media) || media.FailureQueued;", update);
        Assert.Contains("Completed = watched.Completed,", update);
        Assert.Contains("Offline = ConnectionSpeedService.DeviceOffline,", update);
        Assert.Contains("Deferred = _lifecycle.CurrentCount == 0,", update);
        Assert.Contains("clock = watched.Session.Position.TotalSeconds;", update);
        // No FFmpeg source on this path at all, so no locking wrapper member can sneak in.
        Assert.DoesNotContain("FfmpegSource", update);
        Assert.DoesNotContain("FfmpegStreams", update);
        Assert.Contains("_ = RemountStuckSeekAsync(attempt, media, watched.SourceTarget,", update);
        var target = update.IndexOf("attempt.StuckSeekTarget = watched.SourceTarget;", StringComparison.Ordinal);
        var fail = update.IndexOf("QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: false, PlaybackSeekWatchdog.Failure(media.Route.UseFfmpeg));", StringComparison.Ordinal);
        Assert.True(target > 0 && fail > target, "the failure replan starts at the target");
        // The completion handler is attached before the debug hang can swallow the seek.
        var begin = dog[dog.IndexOf("private bool BeginWatchedSeek(", StringComparison.Ordinal)..];
        var subscribe = begin.IndexOf("session.SeekCompleted += watched.OnSeekCompleted;", StringComparison.Ordinal);
        var hang = begin.IndexOf("DebugSeekWatchdog.TakeHang()", StringComparison.Ordinal);
        Assert.True(subscribe > 0 && hang > subscribe);
    }

    [Fact]
    public void TheRemountKeepsThePlanAndAFailedOpenTakesTheSingleReplan()
    {
        var dog = Read("PlayerPage.SeekWatchdog.cs.txt");
        var remount = dog[dog.IndexOf("private async Task RemountStuckSeekAsync(", StringComparison.Ordinal)..];
        Assert.Contains("await _lifecycle.WaitAsync();", remount);
        Assert.Contains("if (!IsCurrent(attempt, media) || media.FailureQueued) return;", remount);
        Assert.Contains("timeline.PlayerForSource(source) ?? timeline.PlayerStartSeconds), paused);", remount);
        Assert.Contains("await RecoverCoreAsync(attempt, ex.HResult, isDecoder: false, IsNetworkFailure(ex));", remount);
        Assert.DoesNotContain("ReplanAsync", remount);
        Assert.DoesNotContain("attempt.Plan =", remount);

        var player = Read("PlayerPage.cs.txt");
        Assert.Contains("var keepsClaims = PlaybackStartBudget.Is(failure) || PlaybackSeekWatchdog.Is(failure);", player);
        Assert.Contains("if (PlaybackSeekWatchdog.Is(failure) && attempt.StuckSeekTarget is { } stuckTarget)", player);
        Assert.Contains("attempt.Rav1gRemounted, hdrOutputFailed, PlaybackEmbeddedSubtitleFailure.Is(failure), keepsClaims, failedMount.Route.VideoHeight))", player);
        Assert.Contains("if (failedDolbyVision7 && !keepsClaims && DolbyVision7Policy.RequiresBaseLayer(attempt.Plan))", player);
        Assert.Contains("if (failedRoute?.ConsoleToneMap == true && !keepsClaims && ConsoleToneMapPolicy.RequiresToneMap(", player);
    }

    [Fact]
    public void TheDebugSwitchesAreValidatedAndTheHangIsOneShot()
    {
        try
        {
            Assert.Equal(PlaybackSeekWatchdog.BudgetMs, DebugSeekWatchdog.BudgetMs);
            Assert.Contains("seek-watchdog=15s (default) hang=none", DebugSeekWatchdog.Describe());
            Assert.True(DebugSeekWatchdog.TrySetBudget("5"));
            Assert.Equal(5_000, DebugSeekWatchdog.BudgetMs);
            Assert.False(DebugSeekWatchdog.TrySetBudget("0"));
            Assert.False(DebugSeekWatchdog.TrySetBudget("121"));
            Assert.False(DebugSeekWatchdog.TrySetBudget("-1"));
            Assert.False(DebugSeekWatchdog.TrySetBudget(null));
            Assert.Equal(5_000, DebugSeekWatchdog.BudgetMs);

            Assert.False(DebugSeekWatchdog.TrySetHang("always"));
            Assert.False(DebugSeekWatchdog.TakeHang());
            Assert.True(DebugSeekWatchdog.TrySetHang("once"));
            Assert.Contains("seek-watchdog=5s hang=once", DebugSeekWatchdog.Describe());
            Assert.True(DebugSeekWatchdog.TakeHang());
            Assert.False(DebugSeekWatchdog.TakeHang());
            Assert.True(DebugSeekWatchdog.TrySetHang("once"));
            Assert.True(DebugSeekWatchdog.TrySetHang("none"));
            Assert.False(DebugSeekWatchdog.TakeHang());
        }
        finally
        {
            DebugSeekWatchdog.TrySetBudget("default");
            DebugSeekWatchdog.TrySetHang("none");
        }
        Assert.Contains("seek-watchdog=15s (default) hang=none", DebugSeekWatchdog.Describe());
    }

    [Fact]
    public void TheDebugRouteAndScriptMatch()
    {
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/seek-watchdog\"):", server);
        Assert.Contains("case (\"POST\", \"/seek-watchdog\"):", server);
        Assert.Contains("\"/start-budget\" or \"/seek-watchdog\" or", server);
        Assert.Contains("GET /seek-watchdog, POST /seek-watchdog?seconds={1..120|default}&hang={none|once}", server);
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bale-debug.sh"));
        Assert.Contains("seek-watchdog)", script);
        Assert.Contains("seconds=([0-9]+|default)|hang=(none|once)", script);
    }
}

[CollectionDefinition(nameof(DebugSeekWatchdog), DisableParallelization = true)]
public sealed class DebugSeekWatchdogCollection;
