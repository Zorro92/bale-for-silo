using System.Globalization;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

public class PlaybackLateFrameTraceTests
{
    private const string Empty = "samples=0 late=0 stream=0 gc=0 gap=0 other=0 max-gap=0.0 long-gaps=0\n";

    [Fact]
    public void EmptyTraceHasOnlyZeroTotals() => Assert.Equal(Empty, new PlaybackLateFrameTrace().Describe());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstSampleOnlyEstablishesTheBaselineEvenWhenLate(bool late)
    {
        var trace = new PlaybackLateFrameTrace();
        Sample(trace, 1000, pts: 100, position: 200, late: late, waits: 12, waitMilliseconds: 300,
            gcMilliseconds: 500, gen0: 10, gen1: 5, gen2: 2);
        Assert.Equal("samples=1 late=0 stream=0 gc=0 gap=0 other=0 max-gap=0.0 long-gaps=0\n", trace.Describe());
    }

    [Fact]
    public void DeltasUseTheImmediatelyPreviousSampleIncludingNonLateDeliveries()
    {
        var trace = new PlaybackLateFrameTrace();
        Sample(trace, 1000, pts: 10000, position: 9990, waits: 5, waitMilliseconds: 100,
            gcMilliseconds: 500, gen0: 10, gen1: 3, gen2: 1);
        Sample(trace, 1010, pts: 10040, duration: 30, position: 10030, waits: 7, waitMilliseconds: 150,
            gcMilliseconds: 505, gen0: 11, gen1: 4, gen2: 1);
        Sample(trace, 1035.5, pts: 10070.2, position: 10080.4, late: true, waits: 9, waitMilliseconds: 180.6,
            gcMilliseconds: 511.5, gen0: 13, gen1: 6, gen2: 2);
        Assert.Equal("samples=3 late=1 stream=1 gc=0 gap=0 other=0 max-gap=25.5 long-gaps=0\n"
            + "t=35.5 pts=10070.2 late=10.2 ahead=-10.4 gap=25.5 dw=2/30.6 gc=6.5 g0=2 g1=2 g2=1 class=stream\n",
            trace.Describe());
    }

    [Fact]
    public void AheadBeforeUsesThePreviousSampleEndAndTheCurrentPlayerPosition()
    {
        var trace = new PlaybackLateFrameTrace();
        Sample(trace, 0, pts: 100, duration: 80, position: 90);
        Sample(trace, 1, pts: 50, duration: 1, position: 150, late: true);
        Assert.Equal("t=1.0 pts=50.0 late=100.0 ahead=30.0 gap=1.0 dw=0/0.0 gc=0.0 g0=0 g1=0 g2=0 class=other",
            Assert.Single(Events(trace)));
    }

    [Theory]
    [InlineData(10, 10, 100, "stream")]
    [InlineData(0.1, 0, 1, "stream")]
    [InlineData(0, 4, 100, "gc")]
    [InlineData(0, 4, 10, "gc")]
    [InlineData(0, 3.9999, 100, "gap")]
    [InlineData(0, 0, 80.0001, "gap")]
    [InlineData(0, 3.9999, 80, "other")]
    [InlineData(0, 0, 10, "other")]
    public void ClassificationUsesStreamThenGcThenGapThenOtherWithExactThresholds(
        double waitMilliseconds, double gcMilliseconds, double gap, string classification)
    {
        var trace = new PlaybackLateFrameTrace();
        Sample(trace, 0);
        Sample(trace, gap, pts: 100, position: 101, late: true, waits: 1,
            waitMilliseconds: waitMilliseconds, gcMilliseconds: gcMilliseconds);
        Assert.EndsWith("class=" + classification, Assert.Single(Events(trace)));
        var summary = trace.Describe().Split('\n')[0];
        Assert.Contains("samples=2 late=1 ", summary);
        foreach (var name in new[] { "stream", "gc", "gap", "other" })
            Assert.Contains(name + "=" + (classification == name ? "1" : "0") + " ", summary);
    }

    [Fact]
    public void GapTotalsIncludeNonLateSamplesAndUseEachCurrentSamplesDuration()
    {
        var trace = new PlaybackLateFrameTrace();
        Sample(trace, 0, duration: 1);
        Sample(trace, 100, duration: 40, waits: 1, waitMilliseconds: 50, gcMilliseconds: 10);
        Sample(trace, 180, duration: 40, waits: 1, waitMilliseconds: 50, gcMilliseconds: 10);
        Sample(trace, 185, pts: 100, duration: 2, position: 101, late: true,
            waits: 1, waitMilliseconds: 50, gcMilliseconds: 10);
        Assert.StartsWith("samples=4 late=1 stream=0 gc=0 gap=1 other=0 max-gap=100.0 long-gaps=2\n", trace.Describe());
        Assert.EndsWith("class=gap", Assert.Single(Events(trace)));
    }

