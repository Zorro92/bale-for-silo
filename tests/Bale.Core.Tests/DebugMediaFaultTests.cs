using Bale.Core.Native;

namespace Bale.Core.Tests;

/// <summary>bale-debug.sh media-fault: the debug server's parser and the Rust round trip (native/bale-core/src/media_fault.rs).</summary>
public sealed class DebugMediaFaultTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("40", 40)]
    [InlineData("600", 600)]
    public void AnOutageOfZeroTo600SecondsParses(string value, int seconds)
    {
        Assert.True(DebugMediaFault.TryParse(new Dictionary<string, string> { ["outage"] = value }, out var parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData("outage", "")]
    [InlineData("outage", "-1")]
    [InlineData("outage", "601")]
    [InlineData("outage", "1.5")]
    [InlineData("outage", " 4")]
    [InlineData("outage", "+4")]
    [InlineData("outage", "4s")]
    [InlineData("seconds", "4")]
    public void AnythingElseIsRefusedBeforeAnythingIsArmed(string key, string value)
    {
        Assert.False(DebugMediaFault.TryParse(new Dictionary<string, string> { [key] = value }, out var parsed));
        Assert.Null(parsed);
        Assert.False(DebugMediaFault.TryParse(new Dictionary<string, string>(), out _));
    }

    [Fact]
    public void TheStatusListsTheArmedOutageAndTheRecoveryCounters()
    {
        // Never arm a real outage here: it is process-wide and would fail the loopback stream tests running alongside.
        var status = DebugMediaFault.Apply(null);
        Assert.Contains("outage-remaining-ms: 0\n", status);
        Assert.Matches(@"range-retries: \d+\n", status);
        Assert.Matches(@"range-renewals: \d+\n", status);
        Assert.Matches(@"range-given-up: \d+\n", status);
        Assert.Equal(status.Split('\n').Length, DebugMediaFault.Apply(0).Split('\n').Length);
    }
}
