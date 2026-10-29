using System.Net;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class SiloClientTests
{
    private const string AccountJson = """
        {"id":"u1","username":"fox","email":"","role":"user","permissions":[],"download_allowed":true,"password_change_required":false}
        """;

    private static SiloClient Create(StubHandler stub)
    {
        ServerAddress.TryParse("silo.example.com", out var server);
        return new SiloClient(server, new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), stub);
    }

    [Fact]
    public async Task ResumeRotatesRefreshTokenAndLoadsAccount()
    {
        var stub = new StubHandler()
            .Enqueue("""{"access_token":"at2","refresh_token":"rt2","expires_in":3600}""")
            .Enqueue(AccountJson);
        using var client = Create(stub);

        Assert.True(await client.ResumeAsync("rt1"));

        Assert.Equal("rt2", client.RefreshToken);
        Assert.Equal("fox", client.Account!.Username);
        Assert.Null(stub.Requests[0].Headers.Authorization);
        Assert.Equal("Bearer at2", stub.Requests[1].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task ResumeWithRevokedTokenReturnsFalse()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, """{"title":"Unauthorized","status":401}""");
        using var client = Create(stub);

        Assert.False(await client.ResumeAsync("revoked"));
        Assert.False(client.IsSignedIn);
    }

    [Fact]
    public async Task SignOutForgetsSessionEvenIfServerFails()
    {
        var stub = new StubHandler()
            .Enqueue("""{"access_token":"at","refresh_token":"rt","expires_in":3600}""")
            .Enqueue(AccountJson)
            .Enqueue(HttpStatusCode.InternalServerError, """{"title":"Oops","status":500}""");
        using var client = Create(stub);
        await client.ResumeAsync("rt");

        await client.SignOutAsync();

        Assert.False(client.IsSignedIn);
        Assert.Null(client.RefreshToken);
    }
}
