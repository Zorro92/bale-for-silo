using System.Net;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class ServerSetupProbeTests
{
    private const string Info = """{"api_major":2,"server_version":"2","contract_digest":"test","links":{"openapi":"/openapi","capabilities":"/capabilities"}}""";

    [Theory]
    [InlineData("404 page not found", ServerContractOutcome.UpdateRequired)]
    [InlineData("404 page not found\n", ServerContractOutcome.UpdateRequired)]
    [InlineData("404 page not found\n\n", ServerContractOutcome.UnexpectedStatus)]
    [InlineData(" 404 page not found", ServerContractOutcome.UnexpectedStatus)]
    [InlineData("<html>404</html>", ServerContractOutcome.UnexpectedStatus)]
    public void OnlyExactLegacy404IsAnUpdateVerdict(string body, ServerContractOutcome expected) =>
        Assert.Equal(expected, ServerSetupProbe.ClassifyContract(404, body));

    [Fact]
    public void ContractMustBeValidV2InfoNotProxyHtmlOrPartialJson()
    {
        Assert.Equal(ServerContractOutcome.V2, ServerSetupProbe.ClassifyContract(200, Info));
        Assert.Equal(ServerContractOutcome.MalformedResponse, ServerSetupProbe.ClassifyContract(200, "<html>"));
        Assert.Equal(ServerContractOutcome.MalformedResponse, ServerSetupProbe.ClassifyContract(200, """{"api_major":2}"""));
        Assert.Equal(ServerContractOutcome.Authentication, ServerSetupProbe.ClassifyContract(403, ""));
        Assert.Equal(ServerContractOutcome.RateLimited, ServerSetupProbe.ClassifyContract(429, ""));
        Assert.Equal(ServerContractOutcome.ServerError, ServerSetupProbe.ClassifyContract(503, ""));
    }

    [Fact]
    public async Task ProbeFallsBackInOrderWithoutAuthOrDeviceHeadersThenReturnsV2SetupAndIdentity()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.BadGateway, "{}").Enqueue(HttpStatusCode.BadGateway, "{}")
            .Enqueue(Info).Enqueue("""{"needs_setup":false,"wizard_completed":true}""")
            .Enqueue("""{"enabled":true}""").Enqueue("""{"server_id":"deployment"}""");
        var logs = new List<string>();
        using var probe = new ServerSetupProbe(handler, logs.Add);
        var result = await probe.ProbeAsync(ServerSetupAddress.Candidates("example.com/prefix"));
        Assert.Equal("http://example.com/prefix", ServerAddress.ToBaseUrl(result.Server!));
        Assert.True(result.SignupEnabled);
        Assert.Equal("deployment", result.VerifiedServerId);
        Assert.Null(result.Error);
        Assert.All(handler.Requests, request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Profile-Id"));
            Assert.False(request.Headers.Contains("X-Silo-Device-Id"));
            Assert.True(request.Headers.CacheControl!.NoCache);
            Assert.True(request.Headers.CacheControl.NoStore);
        });
        Assert.Equal("https", handler.Requests[0].RequestUri!.Scheme);
        Assert.Equal("http", handler.Requests[2].RequestUri!.Scheme);
        Assert.All(logs, log => Assert.DoesNotContain("example.com", log));
    }

    [Fact]
    public async Task SuccessfulV2SetupSupersedesTransientContractFailureAndFreshSetupDoesNotFetchSignup()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.ServiceUnavailable, "{}")
            .Enqueue("""{"needs_setup":true,"wizard_completed":false}""");
        using var probe = new ServerSetupProbe(handler);
        var result = await probe.ProbeAsync([new Uri("https://example.com")]);
        Assert.True(result.NeedsSetup);
        Assert.NotNull(result.Server);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UpdateVerdictStopsBeforeSetupAndDoesNotTryAnotherScheme()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.NotFound, "404 page not found\n");
        using var probe = new ServerSetupProbe(handler);
        var result = await probe.ProbeAsync(ServerSetupAddress.Candidates("example.com"));
        Assert.Equal(ServerSetupProbe.UpdateRequired, result.Error);
        Assert.Null(result.Server);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CallerCancellationIsNotAnOfflineResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var probe = new ServerSetupProbe(new StubHandler());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync([new Uri("https://example.com")], cancellation.Token));
    }
}
