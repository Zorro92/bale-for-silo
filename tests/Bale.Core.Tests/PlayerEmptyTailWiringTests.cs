namespace Bale.Core.Tests;

/// <summary>
/// How the Xbox player ends a mount that started past the media's end (silo-server 4bee8f8, PlayerPage.Media.cs
/// EndAtEmptyTail). The page compiles only on Windows, so its wiring is pinned by source here; the reader's flag is
/// RustHlsStreamTests and native/bale-core/src/hls.rs.
/// </summary>
public class PlayerEmptyTailWiringTests
{
    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)).ReplaceLineEndings("\n");

    [Fact]
    public void AnOpenFailureAFailureOrAMountThatNeverStartsEndsThereInsteadOfReplanning()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        // FFmpeg can't open an MPEG-TS stream of only PAT/PMT: that open failure ends playback, it doesn't replan.
        var create = media.IndexOf("await FFmpegMediaSource.CreateFromInputStreamAsync(forward, config).AsTask()", StringComparison.Ordinal);
        var filter = media.IndexOf("catch (Exception ex) when (media.Progressive is { EndedWithoutMedia: true } && !media.Token.IsCancellationRequested)", StringComparison.Ordinal);
        var end = media.IndexOf("EndAtEmptyTail(attempt, media);\n                    return;", StringComparison.Ordinal);
        Assert.True(create > 0 && filter > create && end > filter);
        Assert.Contains("else if (!media.Started && media.Progressive is { EndedWithoutMedia: true }) EndAtEmptyTail(attempt, media);\n                else QueueFailure(attempt, media, hresult, decoder, network);", media);

        var helper = media[media.IndexOf("private void EndAtEmptyTail(Attempt attempt, Mount media)", StringComparison.Ordinal)..];
        helper = helper[..helper.IndexOf("private void TrackProgressiveIdle(", StringComparison.Ordinal)];
        Assert.Contains("if (!IsCurrent(attempt, media) || media.FailureQueued || media.Started) return;", helper);
        // Marked finished first, so no failure, start budget or seek watchdog acts on it, then ended as MediaEnded does.
        var finished = helper.IndexOf("media.FailureQueued = true;", StringComparison.Ordinal);
        var ended = helper.IndexOf("BufferingMediaEnded();\n            if (!HandleEndedWithUpNext()) _ = StopAndGoBackAsync();", StringComparison.Ordinal);
        Assert.True(finished > 0 && ended > finished);
        Assert.DoesNotContain("QueueFailure", helper);
        Assert.DoesNotContain("RecoverCoreAsync", helper);

        var budget = Read("PlayerPage.StartBudget.cs.txt");
        var tick = budget[budget.IndexOf("private void UpdateStartBudget()", StringComparison.Ordinal)..];
        var check = tick.IndexOf("if (!media.Started && !media.Mounting && !media.FailureQueued && media.Progressive is { EndedWithoutMedia: true })", StringComparison.Ordinal);
        var spend = tick.IndexOf("if (budget.Update(now, facts)) StartBudgetRanOut(attempt, media);", StringComparison.Ordinal);
        Assert.True(check > 0 && spend > check, "an opened mount past the end ends before its budget is spent");
    }
}
