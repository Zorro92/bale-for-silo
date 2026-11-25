using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Bale.Core.Browse;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

/// <summary>
/// Home follows watch state from other devices (silo-apple 0414ff74, silo-roku c1841ae, silo-server 165ab2b and f31842a).
/// The decisions are Rust unit tests (home_events.rs); these pin the handle's wire, the ticket mint and the socket the
/// transport opens, and the driver end to end against a loopback events socket.
/// </summary>
public class HomeEventsTests
{
    private const string Profile = "profile-1";
    private const string HelloDeclared = """{"type":"hello","schema_version":1,"connection_id":"01JC","available_channels":["user_state"],"required_action":"none"}""";
    private const string HelloOwed = """{"type":"hello","schema_version":1,"connection_id":"01JC","available_channels":["user_state"],"required_action":"subscribe"}""";
    private const string Subscribed = """{"type":"subscribed","channels":["user_state"]}""";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Fast timings; stable at once, so a 4001/4003 reconnects without waiting.</summary>
    private static readonly HomeEventsPolicy Fast = new(BackoffBaseMs: 50, BackoffMaxMs: 200, RetryAfterMaxMs: 1_000, PollMs: 60_000,
        DebounceMs: 30, MintTimeoutMs: 5_000, ConnectTimeoutMs: 5_000, SubscribeTimeoutMs: 5_000, StableMs: 0, LifetimeGraceMs: 1_000);

    private static string Changed(string profile) =>
        $$$"""{"type":"event","channel":"user_state","event":"user_state.changed","event_id":"01J","timestamp":"2026-11-19T23:42:54Z","data":{"profile_id":"{{{profile}}}","content_id":"episode-1","series_id":"series-1","change":"progress","playback_stopped":true}}""";