    [Fact]
    public void RingKeepsTheLast300EventsInOrderWhileTotalsSurviveMultipleWraps()
    {
        var trace = new PlaybackLateFrameTrace();
        Sample(trace, 0);
        for (var index = 1; index <= 605; index++)
            Sample(trace, index * 10, pts: index * 40, position: index * 40 + 5, late: true,
                waits: 1, waitMilliseconds: 2);
        var events = Events(trace);
        Assert.Equal(300, events.Length);
        Assert.StartsWith("t=3060.0 pts=12240.0 ", events[0]);
        Assert.StartsWith("t=6050.0 pts=24200.0 ", events[^1]);
        Assert.All(events, entry => Assert.EndsWith("class=other", entry));
        Assert.StartsWith("samples=606 late=605 stream=1 gc=0 gap=0 other=604 max-gap=10.0 long-gaps=0\n", trace.Describe());
    }

    [Fact]
    public void ResetClearsEventsEveryTotalAndTheCounterAndTimeBaselines()
    {
        var trace = new PlaybackLateFrameTrace();
        Sample(trace, 0);
        Sample(trace, 100, late: true, waitMilliseconds: 5);
        Sample(trace, 200, late: true, waitMilliseconds: 5, gcMilliseconds: 4);
        Sample(trace, 300, late: true, waitMilliseconds: 5, gcMilliseconds: 4);
        Sample(trace, 310, late: true, waitMilliseconds: 5, gcMilliseconds: 4);
        Assert.StartsWith("samples=5 late=4 stream=1 gc=1 gap=1 other=1 max-gap=100.0 long-gaps=3\n", trace.Describe());
        trace.Reset();
        Assert.Equal(Empty, trace.Describe());
        Sample(trace, 5000, pts: 100, position: 101, late: true, waits: 2, waitMilliseconds: 10,
            gcMilliseconds: 25, gen0: 2, gen1: 1, gen2: 1);
        Assert.Empty(Events(trace));
        Sample(trace, 5012.5, pts: 200, position: 202.5, late: true, waits: 3, waitMilliseconds: 20,
            gcMilliseconds: 30, gen0: 3, gen1: 2, gen2: 1);
        Assert.Equal("samples=2 late=1 stream=1 gc=0 gap=0 other=0 max-gap=12.5 long-gaps=0\n"
            + "t=12.5 pts=200.0 late=2.5 ahead=-62.5 gap=12.5 dw=1/10.0 gc=5.0 g0=1 g1=1 g2=0 class=stream\n",
            trace.Describe());
    }

