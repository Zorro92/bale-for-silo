using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox;
using Bale.Xbox.Services;
using Windows.Media.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

[Collection("Player seek")]
public class DebugPlaybackControlTests
{
    [Theory]
    [InlineData("play", "Play")]
    [InlineData("pause", "Pause")]
    [InlineData("toggle", "Toggle")]
    public void ParsesOnlyDocumentedStates(string state, string expected)
    {
        Assert.True(DebugPlaybackControlParser.TryParse(state, out var request));
        Assert.Equal(expected, request.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Play")]
    [InlineData("PAUSE")]
    [InlineData(" toggle")]
    [InlineData("play ")]
    [InlineData("resume")]
    [InlineData("0")]
    [InlineData("play\npause")]
    public void RejectsMissingOrAmbiguousInput(string? state) =>
        Assert.False(DebugPlaybackControlParser.TryParse(state, out _));

    [Theory]
    [InlineData("mp4", 0)]
    [InlineData("mkv", 0)]
    [InlineData("hls", 120)]
    public async Task PausedSeekCanResumePauseAndToggleWithoutRemounting(string container, double offset)
    {
        using var fixture = await Fixture.CreateAsync(container, offset);
        var player = fixture.Player;
        var session = player.Session;
        Assert.Equal("200 OK", (await player.DebugSeekAsync(new(offset + 12.5, true))).Status);
        Assert.True(player.IsPaused);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            foreach (var (request, state) in new[]
            {
                (DebugPlaybackControl.Play, "Playing"), (DebugPlaybackControl.Play, "Playing"),
                (DebugPlaybackControl.Pause, "Paused"), (DebugPlaybackControl.Pause, "Paused"),
                (DebugPlaybackControl.Toggle, "Playing"), (DebugPlaybackControl.Toggle, "Paused"),
            })
            {
                var before = player.StateUpdates;
                var result = await player.DebugSetPlaybackAsync(request);
                Assert.Equal("200 OK", result.Status);
                Assert.Equal(string.Create(CultureInfo.InvariantCulture,
                    $"Position: source={offset + 12.5}s player=12.5s state={state}\n"), result.Body);
                Assert.Equal(before + 1, player.StateUpdates);
                Assert.Equal(state.ToLowerInvariant(), player.State);
                var saved = Assert.Single(await fixture.Journal.ReadAsync()).LastPosition!;
                Assert.Equal(offset + 12.5, saved.Position);
                Assert.Equal(state == "Paused", saved.IsPaused);
                Assert.Equal(saved.IsPaused, fixture.Handler.Pauses.Last());
                Assert.Equal(0, session.StateSubscribers);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Same(session, player.Session);
        Assert.Empty(player.Mounts);
        Assert.Equal(4, session.StateRequests - 2); // The seek pauses twice; idempotent transport calls do nothing.
    }

    [Theory]
    [InlineData(false, false, "Opening")]
    [InlineData(false, false, "Paused")]
    [InlineData(true, false, "Paused")]
    [InlineData(true, false, "Playing")]
    [InlineData(true, false, "Buffering")]
    [InlineData(true, true, "None")]
    public async Task RejectsOpeningAndUnreadyStatesWithoutChangingThePlayer(bool opened, bool started, string state)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Player.SetMountReadiness(opened, started);
        fixture.Player.Session.PlaybackState = Enum.Parse<MediaPlaybackState>(state);
        foreach (var request in Enum.GetValues<DebugPlaybackControl>())
        {
            var result = await fixture.Player.DebugSetPlaybackAsync(request);
            Assert.Equal("409 Conflict", result.Status);
            Assert.Equal("player is still loading or buffering from open\n", result.Body);
        }
        Assert.Equal(0, fixture.Player.Session.StateRequests);
        Assert.Empty(fixture.Handler.Pauses);
    }

    [Fact]
    public async Task RejectsBusyAndUnmountedPlayersAndAllowsBufferingAfterStart()
    {
        using var fixture = await Fixture.CreateAsync();
        var player = fixture.Player;
        await player.Lifecycle.WaitAsync();
        try
        {
            var busy = await player.DebugSetPlaybackAsync(DebugPlaybackControl.Play);
            Assert.Equal("409 Conflict", busy.Status);
            Assert.Equal("player is changing its playback pipeline\n", busy.Body);
        }
        finally { player.Lifecycle.Release(); }
        player.Session.PlaybackState = MediaPlaybackState.Buffering;
        Assert.Equal("200 OK", (await player.DebugSetPlaybackAsync(DebugPlaybackControl.Pause)).Status);
        player.Unmount();
        var absent = await player.DebugSetPlaybackAsync(DebugPlaybackControl.Play);
        Assert.Equal("409 Conflict", absent.Status);
        Assert.Equal("no active player pipeline\n", absent.Body);
    }

    [Fact]
    public async Task WaitsForRealPlayingAfterBufferingAndCleansUpOnNativeFailureOrCancellation()
    {
        using var fixture = await Fixture.CreateAsync();
        var player = fixture.Player;
        var session = player.Session;
        session.PlaybackState = MediaPlaybackState.Paused;
        session.OnStateRequested = _ => session.PlaybackState = MediaPlaybackState.Buffering;
        var pending = player.DebugSetPlaybackAsync(DebugPlaybackControl.Play);
        Assert.False(pending.IsCompleted);
        Assert.Equal("409 Conflict", (await player.DebugSetPlaybackAsync(DebugPlaybackControl.Toggle)).Status);
        session.PlaybackState = MediaPlaybackState.Playing;
        Assert.Equal("200 OK", (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        Assert.Equal(0, session.StateSubscribers);

        session.OnStateRequested = _ => throw new InvalidOperationException("sensitive native message");
        var failure = await player.DebugSetPlaybackAsync(DebugPlaybackControl.Pause);
        Assert.Equal("500 Internal Server Error", failure.Status);
        Assert.StartsWith("playback failed hr=0x", failure.Body);
        Assert.DoesNotContain("sensitive", failure.Body);
        Assert.Equal(0, session.StateSubscribers);
        Assert.Equal(1, player.Lifecycle.CurrentCount);

        session.OnStateRequested = _ => { };
        pending = player.DebugSetPlaybackAsync(DebugPlaybackControl.Pause);
        player.Unmount();
        Assert.Equal("409 Conflict", (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        Assert.Equal(0, session.StateSubscribers);
        Assert.Equal(1, player.Lifecycle.CurrentCount);
    }

    [Fact]
    public void EndpointIsDebugOnlyAndDispatchesThroughTheMediaSafeUiHelper()
    {
        var endpoint = Read("DebugServer.Playback.cs.txt");
        var page = Read("PlayerPage.Debug.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", endpoint);
        Assert.StartsWith("#if DEBUG_SERVER", page);
        Assert.Contains("case (\"POST\", \"/playback\")", Read("DebugServer.cs.txt"));
        Assert.Contains("await OnFfmpegUiAsync", endpoint);
        Assert.Contains("player.DebugSetPlaybackAsync(request)", endpoint);
        Assert.DoesNotContain("ex.Message", endpoint + page);
        Assert.Contains("UpdatePlaybackState(attempt, media)", page);
        Assert.Contains("await ReportPositionAsync(attempt, source, paused)", page);
        var shared = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("SetDisplayActive(_state != \"paused\")", shared);
        Assert.Contains("PushSubtitleClock()", shared);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));

    private sealed class Fixture(SiloClient client, PlayerPage player, InMemoryPlaybackJournal journal, ProgressHandler handler) : IDisposable
    {
        internal PlayerPage Player => player;
        internal InMemoryPlaybackJournal Journal => journal;
        internal ProgressHandler Handler => handler;
        internal static async Task<Fixture> CreateAsync(string container = "mp4", double offset = 0)
        {
            var handler = new ProgressHandler();
            var client = Create(handler);
            var installation = new Installation();
            var journal = new InMemoryPlaybackJournal();
            var reporter = client.Playback.CreateProgressReporter(installation, journal, "login");
            await reporter.AttachSessionAsync("session");
            var plan = new PlaybackPlan
            {
                Delivery = container == "hls" ? "server_transcode_hls" : "original_http",
                Stream = new() { Url = "/api/v2/stream/session/file", Container = container, Protocol = container == "hls" ? "hls" : "http" },
                Timeline = new() { TimelineOffsetSeconds = offset, CanSeekAnywhere = true },
                Source = new() { DurationSeconds = 1000 },
            };
            var session = new MediaPlaybackSession(2) { CanSeek = true, CompleteVod = true };
            return new(client, new PlayerPage(client, Start(), plan, installation, reporter, session), journal, handler);
        }
        public void Dispose() { player.Dispose(); client.Dispose(); }
    }

    private sealed class ProgressHandler : HttpMessageHandler
    {
        internal List<bool> Pauses { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.EndsWith("/progress", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement;
            Pauses.Add(root.GetProperty("is_paused").GetBoolean());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    outcome = "applied",
                    accepted = new { sequence = root.GetProperty("sequence").GetInt64(),
                        position = root.GetProperty("position").GetDouble(), is_paused = Pauses.Last() },
                }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
