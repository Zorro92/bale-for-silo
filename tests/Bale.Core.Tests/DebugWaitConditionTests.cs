using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugWaitConditionTests
{
    [Theory]
    [InlineData("page Detail", "page", "Detail")]
    [InlineData(" PAGE\tCatalogGridPage ", "page", "CatalogGridPage")]
    [InlineData("loaded", "loaded", "")]
    [InlineData("text Clear filters", "text", "Clear filters")]
    [InlineData("focus PlayButton", "focus", "PlayButton")]
    public void ParsesTheConditionAndDefaultsToEightSeconds(string input, string kind, string value)
    {
        Assert.True(DebugWaitCondition.TryParse(input, null, out var condition, out var error), error);
        Assert.Equal(new DebugWaitCondition(kind, value, 8000), condition);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("12000", 12000)]
    [InlineData("15000", 15000)]
    [InlineData("08000", 8000)]
    public void AcceptsAnImmediateCheckAndBoundedTimeouts(string input, int expected)
    {
        Assert.True(DebugWaitCondition.TryParse("loaded", input, out var condition, out var error), error);
        Assert.Equal(expected, condition.TimeoutMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("page")]
    [InlineData("text ")]
    [InlineData("focus\t")]
    [InlineData("loaded extra")]
    [InlineData("tree Detail")]
    public void RejectsUnknownOrIncompleteConditions(string? input)
    {
        Assert.False(DebugWaitCondition.TryParse(input, null, out _, out var error));
        Assert.Contains("for must be", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("15001")]
    [InlineData("999999999999999999")]
    [InlineData("1.5")]
    [InlineData("no")]
    public void RejectsInvalidTimeoutsRatherThanWaitingUnboundedly(string input)
    {
        Assert.False(DebugWaitCondition.TryParse("loaded", input, out _, out var error));
        Assert.Contains("0 to 15000 ms", error);
    }

    [Fact]
    public void WaitUsesUiDispatchAndKeepsTheSummaryOnHttpTimeouts()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.cs.txt"));
        Assert.Contains("case (\"GET\", \"/wait\"):", source);
        Assert.Contains("var summary = await OnUiAsync(() =>", source);
        Assert.Contains("matched || elapsed.ElapsedMilliseconds >= condition.TimeoutMs ? DescribeWhere()", source);
        Assert.Contains("408 Request Timeout", source);
        Assert.Contains("await Task.Delay((int)Math.Clamp(condition.TimeoutMs - elapsed.ElapsedMilliseconds, 1, 100))", source);
        Assert.Contains("!PageFrame.IsTransitioning", source);
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bale-debug.sh"));
        Assert.Contains("curl --fail-with-body -sS --max-time \"$(( (wait_timeout + 999) / 1000 + 2 ))\"", script);
        Assert.Contains("--data-urlencode \"for=${wait_args[*]}\" --data-urlencode \"timeout=$wait_timeout\"", script);
    }
}