    [Fact]
    public void NullStreamCountersAreUnavailableNotZeroAndStillAllowGcClassification()
    {
        var trace = new PlaybackLateFrameTrace();
        Sample(trace, 0, waits: null, waitMilliseconds: null);
        Sample(trace, 10, pts: 100, position: 101, late: true, waits: null, waitMilliseconds: null,
            gcMilliseconds: 5, gen0: 1);
        Assert.Equal("t=10.0 pts=100.0 late=1.0 ahead=-61.0 gap=10.0 dw=- gc=5.0 g0=1 g1=0 g2=0 class=gc",
            Assert.Single(Events(trace)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, 2L)]
    [InlineData(2L, null)]
    public void StreamDeltasRequireCountersFromBothSamples(long? previousWaits, long? currentWaits)
    {
        var trace = new PlaybackLateFrameTrace();
        Sample(trace, 0, waits: previousWaits, waitMilliseconds: previousWaits is null ? null : 10);
        Sample(trace, 10, late: true, waits: currentWaits, waitMilliseconds: currentWaits is null ? null : 100);
        Assert.Contains(" dw=- ", Assert.Single(Events(trace)));
        Assert.EndsWith("class=other", Assert.Single(Events(trace)));
    }

    [Fact]
    public void DescribeHasFixedInvariantFieldsAndOneLinePerEvent()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var trace = new PlaybackLateFrameTrace();
            Sample(trace, 7000, pts: 81000, duration: 40);
            Sample(trace, 19345.6, pts: 81234, position: 81246.3, late: true,
                waits: 1, waitMilliseconds: 310);
            Assert.Equal("samples=2 late=1 stream=1 gc=0 gap=0 other=0 max-gap=12345.6 long-gaps=1\n"
                + "t=12345.6 pts=81234.0 late=12.3 ahead=-206.3 gap=12345.6 dw=1/310.0 gc=0.0 g0=0 g1=0 g2=0 class=stream\n",
                trace.Describe());
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SamplingDoesNotAllocatePerFrameEvenWhenTheRingIsFull(bool late)
    {
        var trace = new PlaybackLateFrameTrace();
        for (var index = 0; index < 400; index++) Sample(trace, index, late: late);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 400; index < 1400; index++) Sample(trace, index, late: late);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task ConcurrentSamplesAndDescriptionsKeepConsistentTotalsAndABoundedRing()
    {
        var trace = new PlaybackLateFrameTrace();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 250; index++)
            {
                if (worker < 3) Sample(trace, 0, late: true);
                else
                {
                    var lines = trace.Describe().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    Assert.InRange(lines.Length, 1, 301);
                    Assert.All(lines.Skip(1), entry => Assert.EndsWith("class=other", entry));
                }
            }
        })));
        Assert.StartsWith("samples=750 late=749 stream=0 gc=0 gap=0 other=749 max-gap=0.0 long-gaps=0\n", trace.Describe());
        Assert.Equal(300, Events(trace).Length);
    }

    [Fact]
    public void RealPlayerWiringIsDebugOnlyResetsWithTheTimelineAndUsesSafeStatsDispatch()
    {
        var observer = Read("DebugFrameDelivery.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", observer);
        foreach (var required in new[] { "HttpRangeStream? stream = null", "PlaybackLateFrameTrace _lateFrameTrace = new()", "Stopwatch.GetTimestamp()",
            "var late = _meter.Observe", "if (!_seeking)", "_lateFrameTrace.Sample", "_stream?.DemandWaits", "_stream?.DemandWaitTime",
            "GC.GetTotalPauseDuration()", "GC.CollectionCount(0)", "GC.CollectionCount(1)", "GC.CollectionCount(2)",
            "internal string DescribeLateFrameTrace()", "internal void ResetLateFrameTrace()" })
            Assert.Contains(required, observer);
        var seeking = observer[observer.IndexOf("internal void SetSeeking", StringComparison.Ordinal)
            ..observer.IndexOf("internal string DescribeLateFrameTrace", StringComparison.Ordinal)];
        Assert.Contains("_seeking = seeking", seeking);
        Assert.Contains("_lateFrameTrace.Reset()", seeking);
        var starting = observer[observer.IndexOf("private void OnStarting", StringComparison.Ordinal)
            ..observer.IndexOf("private void OnSampleRequested", StringComparison.Ordinal)];
        Assert.Contains("_lateFrameTrace.Reset()", starting);
        Assert.Contains("stream: media.Stream", Read("PlayerPage.Media.cs.txt"));
        var stats = Read("PlayerPage.Stats.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", stats);
        Assert.Contains("_attempt?.Media?.FrameDelivery?.DescribeLateFrameTrace()", stats);
        Assert.Contains("_attempt?.Media?.FrameDelivery?.ResetLateFrameTrace()", stats);
        Assert.Contains("frame trace unavailable (no software video observer)\\n", stats);
        var endpoint = Read("DebugServer.Stats.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", endpoint);
        Assert.Contains("FrameTraceAsync(bool reset = false) => OnStatsUiAsync", endpoint);
        Assert.Contains("CurrentPage(out _) is not PlayerPage player", endpoint);
        Assert.Contains("\"409 Conflict\", \"no active player page\\n\"", endpoint);
        Assert.Contains("if (reset) player.ResetDebugLateFrameTrace()", endpoint);
        Assert.Contains("\"200 OK\", player.DebugLateFrameTrace", endpoint);
        Assert.DoesNotContain("ex.Message", endpoint);
        var routes = Read("DebugServer.cs.txt");
        var duplicateGuard = routes.Split('\n').Single(line => line.Contains("duplicateQueryKeys && path is", StringComparison.Ordinal));
        Assert.Contains("\"/frame-trace\"", duplicateGuard);
        var get = routes[routes.IndexOf("case (\"GET\", \"/frame-trace\")", StringComparison.Ordinal)
            ..routes.IndexOf("case (\"POST\", \"/frame-trace\")", StringComparison.Ordinal)];
        Assert.Contains("if (query.Count != 0)", get);
        Assert.Contains("\"400 Bad Request\"", get);
        Assert.Contains("return await FrameTraceAsync()", get);
        var post = routes[routes.IndexOf("case (\"POST\", \"/frame-trace\")", StringComparison.Ordinal)
            ..routes.IndexOf("case (\"GET\", \"/stats-rows\")", StringComparison.Ordinal)];
        Assert.Contains("query.Keys.Any(key => key is not \"action\")", post);
        Assert.Contains("query.GetValueOrDefault(\"action\") != \"reset\"", post);
        Assert.Contains("\"400 Bad Request\"", post);
        Assert.Contains("return await FrameTraceAsync(reset: true)", post);
    }

    private static void Sample(PlaybackLateFrameTrace trace, double now, double pts = 0, double duration = 40,
        double position = 0, bool late = false, long? waits = 0, double? waitMilliseconds = 0, double gcMilliseconds = 0,
        int gen0 = 0, int gen1 = 0, int gen2 = 0) =>
        trace.Sample(now, TimeSpan.FromMilliseconds(pts), TimeSpan.FromMilliseconds(duration), TimeSpan.FromMilliseconds(position),
            late, waits, waitMilliseconds is { } wait ? TimeSpan.FromMilliseconds(wait) : null,
            TimeSpan.FromMilliseconds(gcMilliseconds), gen0, gen1, gen2);

    private static string[] Events(PlaybackLateFrameTrace trace) =>
        trace.Describe().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
