using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox;
using Windows.Media.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>
/// A skip that reaches the end of the file ends playback there (PlayerPage.SkipMarkers.cs EndAtSkip, pinned by source in
/// SkipMarkersWiringTests): PlayerPage.Seek.cs runs as is against the seek stub, so the end it reports, the seek that
/// takes the player back into the file and a seek still in flight are exercised here.
/// </summary>
[Collection("Player seek")]
public class PlaybackSkipToEndTests
{
    private static PlaybackPlan Original() => new()
    {
        PlanId = "plan", PlanAttemptKey = "key", Delivery = "original_http",
        Stream = new() { Url = "/api/v2/stream/session/file", Container = "mp4", Protocol = "http" },
        Timeline = new() { SourceStartSeconds = 2, PlayerStartSeconds = 2, CanSeekAnywhere = true },
        Source = new() { DurationSeconds = 1000 },
    };

    [Fact]
    public async Task APlaybackASkipEndedReportsTheEndUntilTheViewerSeeksBackIn()
    {
        using var fixture = await Fixture.CreateAsync();
        var player = fixture.Player;
        Assert.Equal(2, player.Source, 6);
        // The paused player still sits in the credits; the title is at its end, as at the stream's own end.
        player.EndAt(1000);
        Assert.Equal(1000, player.Source, 6);
        // A seek made afterwards goes back into the file: positions follow the player again.
        Assert.Equal("Completed", await player.SeekSourceAsync(500));
        Assert.Equal(500, player.Source, 6);
    }

    [Fact]
    public async Task ASkipQueueRunningWhenASkipEndsPlaybackDropsItsSeekAndLeavesThePlayerPaused()
    {
        using var fixture = await Fixture.CreateAsync();
        var player = fixture.Player;
        // A trigger skip held behind a change, as one can be pending when the credits skip ends playback.
        await player.BeginReplanAsync();
        player.Skip(forward: true);
        Assert.True(player.SkipsPending);
        player.EndAt(1000);
        player.EndReplan();
        await player.SkipsDoneAsync();
        // The queued seek went with the end, and the queue left the stream paused (a remount's would have played).
        Assert.Equal(0, player.Session.NativeSeeks);
        Assert.True(player.IsPaused);
        Assert.Equal(1000, player.Source, 6);
    }

    private sealed class Fixture(SiloClient client, PlayerPage player) : IDisposable
    {
        internal PlayerPage Player => player;

        internal static async Task<Fixture> CreateAsync()
        {
            var client = Create(new ProgressHandler());
            var installation = new Installation();
            var reporter = client.Playback.CreateProgressReporter(installation, new InMemoryPlaybackJournal(), "login");
            await reporter.AttachSessionAsync("session");
            var session = new MediaPlaybackSession(2) { CanSeek = true, CompleteVod = true };
            var player = new PlayerPage(client, Start(), Original(), installation, reporter, session)
            {
                OpenSession = (_, start) => new(start) { CanSeek = true, CompleteVod = true },
            };
            return new(client, player);
        }

        public void Dispose()
        {
            player.Dispose();
            client.Dispose();
        }
    }

    private sealed class ProgressHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.EndsWith("/progress", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    outcome = "applied",
                    accepted = new { sequence = root.GetProperty("sequence").GetInt64(),
                        position = root.GetProperty("position").GetDouble(), is_paused = root.GetProperty("is_paused").GetBoolean() },
                }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
