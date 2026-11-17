using Bale.Core.Playback;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

/// <summary>The page's playback facts become the controller's media events (the spec's Stall exclusions).</summary>
public sealed class BufferingSignalsTests
{
    private static readonly BufferingSnapshot Playing = new() { HasMount = true, WantsToPlay = true, Playing = true };
    private static readonly BufferingSnapshot Buffering = new() { HasMount = true, WantsToPlay = true };
    private static readonly BufferingSnapshot Paused = new() { HasMount = true, WantsToPlay = false };

    [Fact]
    public void AStartWaitsThenPlaysAndRepeatsReportNothing()
    {
        var signals = new BufferingSignals();
        signals.PlanAdopted();
        Assert.Equal([BufferingSignal.WaitingNetwork], signals.Update(new() { WantsToPlay = true }));
        Assert.Empty(signals.Update(Buffering));
        Assert.Equal([BufferingSignal.Playing], signals.Update(Playing));
        Assert.Empty(signals.Update(Playing));
        Assert.True(signals.PlanHasRun);
        Assert.Equal([BufferingSignal.WaitingNetwork], signals.Update(Buffering));
        Assert.Equal([BufferingSignal.Paused], signals.Update(Paused));
    }

    [Fact]
    public void BufferingAfterASeekStaysTheSeeksUntilPlaybackRuns()
    {
        var signals = new BufferingSignals();
        signals.PlanAdopted();
        signals.Update(Playing);
        Assert.Equal([BufferingSignal.WaitingSeek], signals.Update(Buffering with { SeekInFlight = true }));
        // The seek landed, but the buffering that follows is still the seek's.
        Assert.Empty(signals.Update(Buffering));
        Assert.Equal([BufferingSignal.Playing], signals.Update(Playing));
        Assert.Equal([BufferingSignal.WaitingNetwork], signals.Update(Buffering));
    }

    [Fact]
    public void ASeekBeforeTheFirstFrameIsStillTheStartsWait()
    {
        var signals = new BufferingSignals();
        signals.PlanAdopted();
        Assert.Equal([BufferingSignal.WaitingNetwork], signals.Update(Buffering with { SeekInFlight = true }));
    }

    [Fact]
    public void AReplacedSourceIsAReloadOnceThePlanHasRun()
    {
        var signals = new BufferingSignals();
        signals.PlanAdopted();
        signals.Update(Playing);
        Assert.Equal([BufferingSignal.WaitingReload], signals.Update(new() { WantsToPlay = true }));
        // A remount the viewer paused is a pause.
        Assert.Equal([BufferingSignal.Paused], signals.Update(new() { WantsToPlay = false }));
    }

    [Fact]
    public void SilenceWhileWaitingIsAnOutageUntilDataArrivesOrPlaybackRuns()
    {
        var signals = new BufferingSignals();
        signals.PlanAdopted();
        signals.Update(Playing);
        Assert.Equal([BufferingSignal.WaitingNetwork], signals.Update(Buffering));
        Assert.Equal([BufferingSignal.TroubleStarted], signals.Update(Buffering with { UpstreamSilent = true }));
        // The silence lifting is not enough: the outage waits for media data.
        Assert.Empty(signals.Update(Buffering));
        signals.NoteMediaDataArrived();
        Assert.Equal([BufferingSignal.TroubleEnded], signals.Update(Buffering));

        signals.NoteMediaRequestFailed();
        Assert.Equal([BufferingSignal.TroubleStarted], signals.Update(Buffering));
        Assert.Equal([BufferingSignal.TroubleEnded, BufferingSignal.Playing], signals.Update(Playing));
    }

    [Fact]
    public void SilenceWhilePlayingIsNoOutage()
    {
        var signals = new BufferingSignals();
        signals.PlanAdopted();
        Assert.Equal([BufferingSignal.Playing], signals.Update(Playing with { UpstreamSilent = true }));
    }

    [Fact]
    public void AppliedSignalsDriveTheController()
    {
        var harness = new BufferingQualityHarness(BufferingQualityMode.Always);
        var signals = new BufferingSignals();
        harness.At(0, c =>
        {
            c.PlanAdopted(BufferingQualityHarness.Plan("p1"));
            signals.PlanAdopted();
            BufferingSignals.Apply(c, signals.Update(Playing));
        });
        harness.At(125_000, c => BufferingSignals.Apply(c, signals.Update(Buffering)));
        harness.Clock.AdvanceTo(131_000);
        Assert.Single(harness.Replans);
    }

    [Fact]
    public void MediaSilenceUsesTheDeliveryLimits()
    {
        var silence = new MediaSilence();
        var stream = new object();
        silence.Reset(0);
        Assert.True(silence.Observe(stream, 10, 100));
        Assert.False(silence.Observe(stream, 10, 3_000));
        Assert.False(silence.Silent(4_099, segmented: false));
        Assert.True(silence.Silent(4_100, segmented: false));
        Assert.False(silence.Silent(15_099, segmented: true));
        Assert.True(silence.Silent(15_100, segmented: true));
        // A new mount's counters start at zero; its first bytes are data.
        Assert.True(silence.Observe(new object(), 5, 16_000));
        Assert.False(silence.Silent(16_500, segmented: false));
    }
}
