using System.Net;
using System.Text;
using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Tests;

public class SessionExpiryTests
{
    private const string Unauthorized = """{"title":"Unauthorized","status":401}""";
    private const string Refreshed = """{"access_token":"new-at","refresh_token":"new-rt","expires_in":3600}""";

    private static SiloClient Restore(HttpMessageHandler transport)
    {
        var client = new SiloClient(new Uri("https://media.example.com/"), new ClientIdentity("d", "Test Xbox", "xbox", "Bale"), transport);
        client.RestoreSession("old-rt", "old-at", DateTimeOffset.UtcNow.AddHours(1), "p", "proof");
        return client;
    }

    [Theory]
    [InlineData("profiles")]
    [InlineData("account")]
    [InlineData("home")]
    [InlineData("search")]
    [InlineData("detail")]
    [InlineData("calendar")]
    public async Task ARejectedRefreshReportsExpiryFromEveryApiSurface(string surface)
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, Unauthorized)
            .Enqueue(HttpStatusCode.Unauthorized, Unauthorized);
        using var client = Restore(stub);
        var expired = 0;
        client.SessionExpired += (_, _) =>
        {
            Assert.False(client.IsSignedIn);
            Assert.Null(client.ProfileId);
            Assert.Null(client.ProfileToken);
            expired++;
        };

        await Assert.ThrowsAnyAsync<ApiException>(() => ReadAsync(client, surface));
        await Assert.ThrowsAnyAsync<ApiException>(() => client.GetProfilesAsync());

        Assert.Equal(1, expired);
        Assert.Equal(2, stub.Requests.Count);
        Assert.EndsWith("/api/v2/auth/refresh", stub.Requests[1].RequestUri!.AbsolutePath);
        Assert.Null(stub.Requests[1].Headers.Authorization);
    }

    [Fact]
    public async Task ARejectedBearerRefreshesAndRetriesWithoutSigningOut()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, Unauthorized)
            .Enqueue(Refreshed).Enqueue("""{"items":[]}""");
        using var client = Restore(stub);
        var expired = false;
        client.SessionExpired += (_, _) => expired = true;

        await client.GetProfilesAsync();

        Assert.False(expired);
        Assert.Equal("new-rt", client.RefreshToken);
        Assert.Equal("Bearer old-at", stub.Requests[0].Headers.Authorization!.ToString());
        var retry = stub.Requests[2];
        Assert.Equal(stub.Requests[0].RequestUri, retry.RequestUri);
        Assert.Equal("Bearer new-at", retry.Headers.Authorization!.ToString());
        Assert.Equal("p", Assert.Single(retry.Headers.GetValues("X-Profile-Id")));
        Assert.Equal("proof", Assert.Single(retry.Headers.GetValues("X-Profile-Token")));
        Assert.Equal("d", Assert.Single(retry.Headers.GetValues("X-Silo-Device-Id")));
    }

    [Fact]
    public async Task APostRetryPreservesItsBodyAndHeaders()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, Unauthorized)
            .Enqueue(Refreshed).Enqueue("""{"valid":true,"profile_token":"new-proof"}""");
        using var client = Restore(stub);

        Assert.True(await client.VerifyPinAsync("p", "1234"));

        Assert.Equal(HttpMethod.Post, stub.Requests[2].Method);
        Assert.Equal(stub.RequestBodies[0], stub.RequestBodies[2]);
        Assert.Contains("1234", stub.RequestBodies[2]);
        Assert.Equal(stub.Requests[0].Content!.Headers.ContentType, stub.Requests[2].Content!.Headers.ContentType);
        Assert.Equal("new-proof", client.ProfileToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ATransientOrNonAuthRefreshFailureKeepsTheSession(HttpStatusCode status)
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, Unauthorized)
            .Enqueue(status, """{"title":"Unavailable"}""");
        using var client = Restore(stub);
        var expired = false;
        client.SessionExpired += (_, _) => expired = true;

        await Assert.ThrowsAnyAsync<ApiException>(() => client.GetProfilesAsync());

        Assert.False(expired);
        Assert.Equal("old-rt", client.RefreshToken);
        Assert.Equal("proof", client.ProfileToken);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task ASecond401DoesNotLoopOrGuessThatTheRefreshTokenIsInvalid()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, Unauthorized)
            .Enqueue(Refreshed).Enqueue(HttpStatusCode.Unauthorized, Unauthorized);
        using var client = Restore(stub);
        var expired = false;
        client.SessionExpired += (_, _) => expired = true;

        await Assert.ThrowsAnyAsync<ApiException>(() => client.GetProfilesAsync());

        Assert.False(expired);
        Assert.Equal("new-rt", client.RefreshToken);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task Concurrent401sShareOneRefresh()
    {
        var transport = new ConcurrentRejectionHandler();
        using var client = Restore(transport);

        await Task.WhenAll(client.GetProfilesAsync(), client.GetProfilesAsync());

        Assert.Equal(1, transport.RefreshCount);
        Assert.Equal("new-rt", client.RefreshToken);
    }

    [Fact]
    public async Task SigningOutWhileARefreshIsInFlightCannotResurrectTheSession()
    {
        var transport = new CallbackRefreshHandler();
        using var signingOut = Restore(transport);
        transport.DuringRefresh = signingOut.ForgetSession;
        var expired = false;
        signingOut.SessionExpired += (_, _) => expired = true;

        await Assert.ThrowsAnyAsync<ApiException>(() => signingOut.GetProfilesAsync());

        Assert.False(signingOut.IsSignedIn);
        Assert.Null(signingOut.ProfileToken);
        Assert.False(expired);
        Assert.Equal(2, transport.RequestCount);
    }

    [Fact]
    public async Task ADeviceCodeRequestDoesNotSpendOrReplaceTheSavedCredentials()
    {
        var stub = new StubHandler().Enqueue("""{"state":"available"}""");
        using var client = Restore(stub);

        await client.DeviceLogin.GetCapabilityAsync();

        Assert.Null(Assert.Single(stub.Requests).Headers.Authorization);
        Assert.Equal("old-at", client.AccessToken);
        Assert.Equal("old-rt", client.RefreshToken);
        Assert.Equal("p", client.ProfileId);
        Assert.Equal("proof", client.ProfileToken);
    }

    private sealed class CallbackRefreshHandler : HttpMessageHandler
    {
        public Action? DuringRefresh { get; set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var refresh = request.RequestUri!.AbsolutePath.EndsWith("/auth/refresh", StringComparison.Ordinal);
            if (refresh) DuringRefresh?.Invoke();
            return Task.FromResult(new HttpResponseMessage(refresh ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            {
                RequestMessage = request,
                Content = new StringContent(refresh ? Refreshed : Unauthorized, Encoding.UTF8,
                    refresh ? "application/json" : "application/problem+json"),
            });
        }
    }

    private static Task ReadAsync(SiloClient client, string surface) => surface switch
    {
        "account" => client.GetAccountAsync(),
        "home" => client.Api.Api.V2.Home.Sections.GetAsync(),
        "search" => client.Api.Api.V2.Catalog.Query.PostAsync(new CatalogQuery()),
        "detail" => client.Api.Api.V2.Catalog.Items["item"].GetAsync(),
        "calendar" => client.Api.Api.V2.Calendar.GetAsync(),
        _ => client.GetProfilesAsync(),
    };

    private sealed class ConcurrentRejectionHandler : HttpMessageHandler
    {
        public int RefreshCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var refresh = request.RequestUri!.AbsolutePath.EndsWith("/auth/refresh", StringComparison.Ordinal);
            if (refresh)
            {
                Interlocked.Increment(ref RefreshCount);
                await Task.Yield();
            }
            var rejected = !refresh && request.Headers.Authorization?.Parameter == "old-at";
            return new HttpResponseMessage(rejected ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(refresh ? Refreshed : rejected ? Unauthorized : """{"items":[]}""", Encoding.UTF8,
                    rejected ? "application/problem+json" : "application/json"),
            };
        }
    }
}
