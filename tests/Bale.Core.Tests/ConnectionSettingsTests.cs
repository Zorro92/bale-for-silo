using System.Net;
using Bale.Core.Playback;
using Bale.Core.Settings;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>Settings → Playback's Streaming and Connection groups (TvSettingsScreen.kt, TvConnectionSettings.kt).</summary>
public class ConnectionSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero);
    private static readonly BufferingQualitySetting Always = new(BufferingQualityMode.Always, Supported: true);
    private static readonly BufferingQualitySetting Never = new(BufferingQualityMode.Never, Supported: true);
    private static readonly ConnectionLearnedLimitV2 Limit = new(44_100, new(2026, 10, 17, 16, 21, 40, TimeSpan.Zero), null);

    private static ConnectionSettingsView Build(BufferingQualitySetting mode, ConnectionStateV2? connection, bool metered = false,
        bool testing = false, ConnectionSpeedNotice? notice = null) =>
        ConnectionSettings.Build(mode, connection, offered: true, metered, testing, notice, Now, TimeZoneInfo.Utc);

    [Fact]
    public void TheSpeedLineFollowsTheSpec()
    {
        Assert.Equal("Not measured yet", ConnectionSettings.SpeedLine(null, Now));
        Assert.Equal("48 Mbps · measured 3 minutes ago on Wi-Fi",
            ConnectionSettings.SpeedLine(new() { Probe = new(48_000, false, Now.AddMinutes(-3), "wifi") }, Now));
        Assert.Equal("Faster than 115 Mbps · measured 1 hour ago on Ethernet",
            ConnectionSettings.SpeedLine(new() { Probe = new(115_900, true, Now.AddMinutes(-119), "ethernet") }, Now));
        // Without a probe the playback sample shows, always as a lower bound.
        Assert.Equal("Faster than 51 Mbps · seen during playback 2 hours ago",
            ConnectionSettings.SpeedLine(new() { PlaybackSample = new(51_200, Now.AddHours(-2)) }, Now));
        // The probe wins over a sample; an unknown transport is "this connection".
        Assert.Equal("9.9 Mbps · measured just now on this connection", ConnectionSettings.SpeedLine(new()
        {
            Probe = new(9_990, false, Now.AddSeconds(-20), "other"), PlaybackSample = new(51_200, Now),
        }, Now));
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(-300, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(59 * 60 + 59, "59 minutes ago")]
    [InlineData(3600, "1 hour ago")]
    [InlineData(23 * 3600, "23 hours ago")]
    [InlineData(24 * 3600, "1 day ago")]
    [InlineData(9 * 24 * 3600, "9 days ago")]
    public void AgesRoundDownAndAServerAheadIsJustNow(int secondsAgo, string text) =>
        Assert.Equal(text, ConnectionSettings.Age(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void TheLearnedLimitRowShowsItsLocalDayAndHidesInNever()
    {
        var connection = new ConnectionStateV2 { LearnedLimit = Limit, AutoBudgetKbps = 35_280 };
        var view = Build(Always, connection);
        Assert.Equal("Limited to 44 Mbps after buffering on Oct 17", view.LearnedLimit);
        Assert.Equal("Auto plays videos up to 35 Mbps without converting them.", view.Budget);
        Assert.True(view.StreamingShown);
        // Never ignores the limit: no row, and no budget that counts it.
        var never = Build(Never, connection);
        Assert.Null(never.LearnedLimit);
        Assert.Null(never.Budget);
        // Without a limit Never keeps the budget.
        Assert.NotNull(Build(Never, new() { AutoBudgetKbps = 35_280 }).Budget);
        // The day is this device's: 01:00 UTC on Oct 18 is still Oct 17 in New York.
        var lateLimit = Limit with { ObservedAt = new(2026, 10, 18, 1, 0, 0, TimeSpan.Zero) };
        var newYork = TimeZoneInfo.CreateCustomTimeZone("test-ny", TimeSpan.FromHours(-4), "test", "test");
        Assert.Equal("Limited to 44 Mbps after buffering on Oct 17", ConnectionSettings.LearnedLimitLabel(lateLimit, newYork));
        Assert.Equal("Limited to 850 kbps after buffering", ConnectionSettings.LearnedLimitLabel(new(850, default, null), newYork));
    }

    [Fact]
    public void TheModeRowNeedsTheServerSetting()
    {
        var unsupported = Build(new BufferingQualitySetting(), new ConnectionStateV2());
        Assert.False(unsupported.ModeShown);
        Assert.False(unsupported.StreamingShown);
        Assert.Equal("Automatically", unsupported.ModeValue);
        var ask = Build(new BufferingQualitySetting(BufferingQualityMode.Ask, Supported: true), null);
        Assert.True(ask.ModeShown);
        Assert.Equal("Ask first", ask.ModeValue);
    }

    [Fact]
    public void AMeteredLinkHidesTheSpeedTestAndBudget()
    {
        var view = Build(Always, new() { Probe = new(48_000, false, Now, "wifi"), AutoBudgetKbps = 38_400 }, metered: true,
            notice: new(ConnectionSpeedNoticeKind.Failed));
        Assert.True(view.Skipped);
        Assert.Null(view.SpeedLine);
        Assert.Null(view.Budget);
        Assert.Null(view.Notice);
    }

    [Fact]
    public void TestSpeedSaysTestingAndAnswersEachOutcome()
    {
        Assert.Equal("Testing…", Build(Always, null, testing: true).TestValue);
        Assert.Equal("Test speed", Build(Always, null).TestValue);

        var measured = new ConnectionSpeedTestOutcome(new ConnectionSpeedResult(48_000, false, 30_000_000, 5_000));
        Assert.Equal(ConnectionSpeedNoticeKind.StopPlayback, ConnectionSpeedNotice.ForTest(true, measured, ConnectionSpeedReport.Accepted, Now)!.Kind);
        Assert.Equal(ConnectionSpeedNoticeKind.Failed, ConnectionSpeedNotice.ForTest(false, null, null, Now)!.Kind);
        Assert.Equal(ConnectionSpeedNoticeKind.Failed, ConnectionSpeedNotice.ForTest(false, new(null), null, Now)!.Kind);
        Assert.Null(ConnectionSpeedNotice.ForTest(false, measured, ConnectionSpeedReport.Accepted, Now));
        Assert.Equal(ConnectionSpeedNoticeKind.NotSaved, ConnectionSpeedNotice.ForTest(false, measured, ConnectionSpeedReport.Refused, Now)!.Kind);
        Assert.Equal(ConnectionSpeedNoticeKind.NotSaved, ConnectionSpeedNotice.ForTest(false, measured, ConnectionSpeedReport.TransportFailure, Now)!.Kind);

        // A 429 counts down each second, says "1 second" at one, then hides.
        var held = ConnectionSpeedNotice.ForTest(false, new(null, TimeSpan.FromSeconds(30)), null, Now)!;
        Assert.Equal("Try again in 30 seconds", held.Text(Now));
        Assert.Equal("Try again in 2 seconds", held.Text(Now.AddSeconds(28.5)));
        Assert.Equal("Try again in 1 second", held.Text(Now.AddSeconds(29.5)));
        Assert.Null(held.Text(Now.AddSeconds(30)));
        Assert.Equal("Try again in 30 seconds", Build(Always, null, notice: held).Notice);
        Assert.Equal("Couldn’t save the speed test. Try again.", new ConnectionSpeedNotice(ConnectionSpeedNoticeKind.NotSaved).Text(Now));
    }

    [Fact]
    public async Task ResetDropsTheLimitEvenBeforeTheNextRead()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.NoContent, "");
        using var client = Create(stub);
        client.Playback.Speed.Update(null, new() { LearnedLimit = Limit, AutoBudgetKbps = 35_280 });
        Assert.True(await client.Playback.ResetLearnedLimitAsync(CancellationToken.None));
        Assert.Equal(HttpMethod.Delete, stub.Requests[0].Method);
        Assert.Equal("/api/v2/playback/connection/learned-limit", stub.Requests[0].RequestUri!.AbsolutePath);
        Assert.Null(client.Playback.Speed.Connection!.LearnedLimit);
        Assert.Equal(35_280, client.Playback.Speed.Connection.AutoBudgetKbps);
    }

    [Fact]
    public async Task AFailedResetKeepsTheLimit()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.InternalServerError, """{"detail":"No"}""");
        using var client = Create(stub);
        client.Playback.Speed.Update(null, new() { LearnedLimit = Limit });
        Assert.False(await client.Playback.ResetLearnedLimitAsync(CancellationToken.None));
        Assert.Equal(Limit, client.Playback.Speed.Connection!.LearnedLimit);
    }
}