    private static SiloClient Client(Uri server, HttpMessageHandler transport)
    {
        var client = new SiloClient(server, new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), transport);
        client.RestoreSession("refresh-token", "access-token", DateTimeOffset.UtcNow.AddHours(1), Profile, "pin-proof");
        return client;
    }

    [Fact]
    public void TheMachinesActionsAndStateCrossTheHandle()
    {
        using var machine = new HomeEventsMachine();
        var start = machine.Start(0, Profile);
        Assert.Equal([new HomeEventsAction(HomeEventsActionKind.Mint, 1)], start.Actions);
        Assert.Equal(30_000, start.NextDeadlineMs);
        Assert.False(start.Live || start.Stopped);

        var ticket = machine.MintSucceeded(10, 1, "abc", 30, 300, "silo.events.v2");
        var connect = Assert.Single(ticket.Actions);
        Assert.Equal(HomeEventsActionKind.Connect, connect.Kind);
        Assert.Equal(1u, connect.Generation);
        Assert.Equal("api/v2/events/ws?channels=user_state", connect.Path);
        Assert.Equal(["silo.events.v2", "silo.ticket.abc"], connect.Protocols);

        Assert.Empty(machine.Opened(20, 1, "silo.events.v2").Actions);
        var send = Assert.Single(machine.Frame(30, 1, HelloOwed).Actions);
        Assert.Equal(HomeEventsActionKind.Send, send.Kind);
        Assert.Equal("""{"type":"subscribe","request_id":"bale-home","channels":["user_state"]}""", send.Text);

        var subscribed = machine.Frame(40, 1, Subscribed);
        Assert.True(subscribed.Live);
        Assert.Equal(["subscribed"], subscribed.Notes);
        // Android's 2 s debounce before the re-read.
        Assert.Equal(2_040, subscribed.NextDeadlineMs);
        Assert.Empty(machine.Tick(2_039).Actions);
        Assert.Equal([new HomeEventsAction(HomeEventsActionKind.Refresh)], machine.Tick(2_040).Actions);

        var closed = machine.Closed(60_000, 1, 4003);
        Assert.Equal([new HomeEventsAction(HomeEventsActionKind.Mint, 2)], closed.Actions);
        Assert.False(closed.Live);
        var refused = machine.MintFailed(60_010, 2, 403, null);
        Assert.True(refused.Stopped);
        Assert.Equal(["stopped: ticket refused (403)"], refused.Notes);
    }

    [Fact]
    public void ValuesThatAreNoStatusOrCloseCodeCrossAsNoAnswer()
    {
        using var machine = new HomeEventsMachine();
        machine.Start(0, Profile, Fast);
        // 0 and 70 000 are no HTTP status: no answer, so the backoff (50 ms).
        Assert.Equal(50, machine.MintFailed(1, 1, 0, null).NextDeadlineMs - 1);
        machine.Tick(51);
        Assert.Equal(["ticket failed (no answer)", "retrying in 100 ms"], machine.MintFailed(51, 2, 70_000, -5).Notes);
        machine.Tick(151);
        machine.MintSucceeded(151, 3, "t", 30, 300, "silo.events.v2");
        machine.Opened(152, 3, "silo.events.v2");
        Assert.Equal(["closed (no close frame)", "retrying in 200 ms"], machine.Closed(153, 3, 70_000).Notes);
    }

    [Fact]
    public void TheSocketGoesBelowTheBaseUrlOnWsOrWssWithoutUserInfo()
    {
        const string path = "api/v2/events/ws?channels=user_state";
        Assert.Equal("ws://silo.example.com:8096/api/v2/events/ws?channels=user_state",
            SiloHomeEventsTransport.Endpoint(new Uri("http://silo.example.com:8096"), path).AbsoluteUri);
        Assert.Equal("wss://silo.example.com/api/v2/events/ws?channels=user_state",
            SiloHomeEventsTransport.Endpoint(new Uri("https://silo.example.com"), path).AbsoluteUri);
        Assert.Equal("wss://silo.example.com:8443/silo/api/v2/events/ws?channels=user_state",
            SiloHomeEventsTransport.Endpoint(new Uri("https://silo.example.com:8443/silo"), path).AbsoluteUri);
        Assert.Equal("wss://silo.example.com/api/v2/events/ws?channels=user_state",
            SiloHomeEventsTransport.Endpoint(new Uri("https://user:secret@silo.example.com/"), path).AbsoluteUri);
    }

    [Fact]
    public void RetryAfterIsReadInSecondsOrAsADate()
    {
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        Dictionary<string, IEnumerable<string>> Header(string name, string value) => new() { [name] = [value] };
        Assert.Equal(5_000, SiloHomeEventsTransport.RetryAfterMs(Header("Retry-After", "5"), now));
        Assert.Equal(30_000, SiloHomeEventsTransport.RetryAfterMs(Header("retry-after", " 30 "), now));
        Assert.Equal(90_000, SiloHomeEventsTransport.RetryAfterMs(Header("Retry-After", "Wed, 25 Nov 2026 12:01:30 GMT"), now));
        Assert.Equal(0, SiloHomeEventsTransport.RetryAfterMs(Header("Retry-After", "Wed, 25 Nov 2026 11:00:00 GMT"), now));
        Assert.Null(SiloHomeEventsTransport.RetryAfterMs(Header("Retry-After", "soon"), now));
        Assert.Null(SiloHomeEventsTransport.RetryAfterMs(Header("Retry-After", "-5"), now));
        Assert.Null(SiloHomeEventsTransport.RetryAfterMs(new Dictionary<string, IEnumerable<string>>(), now));
        Assert.Null(SiloHomeEventsTransport.RetryAfterMs(null, now));
    }

    [Fact]
    public async Task TheTicketIsMintedAsTheProfileWithItsProof()
    {
        var mints = new MintHandler();
        using var client = Client(new Uri("https://silo.example.com"), mints);
        var answer = await new SiloHomeEventsTransport(client, Profile).MintAsync(CancellationToken.None);
        Assert.Equal("ticket-1", answer.Ticket!.Ticket);
        Assert.Equal(30, answer.Ticket.ExpiresIn);
        Assert.Equal(300, answer.Ticket.MaxConnectionSeconds);
        Assert.Equal("silo.events.v2", answer.Ticket.Protocol);
        var request = Assert.Single(mints.Requests);
        Assert.Equal("POST https://silo.example.com/api/v2/events/ws-ticket", request.Line);
        Assert.Equal("Bearer access-token", request.Headers["Authorization"]);
        Assert.Equal(Profile, request.Headers["X-Profile-Id"]);
        Assert.Equal("pin-proof", request.Headers["X-Profile-Token"]);
    }

    [Fact]
    public async Task AMintTheServerCouldNotCheckIsReportedOnceWithItsRetryAfter()
    {
        // f31842a: "a ticket mint whose check fails answers 503 dependency_unavailable with Retry-After". Kiota's own retry
        // handler would repeat it three times; the subscription backs off by itself.
        var mints = new MintHandler { Answer = _ => Problem(HttpStatusCode.ServiceUnavailable, "5") };
        using var client = Client(new Uri("https://silo.example.com"), mints);
        var answer = await new SiloHomeEventsTransport(client, Profile).MintAsync(CancellationToken.None);
        Assert.Null(answer.Ticket);
        Assert.Equal(503, answer.Status);
        Assert.Equal(5_000, answer.RetryAfterMs);
        Assert.Single(mints.Requests);

        mints.Answer = _ => Problem(HttpStatusCode.Forbidden, null);
        answer = await new SiloHomeEventsTransport(client, Profile).MintAsync(CancellationToken.None);
        Assert.Equal((403, (long?)null), (answer.Status!.Value, answer.RetryAfterMs));

        mints.Answer = _ => throw new HttpRequestException("private network details");
        answer = await new SiloHomeEventsTransport(client, Profile).MintAsync(CancellationToken.None);
        Assert.Equal(new HomeEventsMint(null), answer);
    }

    [Fact]
    public async Task NoTicketIsMintedForAProfileThatNoLongerActs()
    {
        var mints = new MintHandler();
        using var client = Client(new Uri("https://silo.example.com"), mints);
        client.UseProfile("profile-2");
        Assert.Equal(new HomeEventsMint(null), await new SiloHomeEventsTransport(client, Profile).MintAsync(CancellationToken.None));
        Assert.Empty(mints.Requests);
    }

    [Fact]
    public async Task HomeFollowsTheSocketAndTheCloseCodesSayWhetherItReconnects()
    {
        await using var events = new LoopbackEvents();
        var mints = new MintHandler();
        using var client = Client(events.Server, mints);
        using var refreshes = new SemaphoreSlim(0);
        var notes = new ConcurrentQueue<string>();
        using var watch = new HomeWatchState(new SiloHomeEventsTransport(client, Profile), Profile, () => refreshes.Release(),
            notes.Enqueue, Fast, TimeProvider.System);
        watch.Start();

        using var first = await events.NextAsync();
        Assert.Equal("/api/v2/events/ws?channels=user_state", first.Target);
        // The ticket in Sec-WebSocket-Protocol is the only credential: no bearer, no profile header, no cookie.
        Assert.Equal("silo.events.v2, silo.ticket.ticket-1", first.Headers["Sec-WebSocket-Protocol"]);
        Assert.DoesNotContain(first.Headers.Keys, name => name is "Authorization" or "X-Profile-Id" or "X-Profile-Token" or "Cookie");
        await first.SendAsync(HelloDeclared);
        await first.SendAsync(Subscribed);
        // The subscription's re-read.
        Assert.True(await refreshes.WaitAsync(Wait));

        await first.SendAsync(Changed("profile-2"));
        await first.SendAsync(Changed(Profile));
        Assert.True(await refreshes.WaitAsync(Wait));
        Assert.False(await refreshes.WaitAsync(300));

        // 4003 lifetime_ended: a new ticket and socket at once, and the new subscription re-reads.
        await first.CloseAsync(4003, "lifetime_ended");
        using var second = await events.NextAsync();
        Assert.Equal("silo.events.v2, silo.ticket.ticket-2", second.Headers["Sec-WebSocket-Protocol"]);
        await second.SendAsync(HelloOwed);
        using (var owed = new CancellationTokenSource(Wait))
            Assert.Equal("""{"type":"subscribe","request_id":"bale-home","channels":["user_state"]}""", await second.Received.Reader.ReadAsync(owed.Token));
        await second.SendAsync(Subscribed);
        Assert.True(await refreshes.WaitAsync(Wait));

        // 4002 credential_invalid: final, after one last re-read.
        await second.CloseAsync(4002, "credential_invalid");
        Assert.True(await refreshes.WaitAsync(Wait));
        Assert.False(await refreshes.WaitAsync(500));
        Assert.False(events.TryNext());
        Assert.Equal(2, mints.Requests.Count);
        Assert.Contains("closed (4003 lifetime_ended)", notes);
        Assert.Contains("stopped: credential invalid", notes);
        Assert.DoesNotContain(notes, note => note.Contains("ticket-", StringComparison.Ordinal) || note.Contains(Profile, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAccessChangeRereadsAndReconnectsAndOtherEndsBackOff()
    {
        await using var events = new LoopbackEvents();
        var mints = new MintHandler();
        using var client = Client(events.Server, mints);
        using var refreshes = new SemaphoreSlim(0);
        using var watch = new HomeWatchState(new SiloHomeEventsTransport(client, Profile), Profile, () => refreshes.Release(), null,
            Fast, TimeProvider.System);
        watch.Start();

        using var first = await events.NextAsync();
        await first.SendAsync(Subscribed);
        Assert.True(await refreshes.WaitAsync(Wait));
        // docs/realtime-api.md "Access changes": the frame, then 4001.
        await first.SendAsync("""{"type":"access_changed"}""");
        await first.CloseAsync(4001, "access_changed");
        using var second = await events.NextAsync();
        Assert.True(await refreshes.WaitAsync(Wait));
        Assert.Equal("silo.events.v2, silo.ticket.ticket-2", second.Headers["Sec-WebSocket-Protocol"]);

        // 4004 authority_unavailable: the same credential after the backoff.
        await second.SendAsync(Subscribed);
        Assert.True(await refreshes.WaitAsync(Wait));
        await second.CloseAsync(4004, "authority_unavailable");
        using var third = await events.NextAsync();
        Assert.Equal(3, mints.Requests.Count);
        // A dropped connection, no close frame at all.
        third.Abort();
        using var fourth = await events.NextAsync();
        Assert.Equal(4, mints.Requests.Count);
    }

    [Fact]
    public async Task EndingTheWatchClosesTheSocketGoingAwayAndReadsNothingMore()
    {
        await using var events = new LoopbackEvents();
        using var client = Client(events.Server, new MintHandler());
        using var refreshes = new SemaphoreSlim(0);
        var watch = new HomeWatchState(new SiloHomeEventsTransport(client, Profile), Profile, () => refreshes.Release(), null, Fast,
            TimeProvider.System);
        watch.Start();
        using var first = await events.NextAsync();
        await first.SendAsync(Subscribed);
        Assert.True(await refreshes.WaitAsync(Wait));

        watch.Dispose();
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, await first.ClientClosed.Task.WaitAsync(Wait));
        Assert.False(await refreshes.WaitAsync(300));
        Assert.False(events.TryNext());
        watch.Dispose();
    }

    [Fact]
    public async Task WithoutAnEventsSocketHomeIsPolled()
    {
        // HomeWatchState.bs pollHomeWatchState: while no subscription is live, Home is read on a timer instead.
        var transport = new ScriptedTransport(() => new HomeEventsMint(null, 404));
        using var refreshes = new SemaphoreSlim(0);
        var notes = new ConcurrentQueue<string>();
        using var watch = new HomeWatchState(transport, Profile, () => refreshes.Release(), notes.Enqueue,
            Fast with { PollMs = 150 }, TimeProvider.System);
        watch.Start();
        Assert.True(await refreshes.WaitAsync(Wait));
        Assert.True(await refreshes.WaitAsync(Wait));
        Assert.Equal(1, transport.Mints);
        Assert.Contains("stopped: no events socket (404)", notes);
        Assert.Contains("poll", notes);
    }

    [Fact]
    public async Task AWatchDisposedBeforeItStartsDoesNothing()
    {
        var transport = new ScriptedTransport(() => new HomeEventsMint(null, 503, 1_000));
        var watch = new HomeWatchState(transport, Profile, () => { }, null, Fast, TimeProvider.System);
        watch.Dispose();
        watch.Start();
        await Task.Delay(200);
        Assert.Equal(0, transport.Mints);
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string? retryAfter)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent($$"""{"type":"https://silo.example/problems/x","title":"x","status":{{(int)status}}}""", Encoding.UTF8,
                "application/problem+json"),
        };
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    /// <summary>The ticket mint, answering ticket-1, ticket-2, ... unless told otherwise; records each request.</summary>
    private sealed class MintHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(string Line, IReadOnlyDictionary<string, string> Headers)> RequestsQueue { get; } = new();
        public IReadOnlyList<(string Line, IReadOnlyDictionary<string, string> Headers)> Requests => RequestsQueue.ToArray();
        public Func<int, HttpResponseMessage>? Answer { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestsQueue.Enqueue(($"{request.Method} {request.RequestUri}",
                request.Headers.ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase)));
            var number = RequestsQueue.Count;
            var response = Answer?.Invoke(number) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"ticket":"ticket-{{number}}","expires_in":30,"max_connection_seconds":300,"protocol":"silo.events.v2"}""",
                    Encoding.UTF8, "application/json"),
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class ScriptedTransport(Func<HomeEventsMint> mint) : IHomeEventsTransport
    {
        private int _mints;
        public int Mints => Volatile.Read(ref _mints);

        public Task<HomeEventsMint> MintAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _mints);
            return Task.FromResult(mint());
        }

        public Task<IHomeEventsConnection> ConnectAsync(string path, IReadOnlyList<string> protocols, CancellationToken cancellationToken) =>
            throw new HomeEventsConnectException(null, new IOException());
    }

    /// <summary>A loopback events socket: accepts each upgrade, records its request, and speaks frames as the server.</summary>
    private sealed class LoopbackEvents : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Channel<Accepted> _accepted = Channel.CreateUnbounded<Accepted>();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public LoopbackEvents()
        {
            _listener.Start();
            _loop = AcceptAsync();
        }

        public Uri Server => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

        public async Task<Accepted> NextAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            return await _accepted.Reader.ReadAsync(timeout.Token);
        }

        public bool TryNext()
        {
            if (!_accepted.Reader.TryRead(out var accepted)) return false;
            accepted.Dispose();
            return true;
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = UpgradeAsync(tcp);
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task UpgradeAsync(TcpClient tcp)
        {
            var stream = tcp.GetStream();
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one) == 0) return;
                head.Append((char)one[0]);
            }
            var lines = head.ToString().Split("\r\n");
            var target = lines[0].Split(' ')[1];
            var headers = lines.Skip(1).Where(line => line.Length > 0).Select(line => line.Split(':', 2))
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n"
                + $"Sec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: silo.events.v2\r\n\r\n"));
            var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                SubProtocol = "silo.events.v2",
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
            });
            var accepted = new Accepted(target, headers, socket, tcp);
            accepted.Receive();
            _accepted.Writer.TryWrite(accepted);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
            while (_accepted.Reader.TryRead(out var accepted)) accepted.Dispose();
        }
    }

    private sealed class Accepted(string target, IReadOnlyDictionary<string, string> headers, WebSocket socket, TcpClient tcp) : IDisposable
    {
        public string Target => target;
        public IReadOnlyDictionary<string, string> Headers => headers;

        /// <summary>The client's text frames.</summary>
        public Channel<string> Received { get; } = Channel.CreateUnbounded<string>();

        /// <summary>The client's close status, or null when its connection just ended.</summary>
        public TaskCompletionSource<WebSocketCloseStatus?> ClientClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Receive() => _ = ReceiveAsync();

        private async Task ReceiveAsync()
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ClientClosed.TrySetResult(socket.CloseStatus);
                        return;
                    }
                    Received.Writer.TryWrite(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            }
            catch (Exception)
            {
                ClientClosed.TrySetResult(null);
            }
        }

        public Task SendAsync(string text) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

        public Task CloseAsync(int code, string reason) => socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);

        /// <summary>The server goes away without a close frame.</summary>
        public void Abort()
        {
            socket.Abort();
            tcp.Close();
        }

        public void Dispose()
        {
            socket.Dispose();
            tcp.Dispose();
        }
    }
}
