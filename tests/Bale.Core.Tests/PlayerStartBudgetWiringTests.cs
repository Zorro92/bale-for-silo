using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

/// <summary>
/// How the Xbox player page uses the start budget (PlayerPage.StartBudget.cs). The page compiles only on Windows, so
/// its wiring is pinned by source here, and the debug switches are compiled and exercised directly.
/// </summary>
[Collection(nameof(DebugStartBudget))]
public class PlayerStartBudgetWiringTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    [Fact]
    public void EveryMountGetsABudgetAndTheTickSpendsIt()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        var player = Read("PlayerPage.cs.txt");
        var budget = Read("PlayerPage.StartBudget.cs.txt");
        Assert.Contains("StartBudget = NewStartBudget(),", media);
        Assert.Contains("try { await OpenMountAsync(attempt, media, transport, playerStart, paused, rangeRefused); }\n        finally { media.Mounting = false; }", media);
        Assert.Contains("Cleanup(\"dispose start budget\", () => media.StartBudget?.Dispose());", media);
        Assert.Contains("UpdateBufferingQuality();\n        UpdateStartBudget();", player);
        // Ready, ended, offline and deferral facts; a mount's own open never defers it.
        Assert.Contains("Started = media.Started,", budget);
        Assert.Contains("Ended = media.FailureQueued,", budget);
        Assert.Contains("Offline = ConnectionSpeedService.DeviceOffline,", budget);
        Assert.Contains("Deferred = _lifecycle.CurrentCount == 0 && !media.Mounting,", budget);
    }

    [Fact]
    public void EveryTransportOpenCanBeAbandonedAndTheEngineNeverFallsBackAfterTheBudget()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.DoesNotContain("await transport.Open", media);
        Assert.Contains("await OpenStepAsync(transport.OpenNativeStreamAsync(media.Token), media.Token)", media);
        Assert.Contains("await OpenStepAsync(transport.OpenNativeHlsAsync(playerStart, media.Token), media.Token)", media);
        Assert.Contains("await OpenStepAsync(transport.OpenNativeProgressiveAsync(media.Token), media.Token)", media);
        var engine = media.IndexOf("if (await MountEngineAsync(attempt, media, transport.MimeType ?? \"video/mp4\")) return;", StringComparison.Ordinal);
        var fallback = media.IndexOf("Log.Info(\"player: engine could not open this stream; remounting on MediaPlayer\");", StringComparison.Ordinal);
        Assert.True(engine > 0 && fallback > engine);
        Assert.Contains("media.Token.ThrowIfCancellationRequested();", media[engine..fallback]);
    }

    [Fact]
    public void TheControllerIsAskedFirstAndOtherwiseTheSingleReplanRuns()
    {
        var budget = Read("PlayerPage.StartBudget.cs.txt");
        var ranOut = budget[budget.IndexOf("private void StartBudgetRanOut(", StringComparison.Ordinal)..];
        var held = ranOut.IndexOf("attempt.HeldStartDeadline = media;", StringComparison.Ordinal);
        var asked = ranOut.IndexOf("_bufferingController?.StartDeadlinePassed() == true", StringComparison.Ordinal);
        var queued = ranOut.IndexOf("QueueFailure(attempt, media, 0, isDecoder: false, isNetwork: false, PlaybackStartBudget.Failure);", StringComparison.Ordinal);
        Assert.True(held > 0 && asked > held && queued > asked, "held, then asked, then the failure");
        Assert.Contains("if (media.Mounting) InterruptOpening(media);", ranOut);

        var player = Read("PlayerPage.cs.txt");
        Assert.Contains("if (attempt.Media is { StartBudgetRanOut: true } && failure is null && !hdrOutputFailed) failure = PlaybackStartBudget.Failure;", player);
        Assert.Contains("if (StartDeadlineLoweringOwns(attempt))", player);
        Assert.Contains("&& !PlaybackEmbeddedSubtitleFailure.Is(failure) && !keepsClaims)", player);
    }

    [Fact]
    public void TheLoweringWaitsForTheOpenAndHandsBackARefusal()
    {
        var quality = Read("PlayerPage.BufferingQuality.cs.txt");
        Assert.Contains("if (restore || heldAttempt is not null) await _lifecycle.WaitAsync();", quality);
        Assert.Equal(3, quality.Split("await RecoverHeldStartDeadlineAsync(heldAttempt);").Length - 1);
        Assert.Contains("adopted = true;\n            // The lowering is adopted: the mount that ran out of its start budget is gone with its held failure.\n            attempt.HeldStartDeadline = null;", quality);
    }

    [Fact]
    public void TheDebugSwitchesAreValidatedAndTheHangIsOneShot()
    {
        try
        {
            Assert.Equal(PlaybackStartBudget.BudgetMs, DebugStartBudget.BudgetMs);
            Assert.True(DebugStartBudget.TrySetBudget("5"));
            Assert.Equal(5_000, DebugStartBudget.BudgetMs);
            Assert.False(DebugStartBudget.TrySetBudget("0"));
            Assert.False(DebugStartBudget.TrySetBudget("121"));
            Assert.False(DebugStartBudget.TrySetBudget("-1"));
            Assert.False(DebugStartBudget.TrySetBudget(null));
            Assert.Equal(5_000, DebugStartBudget.BudgetMs);
            Assert.Contains("start-budget=5s hang=none", DebugStartBudget.Describe());

            Assert.False(DebugStartBudget.TrySetHang("forever"));
            Assert.True(DebugStartBudget.TrySetHang("open"));
            Assert.Equal("open", DebugStartBudget.TakeHang());
            Assert.Null(DebugStartBudget.TakeHang());
            Assert.True(DebugStartBudget.TrySetHang("ready"));
            Assert.True(DebugStartBudget.TrySetHang("none"));
            Assert.Null(DebugStartBudget.TakeHang());
        }
        finally
        {
            DebugStartBudget.TrySetBudget("default");
            DebugStartBudget.TrySetHang("none");
        }
        Assert.Contains("start-budget=45s (default) hang=none", DebugStartBudget.Describe());
    }

    [Fact]
    public async Task TheDebugHangEndsWithTheMount()
    {
        using var mount = new CancellationTokenSource();
        var hang = DebugStartBudget.HangAsync(mount.Token);
        await Task.Delay(20);
        Assert.False(hang.IsCompleted);
        await mount.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hang);
    }

    [Fact]
    public void TheDebugRouteAndScriptMatch()
    {
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/start-budget\"):", server);
        Assert.Contains("case (\"POST\", \"/start-budget\"):", server);
        Assert.Contains("\"/player-backend\" or \"/start-budget\" or", server);
        Assert.Contains("GET /start-budget, POST /start-budget?seconds={1..120|default}&hang={none|open|ready}", server);
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bale-debug.sh"));
        Assert.Contains("start-budget)", script);
        Assert.Contains("seconds=([0-9]+|default)|hang=(none|open|ready)", script);
    }
}

[CollectionDefinition(nameof(DebugStartBudget), DisableParallelization = true)]
public sealed class DebugStartBudgetCollection;
