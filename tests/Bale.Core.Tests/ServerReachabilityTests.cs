using System.Net;
using System.Threading.Channels;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class ServerReachabilityTests
{
    private static readonly Uri Server = new("https://silo.example.test/base");
    private const string Info = """
        {"server_version":"test","api_major":2,"contract_digest":"digest","links":{"openapi":"/openapi","capabilities":"/capabilities"}}
        """;

    [Fact]
    public async Task FreshDiscoveryIsPublicUncachedAndPreservesTheConfiguredBasePath()
    {
        var stub = new StubHandler().Enqueue(Info);
        var checkedAt = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        using var monitor = new ServerReachabilityMonitor(Server, stub, new FixedClock(checkedAt));
        Assert.Equal(ServerReachabilityStatus.Unknown, monitor.State.Status);
        Assert.True(monitor.State.CanUseServer);
        Assert.Empty(stub.Requests);

        var state = await monitor.RetryNowAsync();

        Assert.Equal(ServerReachabilityStatus.Reachable, state.Status);
        Assert.Equal(checkedAt, state.LastCheckedAt);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("/base/api/v2/system/info", request.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("X-Profile-Id"));
        Assert.True(request.Headers.CacheControl!.NoCache);
        Assert.True(request.Headers.CacheControl.NoStore);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task NonSuccessDiscoveryIsUnreachable(HttpStatusCode status)
    {
        var stub = new StubHandler().Enqueue(status, Info);
        using var monitor = new ServerReachabilityMonitor(Server, stub);
        Assert.Equal(ServerReachabilityStatus.Unreachable, (await monitor.RetryNowAsync()).Status);
        Assert.False(monitor.State.CanUseServer);
    }

    [Theory]
    [InlineData("<html>proxy</html>")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"api_major\":2}")]
    [InlineData("{\"server_version\":\"x\",\"api_major\":1,\"contract_digest\":\"d\",\"links\":{\"openapi\":\"o\",\"capabilities\":\"c\"}}")]
    [InlineData("{\"server_version\":\"x\",\"api_major\":\"2\",\"contract_digest\":\"d\",\"links\":{\"openapi\":\"o\",\"capabilities\":\"c\"}}")]
    [InlineData("{\"server_version\":\"x\",\"api_major\":2,\"contract_digest\":\"d\",\"links\":null}")]
    [InlineData("{\"server_version\":\"x\",\"api_major\":2,\"contract_digest\":\"d\",\"links\":{\"openapi\":\"o\"}}")]
    [InlineData("{\"server_version\":null,\"api_major\":2,\"contract_digest\":\"d\",\"links\":{\"openapi\":\"o\",\"capabilities\":\"c\"}}")]
    public async Task MalformedOrIncompatibleDiscoveryIsNotAReconnect(string body)
    {
        using var monitor = new ServerReachabilityMonitor(Server, new StubHandler().Enqueue(body));
        Assert.Equal(ServerReachabilityStatus.Unreachable, (await monitor.RetryNowAsync()).Status);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("io")]
    [InlineData("timeout")]
    public async Task NetworkFailuresAreUnreachableNotSessionFailures(string failure)
    {
        using var monitor = new ServerReachabilityMonitor(Server, new FailureHandler(failure));
        Assert.Equal(ServerReachabilityStatus.Unreachable, (await monitor.RetryNowAsync()).Status);
    }

    [Fact]
    public async Task CallerCancellationDoesNotPublishAnOfflineVerdict()
    {
        using var monitor = new ServerReachabilityMonitor(Server, new StubHandler());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RetryNowAsync(canceled.Token));
        Assert.Equal(ServerReachabilityStatus.Unknown, monitor.State.Status);
        Assert.Null(monitor.State.LastCheckedAt);
    }

    [Fact]
    public async Task ForegroundProbesImmediatelyThenUsesTwentyOrEightSecondCadence()
    {
        var calls = 0;
        var reachable = true;
        var delays = new ManualDelays();
        using var monitor = new ServerReachabilityMonitor(_ => { calls++; return Task.FromResult(reachable); }, delay: delays.WaitAsync);
        Assert.Equal(0, calls);
        monitor.StartForeground();
        var first = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(20), first.Interval);
        Assert.Equal(1, calls);
        monitor.StartForeground();
        Assert.Equal(1, calls);

        reachable = false;
        first.Tick.SetResult();
        var second = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(8), second.Interval);
        Assert.Equal(ServerReachabilityStatus.Unreachable, monitor.State.Status);
        Assert.Equal(2, calls);

        reachable = true;
        second.Tick.SetResult();
        var third = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(20), third.Interval);
        Assert.Equal(ServerReachabilityStatus.Reachable, monitor.State.Status);
        Assert.Equal(3, calls);
        monitor.StopForeground();
        Assert.Equal(ServerReachabilityStatus.Unknown, monitor.State.Status);
        // Stop cancels this wait; releasing the old interval cannot dispatch another probe.
        third.Tick.SetResult();
        monitor.StartForeground();
        await delays.NextAsync();
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task NewestManualRetrySupersedesTheInflightResultAndSkipsOlderQueuedRetries()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var monitor = new ServerReachabilityMonitor(_ => ++calls == 1 ? release.Task : Task.FromResult(true));
        var published = new List<ServerReachabilityStatus>();
        monitor.StateChanged += (_, _) => published.Add(monitor.State.Status);
        var first = monitor.RetryNowAsync();
        var second = monitor.RetryNowAsync();
        var third = monitor.RetryNowAsync();
        Assert.Equal(1, calls);
        release.SetResult(false);
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
        Assert.Equal([ServerReachabilityStatus.Reachable], published);
    }

    [Fact]
    public async Task LateResponseAfterStopCannotRestoreStateAndResumeStartsFresh()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var delays = new ManualDelays();
        using var monitor = new ServerReachabilityMonitor(_ => ++calls == 1 ? release.Task : Task.FromResult(false), delay: delays.WaitAsync);
        var late = monitor.RetryNowAsync();
        monitor.StopForeground();
        release.SetResult(true);
        Assert.Equal(ServerReachabilityStatus.Unknown, (await late).Status);
        Assert.Null(monitor.State.LastCheckedAt);
        monitor.StartForeground();
        var next = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(8), next.Interval);
        Assert.Equal(ServerReachabilityStatus.Unreachable, monitor.State.Status);
    }

    [Fact]
    public async Task DisposedServerCannotPublishAfterTheReplacementServerConnects()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var old = new ServerReachabilityMonitor(_ => release.Task);
        var pending = old.RetryNowAsync();
        old.Dispose();
        using var replacement = new ServerReachabilityMonitor(_ => Task.FromResult(true));
        await replacement.RetryNowAsync();
        release.SetResult(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(ServerReachabilityStatus.Unknown, old.State.Status);
        Assert.Equal(ServerReachabilityStatus.Reachable, replacement.State.Status);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailureHandler(string failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw failure switch
            {
                "connection" => new HttpRequestException("Discovery connection failed"),
                "io" => new IOException("Discovery stream closed"),
                _ => new TaskCanceledException("Discovery timed out"),
            };
    }

    private sealed class ManualDelays
    {
        private readonly Channel<(TimeSpan Interval, TaskCompletionSource Tick)> _pending =
            Channel.CreateUnbounded<(TimeSpan, TaskCompletionSource)>();

        public async Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Writer.TryWrite((interval, tick));
            await tick.Task.WaitAsync(cancellationToken);
        }

        public Task<(TimeSpan Interval, TaskCompletionSource Tick)> NextAsync() =>
            _pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
