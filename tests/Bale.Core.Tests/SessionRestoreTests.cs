using System.Net;
using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Tests;

public class SessionRestoreTests
{
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Offline");
    }

    private static SiloClient Create(HttpMessageHandler transport) =>
        new(new Uri("https://silo.example/"), new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), transport);

    [Fact]
    public async Task RestoreIsLocalAndTheFirstRequestUsesSavedTokensAndPinProof()
    {
        var stub = new StubHandler().Enqueue("""{"items":[]}""");
        using var client = Create(stub);
        client.RestoreSession("rt", "at", DateTimeOffset.UtcNow.AddHours(1), "p", "proof");

        Assert.Empty(stub.Requests);
        Assert.True(client.IsSignedIn);
        await client.GetProfilesAsync();

        var request = Assert.Single(stub.Requests);
        Assert.Equal("Bearer at", request.Headers.Authorization!.ToString());
        Assert.Equal("p", Assert.Single(request.Headers.GetValues("X-Profile-Id")));
        Assert.Equal("proof", Assert.Single(request.Headers.GetValues("X-Profile-Token")));
    }

    [Fact]
    public async Task LegacyRefreshOnlySessionRefreshesOnItsFirstRequestNotOnRestore()
    {
        var stub = new StubHandler()
            .Enqueue("""{"access_token":"at2","refresh_token":"rt2","expires_in":3600}""")
            .Enqueue("""{"items":[]}""");
        using var client = Create(stub);
        client.RestoreSession("rt");
        Assert.Empty(stub.Requests);

        await client.GetProfilesAsync();

        Assert.Equal("rt2", client.RefreshToken);
        Assert.EndsWith("/api/v2/auth/refresh", stub.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("Bearer at2", stub.Requests[1].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Refresh401ClearsAllCredentialsAndReportsExpiryOnce()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, """{"title":"Unauthorized","status":401}""");
        using var client = Create(stub);
        client.RestoreSession("rt", "at", DateTimeOffset.UtcNow.AddMinutes(-1), "p", "proof");
        var expired = 0;
        client.SessionExpired += (_, _) =>
        {
            Assert.False(client.IsSignedIn);
            Assert.Null(client.ProfileToken);
            expired++;
        };

        await Assert.ThrowsAnyAsync<ApiException>(() => client.GetProfilesAsync());
        await Assert.ThrowsAnyAsync<ApiException>(() => client.GetProfilesAsync());

        Assert.Equal(1, expired);
        Assert.Single(stub.Requests);
        Assert.Null(client.AccessToken);
        Assert.Null(client.RefreshToken);
        Assert.Null(client.ProfileId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Non401RefreshErrorsKeepTheSession(HttpStatusCode status)
    {
        var stub = new StubHandler().Enqueue(status, """{"title":"Unavailable"}""");
        using var client = Create(stub);
        client.RestoreSession("rt");
        var expired = 0;
        client.SessionExpired += (_, _) => expired++;

        await Assert.ThrowsAnyAsync<ApiException>(() => client.GetProfilesAsync());

        Assert.Equal("rt", client.RefreshToken);
        Assert.True(client.IsSignedIn);
        Assert.Equal(0, expired);
    }

    [Fact]
    public async Task OfflineRefreshKeepsCredentialsForRetry()
    {
        using var client = Create(new OfflineHandler());
        client.RestoreSession("rt", "at", DateTimeOffset.UtcNow.AddMinutes(-1), "p", "proof");
        var expired = false;
        client.SessionExpired += (_, _) => expired = true;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetProfilesAsync());

        Assert.False(expired);
        Assert.Equal("rt", client.RefreshToken);
        Assert.Equal("at", client.AccessToken);
        Assert.Equal("proof", client.ProfileToken);
    }

    [Fact]
    public async Task SessionCanPairAgainAfterExpiry()
    {
        var stub = new StubHandler()
            .Enqueue(HttpStatusCode.Unauthorized, """{"title":"Unauthorized","status":401}""")
            .Enqueue("""{"state":"available"}""")
            .Enqueue("""{"items":[]}""");
        using var client = Create(stub);
        client.RestoreSession("rt");
        await Assert.ThrowsAnyAsync<ApiException>(() => client.GetProfilesAsync());

        await client.DeviceLogin.GetCapabilityAsync();
        Assert.Null(stub.Requests[1].Headers.Authorization);
        client.SignIn(new TokenPair { AccessToken = "new-at", RefreshToken = "new-rt", ExpiresIn = 3600 });
        await client.GetProfilesAsync();
        Assert.Equal("Bearer new-at", stub.Requests[2].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task ClearingSelectionAlsoClearsThePreviousPinProof()
    {
        var stub = new StubHandler().Enqueue("""{"items":[]}""");
        using var client = Create(stub);
        client.RestoreSession("rt", "at", DateTimeOffset.UtcNow.AddHours(1), "p", "proof");

        client.UseProfile(null);
        await client.GetProfilesAsync();

        Assert.Null(client.ProfileId);
        Assert.Null(client.ProfileToken);
        Assert.False(stub.Requests[0].Headers.Contains("X-Profile-Token"));
    }

    [Fact]
    public async Task SignOutWhileOfflineStillForgetsTokens()
    {
        using var client = Create(new OfflineHandler());
        client.RestoreSession("rt", "at", DateTimeOffset.UtcNow.AddHours(1), "p", "proof");

        await client.SignOutAsync();

        Assert.False(client.IsSignedIn);
        Assert.Null(client.ProfileToken);
    }
}
