using System.Net;
using Bale.Core.Auth;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class DeviceLoginFlowTests
{
    private static readonly ClientIdentity Identity = new("device-1", "Test Xbox", "xbox", "Bale", "0.1.0", ClientFamily: "tv");

    private const string StartJson = """
        {"device_code":"dc-1","user_code":"4821-7730","match_code":"42","verification_uri":"https://silo.example.com/activate",
         "verification_uri_complete":"https://silo.example.com/activate?code=48217730","expires_at":"2099-01-01T00:00:00Z",
         "expires_in":600,"interval":5,"device_name":"Test Xbox","device_platform":"xbox","client_purpose":"device_login","temporary":false}
        """;

    private static string Poll(string status, bool opened = false, string tokens = "", string profileId = "") => $$"""
        {"status":"{{status}}","poll_after":3,"opened":{{(opened ? "true" : "false")}},"profile_id":"{{profileId}}","profile_token":"","temporary":false{{tokens}}}
        """;

    private const string Tokens = """
        ,"tokens":{"access_token":"at","refresh_token":"rt","expires_in":3600,
          "user":{"id":"u1","username":"fox","email":"fox@example.com","role":"user","permissions":[],"download_allowed":true,"password_change_required":false}}
        """;

    private static (DeviceLoginFlow Flow, StubHandler Stub, List<TimeSpan> Delays, SiloConnection Connection) Create()
    {
        var stub = new StubHandler();
        ServerAddress.TryParse("silo.example.com", out var server);
        var connection = new SiloConnection(server, Identity, transport: stub);
        var delays = new List<TimeSpan>();
        var flow = new DeviceLoginFlow(connection.Api, delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });
        return (flow, stub, delays, connection);
    }

    [Fact]
    public async Task StartSendsDeviceDetailsAndParsesTicket()
    {
        var (flow, stub, _, connection) = Create();
        using var _ = connection;
        stub.Enqueue(HttpStatusCode.Created, StartJson);

        var ticket = await flow.StartAsync("Test Xbox", "xbox");

        Assert.Equal("dc-1", ticket.DeviceCode);
        Assert.Equal("4821 7730", ticket.DisplayCode);
        Assert.Equal("silo.example.com/activate", ticket.ActivateText);
        Assert.Equal(TimeSpan.FromSeconds(5), ticket.Interval);

        var request = Assert.Single(stub.Requests);
        Assert.Equal("https://silo.example.com/api/v2/auth/device/start", request.RequestUri!.ToString());
        Assert.Equal("xbox", request.Headers.GetValues("X-Silo-Device-Platform").Single());
        Assert.Equal("tv", request.Headers.GetValues("X-Silo-Client-Family").Single());
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task PollsUntilApprovedAndHonorsPollAfter()
    {
        var (flow, stub, delays, connection) = Create();
        using var _ = connection;
        stub.Enqueue(HttpStatusCode.Created, StartJson)
            .Enqueue(Poll("pending"))
            .Enqueue(Poll("pending", opened: true))
            .Enqueue(Poll("approved", opened: true, tokens: Tokens, profileId: "p7"));

        var ticket = await flow.StartAsync("Test Xbox", "xbox");
        var openedCount = 0;
        var result = await flow.WaitForApprovalAsync(ticket, onOpened: () => openedCount++);

        Assert.Equal(DeviceLoginOutcome.Approved, result.Outcome);
        Assert.Equal("at", result.Tokens!.AccessToken);
        Assert.Equal("fox", result.Tokens.User!.Username);
        Assert.Equal("p7", result.ProfileId);
        Assert.Null(result.ProfileToken);
        Assert.Equal(1, openedCount);
        Assert.Equal(new[] { 5, 3, 3 }.Select(s => TimeSpan.FromSeconds(s)), delays);
    }

    [Theory]
    [InlineData("denied", DeviceLoginOutcome.Denied)]
    [InlineData("canceled", DeviceLoginOutcome.Canceled)]
    [InlineData("expired", DeviceLoginOutcome.Expired)]
    [InlineData("consumed", DeviceLoginOutcome.Expired)]
    public async Task StopsOnTerminalStatus(string status, DeviceLoginOutcome expected)
    {
        var (flow, stub, _, connection) = Create();
        using var _ = connection;
        stub.Enqueue(HttpStatusCode.Created, StartJson).Enqueue(Poll(status));

        var result = await flow.WaitForApprovalAsync(await flow.StartAsync("Test Xbox", "xbox"));

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task MissingPairingRowMeansExpired()
    {
        var (flow, stub, _, connection) = Create();
        using var _ = connection;
        stub.Enqueue(HttpStatusCode.Created, StartJson)
            .Enqueue(HttpStatusCode.NotFound, """{"title":"Not Found","status":404}""");

        var result = await flow.WaitForApprovalAsync(await flow.StartAsync("Test Xbox", "xbox"));

        Assert.Equal(DeviceLoginOutcome.Expired, result.Outcome);
    }

    [Fact]
    public async Task SendsBearerTokenAndProfileWhenSignedIn()
    {
        var stub = new StubHandler().Enqueue("""{"id":"u1","username":"fox","email":"","role":"user","permissions":[],"download_allowed":true,"password_change_required":false}""");
        ServerAddress.TryParse("silo.example.com", out var server);
        using var connection = new SiloConnection(server, Identity, _ => Task.FromResult<RequestCredentials?>(new("secret", "p1")), stub);

        var me = await connection.Api.Api.V2.Account.Me.GetAsync();

        Assert.Equal("fox", me!.Username);
        var request = stub.Requests.Single();
        Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());
        Assert.Equal("p1", request.Headers.GetValues("X-Profile-Id").Single());
        Assert.False(request.Headers.Contains("X-Profile-Token"));
    }
}
