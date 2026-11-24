using System.Net;
using Bale.Core.Connection;
using Bale.Core.Settings;

namespace Bale.Core.Tests;

public class PlaybackSettingsFeedTests
{
    private static SiloConnection Connect(StubHandler handler) => new(new Uri("https://example.com/silo/"),
        new ClientIdentity("device", "TV", "xbox", "Bale"), transport: handler);

    [Fact]
    public async Task DefaultsOnAndReadsTheEffectiveValueAndScope()
    {
        var handler = new StubHandler()
            .Enqueue("""{"items":[{"key":"playback.auto_play_next","value":false,"scope":"profile_device","source":"stored"}],"revision":3}""")
            .Enqueue("""{"items":[{"key":"playback.auto_play_next","value":false,"scope":"profile","source":"stored"}],"revision":3}""")
            .Enqueue("""{"items":[],"revision":3}""");
        using var connection = Connect(handler);
        var feed = new PlaybackSettingsFeed(connection.Api);
        Assert.Equal(new BooleanSetting(true), feed.AutoPlayNext);
        Assert.Equal(new BooleanSetting(false, true, true), await feed.RefreshAsync());
        Assert.Equal(new BooleanSetting(false, false, true), await feed.RefreshAsync());
        Assert.Equal(new BooleanSetting(true, false, true), await feed.RefreshAsync());
        Assert.Equal("/silo/api/v2/settings/values/effective", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("keys=playback.auto_play_next", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task WritesADeviceOverrideAndDeletesItForTheProfileSetting()
    {
        var handler = new StubHandler()
            .Enqueue("""{"key":"playback.auto_play_next","value":false,"scope":"profile_device","revision":4}""")
            .Enqueue(HttpStatusCode.NoContent, "")
            .Enqueue("""{"items":[{"key":"playback.auto_play_next","value":true,"scope":"default","source":"default"}],"revision":5}""");
        using var connection = Connect(handler);
        var feed = new PlaybackSettingsFeed(connection.Api);
        Assert.Equal(new BooleanSetting(false, true, true), await feed.SetAutoPlayNextAsync(false));
        Assert.Equal(feed.AutoPlayNext, new BooleanSetting(false, true, true));
        Assert.Equal(new BooleanSetting(true, false, true), await feed.UseProfileSettingAsync());
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/silo/api/v2/settings/values/playback.auto_play_next", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("scope=profile_device", handler.Requests[0].RequestUri!.Query);
        Assert.Equal("""{"value":false}""", handler.RequestBodies[0]);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Contains("scope=profile_device", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task ReadsTheShowNextUpLeadWithAutoPlay()
    {
        // playback.next_up_prompt_seconds: integer 0–120, default 30 (settings manifest; Android e580e1811).
        var handler = new StubHandler()
            .Enqueue("""{"items":[{"key":"playback.next_up_prompt_seconds","value":10,"scope":"profile_device","source":"stored"}],"revision":3}""")
            .Enqueue("""{"items":[{"key":"playback.next_up_prompt_seconds","value":0,"scope":"profile","source":"stored"}],"revision":4}""")
            .Enqueue("""{"items":[{"key":"playback.next_up_prompt_seconds","value":300,"scope":"profile"}],"revision":5}""")
            .Enqueue("""{"items":[{"key":"playback.next_up_prompt_seconds","value":"45","scope":"profile"}],"revision":6}""")
            .Enqueue("""{"items":[{"key":"playback.next_up_prompt_seconds","value":12.5,"scope":"profile"}],"revision":7}""")
            .Enqueue("""{"items":[],"revision":8}""")
            .Enqueue("""{"items":[{"key":"playback.next_up_prompt_seconds","value":60,"scope":"profile"}],"revision":9}""")
            .Enqueue(HttpStatusCode.InternalServerError, """{"detail":"No"}""");
        using var connection = Connect(handler);
        var feed = new PlaybackSettingsFeed(connection.Api);
        Assert.Equal(new IntegerSetting(30), feed.NextUpPromptSeconds);
        await feed.RefreshAsync();
        Assert.Equal(new IntegerSetting(10, true, true), feed.NextUpPromptSeconds);
        Assert.Contains("keys=playback.next_up_prompt_seconds", handler.Requests[0].RequestUri!.Query);
        await feed.RefreshAsync();
        Assert.Equal(new IntegerSetting(0, false, true), feed.NextUpPromptSeconds);
        await feed.RefreshAsync();
        Assert.Equal(new IntegerSetting(120, false, true), feed.NextUpPromptSeconds);
        await feed.RefreshAsync();
        Assert.Equal(new IntegerSetting(45, false, true), feed.NextUpPromptSeconds);
        await feed.RefreshAsync();
        Assert.Equal(new IntegerSetting(30, false, true), feed.NextUpPromptSeconds);
        await feed.RefreshAsync();
        Assert.Equal(new IntegerSetting(30, false, true), feed.NextUpPromptSeconds);
        // A failed read keeps the last value; Reset (a profile switch) drops it.
        await feed.RefreshAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => feed.RefreshAsync());
        Assert.Equal(new IntegerSetting(60, false, true), feed.NextUpPromptSeconds);
        feed.Reset();
        Assert.Equal(new IntegerSetting(30), feed.NextUpPromptSeconds);
    }

    [Fact]
    public async Task AFailedReadKeepsTheLastValueAndResetDropsIt()
    {
        var handler = new StubHandler()
            .Enqueue("""{"items":[{"key":"playback.auto_play_next","value":false,"scope":"profile"}],"revision":1}""")
            .Enqueue(HttpStatusCode.InternalServerError, """{"detail":"No"}""");
        using var connection = Connect(handler);
        var feed = new PlaybackSettingsFeed(connection.Api);
        await feed.RefreshAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => feed.RefreshAsync());
        Assert.False(feed.AutoPlayNext.Value);
        feed.Reset();
        Assert.Equal(new BooleanSetting(true), feed.AutoPlayNext);
    }
}

public class BufferingQualitySettingTests
{
    private static SiloConnection Connect(StubHandler handler) => new(new Uri("https://example.com/silo/"),
        new ClientIdentity("device", "TV", "xbox", "Bale"), transport: handler);

    [Fact]
    public async Task ReadsTheModeWithAutoPlayAndItsScope()
    {
        var handler = new StubHandler()
            .Enqueue("""
                {"items":[{"key":"playback.auto_play_next","value":true,"scope":"profile","source":"stored"},
                {"key":"playback.buffering_quality_mode","value":"never","scope":"profile_device","source":"stored"}],"revision":30}
                """)
            .Enqueue("""
                {"items":[{"key":"playback.auto_play_next","value":true,"scope":"default","source":"default"},
                {"key":"playback.buffering_quality_mode","value":"always","scope":"default","source":"default"}],"revision":30}
                """);
        using var connection = Connect(handler);
        var feed = new PlaybackSettingsFeed(connection.Api);
        Assert.Equal(new BufferingQualitySetting(BufferingQualityMode.Always), feed.BufferingQuality);
        await feed.RefreshAsync();
        Assert.Equal(new BufferingQualitySetting(BufferingQualityMode.Never, true, true), feed.BufferingQuality);
        await feed.RefreshAsync();
        Assert.Equal(new BufferingQualitySetting(BufferingQualityMode.Always, false, true), feed.BufferingQuality);
        var query = handler.Requests[0].RequestUri!.Query;
        Assert.Contains("keys=playback.auto_play_next", query);
        Assert.Contains("keys=playback.buffering_quality_mode", query);
    }

    private sealed class FakeMemory : ISettingsMemory
    {
        public Dictionary<string, string> Values { get; } = new();
        public string? Read(string key) => Values.GetValueOrDefault(key);
        public void Write(string key, string value) => Values[key] = value;
    }

    [Fact]
    public async Task TheLastModeSeenOnThisDeviceStandsInUntilTheFirstRead()
    {
        var handler = new StubHandler()
            .Enqueue("""{"items":[{"key":"playback.buffering_quality_mode","value":"ask","scope":"profile"}],"revision":3}""")
            .Enqueue("""{"items":[{"key":"playback.buffering_quality_mode","value":"never","scope":"profile"}],"revision":4}""");
        using var connection = Connect(handler);
        var memory = new FakeMemory();
        var profile = "p1";
        var feed = new PlaybackSettingsFeed(connection.Api, () => "server\n" + profile) { Memory = memory };
        Assert.Equal(BufferingQualityMode.Always, feed.BufferingQuality.Value);
        await feed.RefreshAsync();
        Assert.Equal(BufferingQualityMode.Ask, feed.BufferingQuality.Value);
        var key = Assert.Single(memory.Values).Key;
        Assert.DoesNotContain("server", key);
        // A new launch (or a reset) for the same profile starts on the remembered mode until its own read.
        var next = new PlaybackSettingsFeed(connection.Api, () => "server\n" + profile) { Memory = memory };
        Assert.Equal(BufferingQualityMode.Ask, next.BufferingQuality.Value);
        await next.RefreshAsync();
        Assert.Equal(BufferingQualityMode.Never, next.BufferingQuality.Value);
        next.Reset();
        Assert.Equal(BufferingQualityMode.Never, next.BufferingQuality.Value);
        // Another profile has its own memory; no profile, no memory.
        profile = "p2";
        Assert.Equal(BufferingQualityMode.Always, next.BufferingQuality.Value);
        Assert.Equal(BufferingQualityMode.Always,
            new PlaybackSettingsFeed(connection.Api, () => null) { Memory = memory }.BufferingQuality.Value);
    }

    [Fact]
    public async Task AServerWithoutTheSettingHidesItAndStillReadsAutoPlay()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.NotFound, """{"status":404,"title":"Not Found","detail":"unknown setting key"}""")
            .Enqueue("""{"items":[{"key":"playback.auto_play_next","value":false,"scope":"profile"}],"revision":20}""");
        using var connection = Connect(handler);
        var feed = new PlaybackSettingsFeed(connection.Api);
        feed.StoreBufferingQuality(new(BufferingQualityMode.Never, true, true));
        Assert.Equal(new BooleanSetting(false, false, true), await feed.RefreshAsync());
        Assert.Equal(new BufferingQualitySetting(), feed.BufferingQuality);
        Assert.DoesNotContain("buffering_quality_mode", handler.Requests[1].RequestUri!.Query);
        // The Show Next Up lead came with the first settings manifest, as auto-play did, so the retry keeps it.
        Assert.Contains("keys=playback.next_up_prompt_seconds", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task APickStoresADeviceValueAndUseProfileSettingDeletesIt()
    {
        var handler = new StubHandler()
            .Enqueue("""{"key":"playback.buffering_quality_mode","value":"ask","scope":"profile_device","revision":31}""")
            .Enqueue(HttpStatusCode.NoContent, "")
            .Enqueue("""
                {"items":[{"key":"playback.auto_play_next","value":true,"scope":"default"},
                {"key":"playback.buffering_quality_mode","value":"never","scope":"profile"}],"revision":32}
                """);
        using var connection = Connect(handler);
        var feed = new PlaybackSettingsFeed(connection.Api);
        Assert.Equal(new BufferingQualitySetting(BufferingQualityMode.Ask, true, true), await feed.SetBufferingQualityModeAsync(BufferingQualityMode.Ask));
        Assert.Equal(BufferingQualityMode.Ask, feed.BufferingQuality.Value);
        Assert.Equal(new BufferingQualitySetting(BufferingQualityMode.Never, false, true), await feed.UseProfileBufferingQualityModeAsync());
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/silo/api/v2/settings/values/playback.buffering_quality_mode", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("scope=profile_device", handler.Requests[0].RequestUri!.Query);
        Assert.Equal("""{"value":"ask"}""", handler.RequestBodies[0]);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Contains("scope=profile_device", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public void ModesRoundTripTheirWireValuesAndLabels()
    {
        Assert.Equal(["never", "ask", "always"], BufferingQualityModes.All.Select(BufferingQualityModes.Wire));
        Assert.Equal(["Never", "Ask first", "Automatically"], BufferingQualityModes.All.Select(BufferingQualityModes.Label));
        foreach (var mode in BufferingQualityModes.All) Assert.Equal(mode, BufferingQualityModes.FromWire(BufferingQualityModes.Wire(mode)));
        Assert.Null(BufferingQualityModes.FromWire("sometimes"));
        Assert.Equal(BufferingQualityMode.Always, BufferingQualityModes.Default);
    }
}
