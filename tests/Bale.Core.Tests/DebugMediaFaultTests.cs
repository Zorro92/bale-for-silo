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
        Assert.True(DebugMediaFault.TryParse(new Dictionary<string, string> { ["outage"] = value }, out var parsed, out var tail));
        Assert.Equal(seconds, parsed);
        Assert.Null(tail);
    }

    [Fact]
    public void AnEmptyTailParsesAloneOrWithAnOutage()
    {
        Assert.True(DebugMediaFault.TryParse(new Dictionary<string, string> { ["empty-tail"] = "3" }, out var outage, out var tail));
        Assert.Null(outage);
        Assert.Equal(3, tail);
        Assert.True(DebugMediaFault.TryParse(new Dictionary<string, string> { ["outage"] = "0", ["empty-tail"] = "20" }, out outage, out tail));
        Assert.Equal(0, outage);
        Assert.Equal(20, tail);
    }

    [Theory]
    [InlineData("outage", "")]
    [InlineData("outage", "-1")]
    [InlineData("outage", "601")]
    [InlineData("outage", "1.5")]
    [InlineData("outage", " 4")]
    [InlineData("outage", "+4")]
    [InlineData("outage", "4s")]
    [InlineData("empty-tail", "21")]
    [InlineData("empty-tail", "-1")]
    [InlineData("empty-tail", "two")]
    [InlineData("seconds", "4")]
    public void AnythingElseIsRefusedBeforeAnythingIsArmed(string key, string value)
    {
        Assert.False(DebugMediaFault.TryParse(new Dictionary<string, string> { [key] = value }, out var outage, out var tail));
        Assert.Null(outage);
        Assert.Null(tail);
        Assert.False(DebugMediaFault.TryParse(new Dictionary<string, string> { ["outage"] = "40", [key] = value }, out outage, out tail));
        Assert.Null(outage);
        Assert.Null(tail);
        Assert.False(DebugMediaFault.TryParse(new Dictionary<string, string>(), out _, out _));
    }

    [Fact]
    public void TheStatusListsTheArmedFaultsAndTheRecoveryCounters()
    {
        // Never arm a real outage or empty tail here: they are process-wide and would fail the loopback stream tests
        // running alongside.
        string status;
        try { status = DebugMediaFault.Apply(null); }
        catch (BaleCoreException error) when (error.Message == "unknown function media_fault.set")
        {
            // Production builds have no such call (Cargo feature debug-probes); this suite runs debug-server code.
            Assert.Fail("libbale_core.so lacks the debug calls: run `cargo build --release --locked --features debug-probes` in native/bale-core");
            throw;
        }
        Assert.Contains("outage-remaining-ms: 0\n", status);
        foreach (var counter in new[] { "empty-tail-armed", "range-retries", "range-renewals", "range-given-up",
                     "hls-empty-segments", "hls-empty-served", "hls-no-media", "hls-retries", "hls-resumes", "hls-given-up" })
            Assert.Matches(counter + @": \d+\n", status);
        Assert.Equal(status.Split('\n').Length, DebugMediaFault.Apply(0).Split('\n').Length);
    }
}
