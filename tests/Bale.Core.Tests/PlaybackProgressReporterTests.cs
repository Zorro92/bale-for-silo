using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

public class PlaybackProgressReporterTests
{
    [Fact]
    public async Task ConcurrentProgressHasStrictlyIncreasingPerSessionSequences()
    {
        var handler = new MutationHandler();
        using var client = Create(handler);
        var journal = new InMemoryPlaybackJournal();
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        await reporter.AttachSessionAsync("session");

        await Task.WhenAll(Enumerable.Range(1, 20).Select(position => reporter.ReportProgressAsync("session", position, false)));

        Assert.Equal(Enumerable.Range(1, 20).Select(value => (long)value), handler.Bodies.Select(body => Value(body, "sequence").GetInt64()));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v2/playback/session/progress", request.RequestUri!.AbsolutePath);
            Assert.Equal("profile", Assert.Single(request.Headers.GetValues("X-Profile-Id")));
        });
        Assert.All(handler.Bodies, body => Assert.Equal("installation", Value(body, "installation_id").GetString()));
        var entry = Assert.Single(await journal.ReadAsync());
        Assert.Equal(20, entry.Sequence);
        Assert.Null(entry.Progress);
        Assert.NotNull(entry.LastPosition);
    }

    [Fact]
    public async Task StopFollowsProgressAndCarriesAnExactFinalSample()
    {
        var handler = new MutationHandler();
        using var client = Create(handler);
        var journal = new InMemoryPlaybackJournal();
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        await reporter.AttachSessionAsync("session");
        await reporter.ReportProgressAsync("session", 10, false);
        await reporter.StopAsync("session", 17.25, true);

        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Delete }, handler.Requests.Select(request => request.Method));
        Assert.Equal("/api/v2/playback/session", handler.Requests[1].RequestUri!.AbsolutePath);
        var stop = handler.Bodies[1];
        Assert.Equal(2, Value(stop, "sequence").GetInt64());
        Assert.Equal(17.25, Value(stop, "position").GetDouble());
        Assert.True(Value(stop, "is_paused").GetBoolean());
        Assert.Equal("installation", Value(stop, "installation_id").GetString());
        Assert.True(Guid.TryParse(Value(stop, "stop_id").GetString(), out _));
        var entry = Assert.Single(await journal.ReadAsync());
        Assert.True(entry.Terminal);
        Assert.Null(entry.Progress);
        Assert.Equal(17.25, entry.LastPosition!.Position);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reporter.ReportProgressAsync("session", 20, false));
        await reporter.StopAsync("session", 99, false);
        Assert.Equal(0, await reporter.ReplayAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PendingProgressIsRetriedVerbatimBeforeTheNextLogicalSample()
    {
        var handler = new MutationHandler();
        handler.OnSend = (_, _) => handler.Requests.Count == 1
            ? throw new HttpRequestException("Lost progress reply") : null;
        using var client = Create(handler);
        var journal = new InMemoryPlaybackJournal();
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        await reporter.AttachSessionAsync("session");

        await Assert.ThrowsAsync<HttpRequestException>(() => reporter.ReportProgressAsync("session", 10, false));
        Assert.Equal(1, Assert.Single(await journal.ReadAsync()).Progress!.Sequence);
        await reporter.ReportProgressAsync("session", 23, true);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.Equal(new long[] { 1, 1, 2 }, handler.Bodies.Select(body => Value(body, "sequence").GetInt64()));
        Assert.Equal(23, Value(handler.Bodies[2], "position").GetDouble());
        Assert.True(Value(handler.Bodies[2], "is_paused").GetBoolean());
        Assert.Null(Assert.Single(await journal.ReadAsync()).Progress);
    }

    [Theory]
    [InlineData("""{"outcome":"applied"}""")]
    [InlineData("""{"outcome":"stopped","accepted":{"sequence":1,"position":10,"is_paused":false}}""")]
    [InlineData("""{"outcome":"applied","accepted":{"sequence":0,"position":10,"is_paused":false}}""")]
    [InlineData("""{"outcome":"replayed","accepted":{"sequence":1,"position":11,"is_paused":false}}""")]
    [InlineData("""{"outcome":"applied","accepted":{"sequence":1,"position":10,"is_paused":true}}""")]
    public async Task InconsistentProgressReceiptsLeaveTheSamplePending(string receipt)
    {
        var handler = new MutationHandler { OnSend = (_, _) => MutationHandler.Json(HttpStatusCode.OK, receipt) };
        using var client = Create(handler);
        var journal = new InMemoryPlaybackJournal();
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        await reporter.AttachSessionAsync("session");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => reporter.ReportProgressAsync("session", 10, false));
        Assert.Equal("Playback returned an inconsistent progress receipt.", error.Message);
        Assert.Equal(1, Assert.Single(await journal.ReadAsync()).Progress!.Sequence);
    }

    [Fact]
    public async Task ANewerAcceptedSequenceAdvancesTheWriter()
    {
        var handler = new MutationHandler();
        handler.OnSend = (_, _) => handler.Requests.Count == 1 ? MutationHandler.Json(HttpStatusCode.OK,
            """{"outcome":"stale_sample","accepted":{"sequence":8,"position":40,"is_paused":true}}""") : null;
        using var client = Create(handler);
        var reporter = client.Playback.CreateProgressReporter(new Installation(), new InMemoryPlaybackJournal(), "login");
        await reporter.AttachSessionAsync("session");
        await reporter.ReportProgressAsync("session", 10, false);
        await reporter.ReportProgressAsync("session", 12, false);
        Assert.Equal(new long[] { 1, 9 }, handler.Bodies.Select(body => Value(body, "sequence").GetInt64()));
    }

    [Fact]
    public async Task PositionRecordingUsesContentMovementAndForcesPauseChanges()
    {
        var handler = new MutationHandler();
        using var client = Create(handler);
        var journal = new CountingJournal();
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        await reporter.AttachSessionAsync("session");
        await reporter.RecordPositionAsync("session", 4, false);
        await reporter.RecordPositionAsync("session", 9.99, false);
        Assert.Equal(1, journal.Writes);
        await reporter.RecordPositionAsync("session", 10, false);
        Assert.Equal(2, journal.Writes);
        await reporter.RecordPositionAsync("session", 19.99, false);
        Assert.Equal(2, journal.Writes);
        await reporter.RecordPositionAsync("session", 0, false); // Backward seeks count too.
        await reporter.RecordPositionAsync("session", 0.25, true);
        Assert.Equal(4, journal.Writes);
        Assert.Equal(new PlaybackPosition(0.25, true), Assert.Single(await journal.ReadAsync()).LastPosition);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HeartbeatUsesTenWallSecondsIncludingPausedPlayback()
    {
        var handler = new MutationHandler();
        using var client = Create(handler);
        var clock = new ManualClock();
        var reporter = client.Playback.CreateProgressReporter(new Installation(), new InMemoryPlaybackJournal(), "login", clock);
        await reporter.AttachSessionAsync("session", 42, true);
        clock.Elapsed = TimeSpan.FromSeconds(9.99);
        Assert.False(await reporter.ReportIfDueAsync("session"));
        clock.Elapsed = TimeSpan.FromSeconds(10);
        Assert.True(await reporter.ReportIfDueAsync("session"));
        Assert.False(await reporter.ReportIfDueAsync("session"));
        clock.Elapsed = TimeSpan.FromSeconds(20);
        Assert.True(await reporter.ReportIfDueAsync("session"));
        Assert.Equal(new long[] { 1, 2 }, handler.Bodies.Select(body => Value(body, "sequence").GetInt64()));
        Assert.All(handler.Bodies, body =>
        {
            Assert.Equal(42, Value(body, "position").GetDouble());
            Assert.True(Value(body, "is_paused").GetBoolean());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrashReplayStopsAtTheLastJournaledPositionEvenAfterAnAcknowledgedProgress(bool acknowledged)
    {
        var journal = new InMemoryPlaybackJournal();
        using (var oldClient = Create(new MutationHandler()))
        {
            var oldReporter = oldClient.Playback.CreateProgressReporter(new Installation(), journal, "login");
            await oldReporter.AttachSessionAsync("session");
            if (acknowledged) await oldReporter.ReportProgressAsync("session", 10, false);
            else await oldReporter.RecordPositionAsync("session", 10, false);
            await oldReporter.RecordPositionAsync("session", 15, false);
        }
        var handler = new MutationHandler();
        using var client = Create(handler);
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        Assert.Equal(1, await reporter.ReplayAsync());
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Delete }, handler.Requests.Select(request => request.Method));
        Assert.Equal(10, Value(handler.Bodies[1], "position").GetDouble());
        Assert.Equal(acknowledged ? 2 : 1, Value(handler.Bodies[1], "sequence").GetInt64());
        Assert.True(Assert.Single(await journal.ReadAsync()).Terminal);
        Assert.Equal(0, await reporter.ReplayAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AFailedStopReplaysTheSameIdAndBodyOnNextLaunch()
    {
        var journal = new InMemoryPlaybackJournal();
        var oldHandler = new MutationHandler
        {
            OnSend = (_, _) => MutationHandler.Json(HttpStatusCode.OK, """{"outcome":"stopped","stop_id":"wrong-stop"}"""),
        };
        using (var oldClient = Create(oldHandler))
        {
            var oldReporter = oldClient.Playback.CreateProgressReporter(new Installation(), journal, "login");
            await oldReporter.AttachSessionAsync("session");
            await Assert.ThrowsAsync<InvalidDataException>(() => oldReporter.StopAsync("session", 37.75, true));
            Assert.False(Assert.Single(await journal.ReadAsync()).Terminal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => oldReporter.ReportProgressAsync("session", 50, false));
        }
        var handler = new MutationHandler();
        using var client = Create(handler);
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        Assert.Equal(1, await reporter.ReplayAsync());
        Assert.Equal(oldHandler.Bodies[0], handler.Bodies[1]);
        Assert.True(Assert.Single(await journal.ReadAsync()).Terminal);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("login")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("installation")]
    public async Task ReplayNeverSendsAnotherAuthoritysJournal(string changed)
    {
        var owner = new PlaybackJournalOwner("https://silo.example.com/", "login", "account", "profile", "installation");
        owner = changed switch
        {
            "origin" => owner with { Origin = "https://other.example.com/" },
            "login" => owner with { LoginId = "other" },
            "account" => owner with { AccountId = "other" },
            "profile" => owner with { ProfileId = "other" },
            _ => owner with { InstallationId = "other" },
        };
        var journal = new InMemoryPlaybackJournal();
        await journal.WriteAsync([new(owner, "session") { LastPosition = new(10, false) }]);
        var handler = new MutationHandler();
        using var client = Create(handler);
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        Assert.Equal(0, await reporter.ReplayAsync());
        Assert.Empty(handler.Requests);
        Assert.False(Assert.Single(await journal.ReadAsync()).Terminal);
    }

    [Fact]
    public async Task ReplayLeavesLiveSessionsAloneAndChecksTheServersCurrentInstallation()
    {
        var journal = new InMemoryPlaybackJournal();
        var handler = new MutationHandler();
        using var client = Create(handler);
        var live = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        await live.AttachSessionAsync("session");
        Assert.Equal(0, await live.ReplayAsync());
        Assert.Empty(handler.Requests);
        handler.OnSend = (_, _) => MutationHandler.Json(HttpStatusCode.OK, Capabilities.Replace("\"installation\"", "\"new-installation\""));
        var restarted = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        Assert.Equal(0, await restarted.ReplayAsync());
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task SwitchingAwayAndBackStillFencesAnOldReporter()
    {
        var handler = new MutationHandler();
        using var client = Create(handler);
        var reporter = client.Playback.CreateProgressReporter(new Installation(), new InMemoryPlaybackJournal(), "login");
        await reporter.AttachSessionAsync("session");
        client.UseProfile("other");
        client.UseProfile("profile");
        await Assert.ThrowsAsync<OperationCanceledException>(() => reporter.ReportProgressAsync("session", 10, false));
        await Assert.ThrowsAsync<OperationCanceledException>(() => reporter.StopAsync("session", 10, false));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AFailedJournalWritePreventsDispatchAndDoesNotAdvanceTheSequence()
    {
        var handler = new MutationHandler();
        using var client = Create(handler);
        var journal = new CountingJournal();
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        await reporter.AttachSessionAsync("session");
        journal.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => reporter.ReportProgressAsync("session", 0, false));
        Assert.Empty(handler.Requests);
        Assert.Equal(0, Assert.Single(await journal.ReadAsync()).Sequence);
        Assert.Null(Assert.Single(await journal.ReadAsync()).Progress);
    }

    [Fact]
    public async Task StopRetriesOnlyTransientFailuresAndKeepsItsIdentity()
    {
        var handler = new MutationHandler();
        handler.OnSend = (_, _) => handler.Requests.Count < 3
            ? MutationHandler.Json(HttpStatusCode.ServiceUnavailable, """{"title":"Unavailable","status":503}""") : null;
        using var client = Create(handler);
        var journal = new InMemoryPlaybackJournal();
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        await reporter.AttachSessionAsync("session");
        await reporter.StopAsync("session", 1, false);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Bodies.Distinct());
        Assert.True(Assert.Single(await journal.ReadAsync()).Terminal);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"outcome":"replayed","stop_id":"another-winning-stop"}""")]
    [InlineData(HttpStatusCode.NotFound, """{"title":"Not found","status":404}""")]
    public async Task ReplayedOrAlreadyForgottenStopsAreSettled(HttpStatusCode status, string response)
    {
        var handler = new MutationHandler { OnSend = (_, _) => MutationHandler.Json(status, response) };
        using var client = Create(handler);
        var journal = new InMemoryPlaybackJournal();
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        await reporter.AttachSessionAsync("session");
        await reporter.StopAsync("session", 12, false);
        Assert.True(Assert.Single(await journal.ReadAsync()).Terminal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task InvalidPositionsNeverReachTheJournalOrNetwork(double position)
    {
        var handler = new MutationHandler();
        using var client = Create(handler);
        var journal = new InMemoryPlaybackJournal();
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        await reporter.AttachSessionAsync("session");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reporter.RecordPositionAsync("session", position, false));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reporter.ReportProgressAsync("session", position, false));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reporter.StopAsync("session", position, false));
        Assert.Empty(handler.Requests);
        Assert.Equal(0, Assert.Single(await journal.ReadAsync()).Sequence);
    }

    private static JsonElement Value(string? json, string name)
    {
        using var document = JsonDocument.Parse(json!);
        return document.RootElement.GetProperty(name).Clone();
    }

    private sealed class ManualClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Elapsed;
    }

    private sealed class CountingJournal : IPlaybackJournal
    {
        private readonly InMemoryPlaybackJournal _inner = new();
        public int Writes { get; private set; }
        public bool FailWrites { get; set; }
        public Task<IReadOnlyList<PlaybackJournalEntry>> ReadAsync(CancellationToken cancellationToken = default) => _inner.ReadAsync(cancellationToken);
        public Task WriteAsync(IReadOnlyList<PlaybackJournalEntry> entries, CancellationToken cancellationToken = default)
        {
            if (FailWrites) throw new IOException("Journal write failed");
            Writes++;
            return _inner.WriteAsync(entries, cancellationToken);
        }
    }

    private sealed class MutationHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];
        public Func<HttpRequestMessage, JsonElement, HttpResponseMessage?>? OnSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(request);
            Bodies.Add(body);
            using var document = JsonDocument.Parse(body ?? "{}");
            var value = document.RootElement;
            if (OnSend?.Invoke(request, value) is { } custom) return custom;
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, Capabilities);
            if (request.Method == HttpMethod.Delete) return Json(HttpStatusCode.OK,
                $$$"""{"outcome":"stopped","stop_id":{{{value.GetProperty("stop_id").GetRawText()}}}}""");
            return Json(HttpStatusCode.OK, $$$"""
                {"outcome":"applied","accepted":{"sequence":{{{value.GetProperty("sequence").GetRawText()}}},
                "position":{{{value.GetProperty("position").GetRawText()}}},"is_paused":{{{value.GetProperty("is_paused").GetRawText()}}}}}
                """);
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, (int)status >= 400 ? "application/problem+json" : "application/json"),
        };
    }
}
