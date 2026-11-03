using System.Net;
using System.Text.Json;
using Bale.Core.Auth;
using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class PasswordSignInTests
{
    private static readonly ClientIdentity Identity = new("test", "Test", "test", "Bale");

    [Fact]
    public void BlankOnlyValidationAndSubmissionGateMatchTvRules()
    {
        var value = new string('x', 1);
        Assert.Equal("Username is required", AuthInputRules.ValidationError(" \t", ""));
        Assert.Equal("Password is required", AuthInputRules.ValidationError(value, "\n "));
        Assert.Null(AuthInputRules.ValidationError(value, value));
        Assert.True(AuthInputRules.CanSubmit(value, value));
        Assert.False(AuthInputRules.CanSubmit(value, value, isLoading: true));
        Assert.False(AuthInputRules.CanSubmit(value, " "));
    }

    [Theory]
    [InlineData(401, null, "Incorrect username or password.")]
    [InlineData(403, null, "Account is disabled")]
    [InlineData(429, null, "Too many attempts. Wait a moment, then try again.")]
    [InlineData(403, "local_login_disabled", "Password sign-in is turned off on this server. Use your phone instead.")]
    [InlineData(403, "not_permitted", "Your account isn't allowed to use this server.")]
    [InlineData(401, "password_expired", "Your password has expired. Change it, then sign in again.")]
    [InlineData(503, "provider_unavailable", "The sign-in service can't be reached right now. Try again later.")]
    [InlineData(403, "account_required", "You don't have an account on this server yet. Ask an admin to add you.")]
    public void MapsProblemIdentifiersBeforeHttpStatus(int status, string? problem, string expected) =>
        Assert.Equal(expected, AuthInputRules.FailureMessage(status, problem));

    [Fact]
    public async Task PasswordIsSentExactlyOnceWithoutCredentialTransformationAndReturnsUnadoptedTokens()
    {
        var access = Guid.NewGuid().ToString();
        var refresh = Guid.NewGuid().ToString();
        var username = " viewer ";
        var password = " " + Guid.NewGuid() + " ";
        var handler = new StubHandler().Enqueue(JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh, expires_in = 3600 }));
        using var flow = new PasswordSignIn(new Uri("https://example.com/prefix"), Identity, handler);
        var result = await flow.SubmitAsync(username, password, new SignInOptions());
        Assert.Null(result.Error);
        Assert.Equal(access, result.Tokens!.AccessToken);
        Assert.Equal(refresh, result.Tokens.RefreshToken);
        Assert.Equal("/prefix/api/v2/auth/login", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
        Assert.Null(handler.Requests[0].Headers.Authorization);
        using var body = JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.Equal(username, body.RootElement.GetProperty("username").GetString());
        Assert.Equal(password, body.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task UncertainServerFailureNeverRetriesLogin()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.ServiceUnavailable, "{}");
        using var flow = new PasswordSignIn(new Uri("https://example.com"), Identity, handler);
        var result = await flow.SubmitAsync("viewer", Guid.NewGuid().ToString(), new SignInOptions());
        Assert.Null(result.Tokens);
        Assert.Equal("The sign-in service can't be reached right now. Try again later.", result.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PasswordDisabledAndOauthOnlyDiscoveryHaveOfficialGates()
    {
        var handler = new StubHandler().Enqueue("""{"password_login":false,"items":[{"id":"oauth","mode":"oauth","display_name":"Example","default":true}]}""")
            .Enqueue(HttpStatusCode.Forbidden, """{"type":"https://example.com/problems/local_login_disabled","status":403}""");
        using var flow = new PasswordSignIn(new Uri("https://example.com"), Identity, handler);
        var options = await flow.GetOptionsAsync();
        Assert.False(options.PasswordAvailable);
        Assert.Equal("Example", options.ProviderName);
        var result = await flow.SubmitAsync("viewer", Guid.NewGuid().ToString(), options);
        Assert.True(result.PasswordTurnedOff);
        Assert.Equal("Password sign-in is turned off on this server. Use your phone instead.", result.Error);
    }
}
