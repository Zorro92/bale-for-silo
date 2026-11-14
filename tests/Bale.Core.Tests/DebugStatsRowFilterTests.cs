using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugStatsRowFilterTests
{
    private static readonly (string Label, string Value)[] Rows =
    [
        ("Read-ahead", "on"), ("Stream reads", "65536 B"), ("Dropped frames", "unavailable"), ("CPU", "1.2 cores"),
    ];

    [Fact]
    public void OnlyDroppedFramesStartsHiddenAndListKeepsSnapshotOrder()
    {
        var filter = new DebugStatsRowFilter();
        Assert.All(Rows.Where(row => row.Label != "Dropped frames"), row => Assert.False(filter.IsHidden(row.Label)));
        Assert.True(filter.IsHidden("dropped frames"));
        Assert.False(filter.IsHidden("Future row"));
        Assert.Equal("Read-ahead: on\nStream reads: on\nDropped frames: off\nCPU: on\n", filter.DescribeLabels(Rows));
    }

    [Fact]
    public void ShowBringsBackTheDefaultHiddenDroppedFramesRow()
    {
        var filter = new DebugStatsRowFilter();
        Assert.Empty(Apply(filter, "show", "Dropped frames"));
        Assert.False(filter.IsHidden("Dropped frames"));
        Assert.Empty(Apply(filter, "reset", null));
        Assert.True(filter.IsHidden("Dropped frames"));
    }

    [Fact]
    public void HideAndShowAreIdempotentAndMatchWholeLabelsIgnoringCase()
    {
        var filter = new DebugStatsRowFilter();
        Assert.Empty(Apply(filter, "hide", " read-ahead , CPU,read-ahead "));
        Assert.Empty(Apply(filter, "hide", "READ-AHEAD"));
        Assert.True(filter.IsHidden("Read-ahead"));
        Assert.True(filter.IsHidden("cpu"));
        Assert.False(filter.IsHidden("Read"));
        Assert.False(filter.IsHidden("CPU time"));
        Assert.False(filter.IsHidden("Stream reads"));
        Assert.Empty(Apply(filter, "show", "READ-AHEAD"));
        Assert.Empty(Apply(filter, "show", "Read-ahead"));
        Assert.False(filter.IsHidden("Read-ahead"));
        Assert.True(filter.IsHidden("CPU"));
    }

    [Fact]
    public void UnknownLabelsAreReportedOnceAndApplyWhenRowsAppearLater()
    {
        var filter = new DebugStatsRowFilter();
        Assert.Equal(["Future (decoder)"], Apply(filter, "hide", "Future (decoder),cpu,future (DECODER)"));
        Assert.True(filter.IsHidden("future (decoder)"));
        Assert.Equal("Future (decoder): off\n", filter.DescribeLabels([("Future (decoder)", "available")]));
        Assert.False(new DebugStatsRowFilter().IsHidden("Future (decoder)"));
    }

    [Fact]
    public void CommandsWithoutCurrentRowsStillRememberTheNextPlayersSelection()
    {
        var filter = new DebugStatsRowFilter();
        // Before a player opens there are no live rows, but known overlay labels are still not "unknown".
        Assert.True(filter.TryApply("hide", "CPU,Read-ahead,Future row", [], out var unknown));
        Assert.Equal(["Future row"], unknown);
        Assert.Equal("Read-ahead: off\nStream reads: on\nDropped frames: off\nCPU: off\n", filter.DescribeLabels(Rows));
    }

    [Fact]
    public void OnlyAlsoHidesFutureUnlistedRowsAndSupportsSubsequentHideAndShow()
    {
        var filter = new DebugStatsRowFilter();
        Apply(filter, "hide", "CPU");
        Assert.Equal(["Future row"], Apply(filter, "only", "cpu,Future row"));
        Assert.False(filter.IsHidden("CPU"));
        Assert.False(filter.IsHidden("future ROW"));
        Assert.True(filter.IsHidden("Stream reads"));
        Assert.True(filter.IsHidden("Later row"));
        Apply(filter, "hide", "CPU,Future row");
        Apply(filter, "show", "Stream reads,Later row");
        Assert.True(filter.IsHidden("CPU"));
        Assert.True(filter.IsHidden("Future row"));
        Assert.False(filter.IsHidden("Stream reads"));
        Assert.False(filter.IsHidden("Later row"));
        Apply(filter, "only", "Read-ahead");
        Assert.False(filter.IsHidden("Read-ahead"));
        Assert.True(filter.IsHidden("Stream reads"));
        Assert.True(filter.IsHidden("Later row"));
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("only")]
    public void ListIsReadOnlyAndResetRestoresTheDefaultForCurrentAndFutureRows(string action)
    {
        var filter = new DebugStatsRowFilter();
        Apply(filter, action, "Read-ahead,Future row");
        var before = filter.DescribeLabels(Rows);
        Assert.Empty(Apply(filter, "list", null));
        Assert.Equal(before, filter.DescribeLabels(Rows));
        Assert.Empty(Apply(filter, "reset", null));
        Assert.All(Rows.Where(row => row.Label != "Dropped frames"), row => Assert.False(filter.IsHidden(row.Label)));
        Assert.True(filter.IsHidden("Dropped frames"));
        Assert.False(filter.IsHidden("Future row"));
        Assert.False(filter.IsHidden("Unlisted row"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("unknown", "CPU")]
    [InlineData("HIDE", "CPU")]
    [InlineData("hide", null)]
    [InlineData("show", "")]
    [InlineData("only", " ")]
    [InlineData("hide", ",CPU")]
    [InlineData("hide", "CPU,")]
    [InlineData("show", "CPU, ,Read-ahead")]
    [InlineData("only", "CPU,,Read-ahead")]
    [InlineData("hide", "CPU\nRead-ahead")]
    [InlineData("hide", "CPU\r")]
    [InlineData("hide", "CPU\t")]
    [InlineData("reset", "")]
    [InlineData("reset", "CPU")]
    [InlineData("list", "CPU")]
    public void InvalidCommandsLeaveTheWholeSelectionUnchanged(string? action, string? labels)
    {
        var filter = new DebugStatsRowFilter();
        Apply(filter, "only", "CPU");
        var before = filter.DescribeLabels(Rows);
        Assert.False(filter.TryApply(action, labels, Rows.Select(row => row.Label), out var unknown));
        Assert.Empty(unknown);
        Assert.Equal(before, filter.DescribeLabels(Rows));
        Assert.True(filter.IsHidden("Future row"));
    }

    [Fact]
    public void StatsAlwaysIncludeAllValuesAndMarkOnlyHiddenRowsWithoutMutatingTheSnapshot()
    {
        var filter = new DebugStatsRowFilter();
        var before = Rows.ToArray();
        Apply(filter, "hide", "Stream reads,Dropped frames");
        Assert.Equal("Read-ahead: on\nStream reads: 65536 B [hidden]\nDropped frames: unavailable [hidden]\nCPU: 1.2 cores\n",
            filter.DescribeStats(Rows));
        Assert.Equal("Read-ahead: on\nStream reads: off\nDropped frames: off\nCPU: on\n", filter.DescribeLabels(Rows));
        Assert.Equal(before, Rows);
        Assert.Equal("CPU: first\\r\\nsecond [hidden]\n", HiddenCpuStats());

        static string HiddenCpuStats()
        {
            var hidden = new DebugStatsRowFilter();
            Apply(hidden, "hide", "CPU");
            return hidden.DescribeStats([("CPU", "first\r\nsecond")]);
        }
    }

    [Fact]
    public void ReadoutUsesTheSanitizedPlanRowsWithoutRestoringRejectedMetadata()
    {
        var stats = PlaybackStats.FromPlan(new PlaybackPlan
        {
            Delivery = "original_http", DecisionReason = "unexpected diagnostic: suppressed",
            Source = new PlaybackSource { Container = "mkv", VideoProfile = "unexpected diagnostic: suppressed" },
        });
        var filter = new DebugStatsRowFilter();
        Apply(filter, "hide", "Delivery");
        var output = filter.DescribeStats(stats.HudRows());
        Assert.Contains("Delivery: Direct play [hidden]\n", output);
        Assert.Contains("Source: mkv\n", output);
        Assert.DoesNotContain("unexpected diagnostic", output);
    }

    [Fact]
    public void PlayerFiltersOnlyTheRenderedCopyAndKeepsTheOfficialColumnSplit()
    {
        var source = Read("PlayerPage.Stats.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("return _statsRows;", source);
        var render = source.IndexOf("private void RenderStatsRows()", StringComparison.Ordinal);
        Assert.DoesNotContain("StatsRowFilter.IsHidden", source[..render]);
        Assert.Contains("_statsRows.Where(row => !DebugServer.StatsRowFilter.IsHidden(row.Label)).ToArray()", source[render..]);
        Assert.Contains("var split = (rows.Length + 1) / 2", source[render..]);
        Assert.Contains("if (DebugStatsVisible) RenderStatsRows()", source);
        Assert.Contains("foreach (var (label, value) in player.DebugStatsRows)", Read("DebugServer.cs.txt"));
    }

    [Fact]
    public void EndpointsAreDebugOnlyUseTheMediaSafeDispatcherAndRerenderVisibleStatsImmediately()
    {
        var endpoint = Read("DebugServer.Stats.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", endpoint);
        foreach (var required in new[] { "static DebugStatsRowFilter StatsRowFilter { get; } = new()", "OnFfmpegUiAsync(action)",
            "StatsRowFilter.DescribeStats(player.DebugStatsRows)", "player?.RefreshDebugStatsRows()", "\"409 Conflict\"",
            "no active player page\\n", "ex.HResult", "Unknown labels: " })
            Assert.Contains(required, endpoint);
        Assert.DoesNotContain("ex.Message", endpoint);
        Assert.DoesNotContain("ex.ToString()", endpoint);
        var routes = Read("DebugServer.cs.txt");
        Assert.Contains("case (\"GET\", \"/stats\")", routes);
        Assert.Contains("case (\"GET\", \"/stats-rows\")", routes);
        Assert.Contains("case (\"POST\", \"/stats-rows\")", routes);
        var duplicateGuard = routes.Split('\n').Single(line => line.Contains("duplicateQueryKeys && path is", StringComparison.Ordinal));
        Assert.Contains("\"/stats-rows\"", duplicateGuard);
    }

    private static IReadOnlyList<string> Apply(DebugStatsRowFilter filter, string action, string? labels)
    {
        Assert.True(filter.TryApply(action, labels, Rows.Select(row => row.Label), out var unknown));
        return unknown;
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
