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
