using System.Net;
using System.Text;
using System.Text.Json;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using static Bale.Core.Tests.PlaybackTestSupport;

namespace Bale.Core.Tests;

/// <summary>
/// The paused-session timeout and an administrator's end (server dde5ed2; Android 56e23561a, Apple 80ae9437, Roku 6389fcc):
/// starts and replans advertise paused_timeout_v1, and a 410 with either problem type says the session is over.
/// </summary>
public class PlaybackServerEndTests
{
    // SequencedPlaybackTest.kt stopAfterAPausedTimeoutSettlesTheAttemptSoLaterStartsLeaveItAlone.
    private const string PausedTimeout = """
        {"type":"https://siloserver.org/docs/api/v2/problems/playback_session_paused_timeout",
         "title":"Playback session paused too long","status":410,"detail":"Playback stopped because it was paused for too long."}
        """;
    private const string Ended = """
        {"type":"https://siloserver.org/docs/api/v2/problems/playback_session_ended",
         "title":"Playback session ended","status":410,"detail":"An administrator ended this playback session"}
        """;

    private static readonly PlaybackPlan Plan = new()
    {
        PlanId = "plan-0123456789", PlanAttemptKey = "v3:0123456789abcdef", Delivery = "original_http",
    };

    [Fact]
    public void StartsAndEveryReplanAdvertiseThePausedTimeout()
    {
        var start = Start();
        Assert.Contains(PlaybackProtocol.PausedTimeoutFeature, start.ClientFeatures);
        Assert.Contains(PlaybackProtocol.PausedTimeoutFeature, PlaybackReplan.ForSeek(start, Plan, "plan-attempt", [], 1, 30).ClientFeatures);
        Assert.Contains(PlaybackProtocol.PausedTimeoutFeature,
            PlaybackReplan.ForTrackChange(start, Plan, "plan-attempt", [], 1, 30, 1, null).ClientFeatures);
        Assert.Contains(PlaybackProtocol.PausedTimeoutFeature,
            PlaybackReplan.ForFailure(start, Plan, "plan-attempt", [], 1, 30, new("decoder_failure")).ClientFeatures);
        // The single replan after a failed FFmpeg software mount rebuilds the features in Rust.
        var route = new PlaybackRouteDecision(true, true, "av1", "aac", 0, 1920, 1080);
        Assert.Contains(PlaybackProtocol.PausedTimeoutFeature,
            PlaybackReplan.ForFailure(start, Plan, "plan-attempt", [], 1, 30, new("decoder_failure"), route).ClientFeatures);
    }

    [Fact]
    public void OnlyA410WithEitherEndTypeSaysTheSessionIsOver()
    {
        Assert.Equal(PlaybackServerEnd.PausedTimeout, PlaybackServerEnds.From(Problem(410, "playback_session_paused_timeout")));
        Assert.Equal(PlaybackServerEnd.Administrator, PlaybackServerEnds.From(Problem(410, "playback_session_ended")));
        Assert.Null(PlaybackServerEnds.From(Problem(410, "unsupported_source")));
        Assert.Null(PlaybackServerEnds.From(Problem(404, "playback_session_ended")));
        Assert.Null(PlaybackServerEnds.From(new Problem { ResponseStatusCode = 410, Type = "playback_session_ended" }));
        Assert.Null(PlaybackServerEnds.From(new HttpRequestException("offline")));
        Assert.Null(PlaybackServerEnds.From(null));
    }

    [Fact]
    public void EachEndLeavesWithTheOfficialWording()
    {
        Assert.Equal("Playback stopped because it was paused for too long.", PlaybackServerEnds.Notice(PlaybackServerEnd.PausedTimeout));
        Assert.Equal("An administrator ended this playback session.", PlaybackServerEnds.Notice(PlaybackServerEnd.Administrator));
    }

    [Fact]
    public async Task ProgressAndReplanReadThePausedTimeoutAndTheExitsStopSettlesTheAttempt()
    {
        var deletes = new List<string>();
        var handler = new Handler((request, body) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/playback/session/progress" or "/api/v2/playback/session/replan" => Json(HttpStatusCode.Gone, PausedTimeout),
            // The server's own stop already won: the exit's stop replays its receipt.
            "/api/v2/playback/session" => Stop(deletes, body),
            _ => Json(HttpStatusCode.OK, Capabilities),
        });
        using var client = Create(handler);
        var journal = new InMemoryPlaybackJournal();
        var reporter = client.Playback.CreateProgressReporter(new Installation(), journal, "login");
        await reporter.AttachSessionAsync("session");

        var progress = await Assert.ThrowsAsync<Problem>(() => reporter.ReportProgressAsync("session", 600, true));
        Assert.Equal(PlaybackServerEnd.PausedTimeout, PlaybackServerEnds.From(progress));
        var replan = await Assert.ThrowsAsync<Problem>(() => client.Playback.ReplanAsync("session",
            PlaybackReplan.ForSeek(Start(), Plan, "plan-attempt", [], 1, 600), new Installation()));
        Assert.Equal(PlaybackServerEnd.PausedTimeout, PlaybackServerEnds.From(replan));

        await reporter.StopAsync("session", 600, true);
        Assert.True(Assert.Single(await journal.ReadAsync()).Terminal);
        Assert.Equal(0, await reporter.ReplayAsync());
        Assert.Single(deletes);
    }

    [Theory]
    [InlineData(PausedTimeout, PlaybackServerEnd.PausedTimeout)]
    [InlineData(Ended, PlaybackServerEnd.Administrator)]
    public async Task TheHeartbeatEndsOnlyWhenTheServerEndedTheSession(string answer, PlaybackServerEnd expected)
    {
        var sent = 0;
        var handler = new Handler((request, body) => request.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, Capabilities)
            // A transient failure keeps the heartbeat going; the end stops it.
            : ++sent == 1 ? Json(HttpStatusCode.ServiceUnavailable, """{"title":"Dependency unavailable","status":503}""")
            : Json(HttpStatusCode.Gone, answer));
        using var client = Create(handler);
        var reporter = client.Playback.CreateProgressReporter(new Installation(), new InMemoryPlaybackJournal(), "login",
            new ImmediateClock());
        await reporter.AttachSessionAsync("session", 42, true);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.Equal(expected, await reporter.RunAsync("session", timeout.Token));
        Assert.Equal(2, sent);
        Assert.Equal(expected, PlaybackServerEnds.From(reporter.LastProgressError));
    }

    private static Problem Problem(int status, string code) => new()
    {
        ResponseStatusCode = status, Type = "https://siloserver.org/docs/api/v2/problems/" + code,
    };

    private static HttpResponseMessage Stop(List<string> deletes, string? body)
    {
        deletes.Add(body!);
        return Json(HttpStatusCode.OK, """{"outcome":"replayed","stop_id":"server-stop"}""");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, (int)status >= 400 ? "application/problem+json" : "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body is not null) JsonDocument.Parse(body).Dispose();
            return answer(request, body);
        }
    }

    /// <summary>Every timer fires at once (on the pool) and moves the clock by its due time, so each heartbeat is due.</summary>
    private sealed class ImmediateClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Add(ref _ticks, Math.Max(0, dueTime.Ticks));
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new Timer();
        }

        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }
}
