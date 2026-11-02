using System.Net;
using Bale.Core.Auth;

namespace Bale.Core.Tests;

public class ServerBrandingTests
{
    [Fact]
    public async Task PublicBrandingIncludesTheResolvedMarkWithoutCredentials()
    {
        var stub = new StubHandler().Enqueue("""{"server_name":" Family Media ","mark_url":" /assets/mark.png "}""");

        var branding = await ServerBranding.FetchAsync(new Uri("https://media.example.com/silo/"), transport: stub);

        Assert.Equal("Family Media", branding!.ServerName);
        Assert.Equal("https://media.example.com/silo/assets/mark.png", branding.MarkUrl!.AbsoluteUri);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("https://media.example.com/silo/api/v2/theme/branding", request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData("assets/mark.png", "https://media.example.com/silo/assets/mark.png")]
    [InlineData("/assets/mark.png", "https://media.example.com/silo/assets/mark.png")]
    [InlineData("https://media.example.com:443/mark.png", "https://media.example.com/mark.png")]
    [InlineData("https://MEDIA.EXAMPLE.COM/mark.png", "https://media.example.com/mark.png")]
    public void ResolvesOnlySameOriginHttpAssets(string value, string expected)
    {
        Assert.Equal(expected, ServerBranding.ResolveAsset(new Uri("https://media.example.com/silo/"), value)!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("https://other.example.com/mark.png")]
    [InlineData("http://media.example.com/mark.png")]
    [InlineData("https://media.example.com:8443/mark.png")]
    [InlineData("file:///mark.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("https://user:password@media.example.com/mark.png")]
    public void DropsUntrustedOrMissingAssets(string? value)
    {
        Assert.Null(ServerBranding.ResolveAsset(new Uri("https://media.example.com/silo/"), value));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"mark_url\":null}")]
    [InlineData("{\"mark_url\":42}")]
    [InlineData("{\"mark_url\":\"https://other.example.com/mark.png\"}")]
    public async Task MissingOrRejectedMarkKeepsTheNameFallback(string document)
    {
        var stub = new StubHandler().Enqueue(document);
        var server = new SignInServer(new Uri("https://media.example.com/"), "Family Media",
            await ServerBranding.FetchAsync(new Uri("https://media.example.com/"), transport: stub));

        Assert.Null(server.MarkUrl);
        Assert.Equal("Family Media", server.Name);
        Assert.Equal("F", server.Initial);
    }

    [Fact]
    public async Task UnavailableBrandingDoesNotBlockTheFallback()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.NotFound, "{}");

        Assert.Null(await ServerBranding.FetchAsync(new Uri("https://media.example.com/"), transport: stub));
    }
}
