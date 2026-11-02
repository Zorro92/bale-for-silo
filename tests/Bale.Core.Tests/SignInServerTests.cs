using System.Net;
using Bale.Core.Auth;

namespace Bale.Core.Tests;

public class SignInServerTests
{
    [Theory]
    [InlineData("https://media.example.com/", "  Family Media  ", "Family Media", "media.example.com", true)]
    [InlineData("https://media.example.com:8443/silo/", null, "media.example.com:8443", "media.example.com:8443", true)]
    [InlineData("http://media.example.com/", " ", "media.example.com", "media.example.com", false)]
    public void UsesTheSavedNameOrHostAndMarksOnlyHttpsSecure(string address, string? savedName, string name, string host, bool secure)
    {
        var server = new SignInServer(new Uri(address), savedName);

        Assert.Equal(name, server.Name);
        Assert.Equal(host, server.Address);
        Assert.Equal(secure, server.IsSecure);
    }

    [Fact]
    public async Task ReadsThePublicBrandingWithoutCredentialsAndPreservesTheServerPath()
    {
        var stub = new StubHandler().Enqueue("""{"server_name":"  Family Media  ","accent_color":"#123456"}""");

        var name = await SignInServer.FetchNameAsync(new Uri("https://media.example.com/silo/"), transport: stub);

        Assert.Equal("Family Media", name);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("https://media.example.com/silo/api/v2/theme/branding", request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"server_name\":\" \"}")]
    [InlineData("{\"server_name\":null}")]
    [InlineData("{\"server_name\":42}")]
    [InlineData("[]")]
    [InlineData("not JSON")]
    public async Task MissingOrMalformedBrandingLeavesTheSavedNameAvailable(string json)
    {
        var stub = new StubHandler().Enqueue(json);

        Assert.Null(await SignInServer.FetchNameAsync(new Uri("https://media.example.com/"), transport: stub));
    }

    [Fact]
    public async Task AnUnavailableBrandingEndpointIsOptional()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.NotFound, "{}");

        Assert.Null(await SignInServer.FetchNameAsync(new Uri("https://media.example.com/"), transport: stub));
    }

    [Fact]
    public async Task OfflineBrandingIsOptional()
    {
        Assert.Null(await SignInServer.FetchNameAsync(new Uri("https://media.example.com/"), transport: new UnavailableHandler()));
    }

    [Fact]
    public async Task LeavingTheScreenCancelsBranding()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SignInServer.FetchNameAsync(
            new Uri("https://media.example.com/"), cancellation.Token, new UnavailableHandler()));
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("Offline");
        }
    }
}
