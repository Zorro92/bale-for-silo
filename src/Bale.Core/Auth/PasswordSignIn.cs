using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace Bale.Core.Auth;

/// <summary>TvAuthInputRules.kt / TvLoginViewModel.kt: blank checks only; never trim or transform credentials.</summary>
public static class AuthInputRules
{
    public static bool CanSubmit(string username, string password, bool isLoading = false) =>
        !isLoading && !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);

    public static string? ValidationError(string username, string password) =>
        string.IsNullOrWhiteSpace(username) ? "Username is required" : string.IsNullOrWhiteSpace(password) ? "Password is required" : null;

    /// <summary>TvLoginViewModel.kt tvLoginError / strings.xml. Problem identifiers precede HTTP fallbacks.</summary>
    public static string FailureMessage(int status, string? problem, string? providerName = null, string? directoryName = null, string? detail = null)
    {
        var external = directoryName ?? "the sign-in provider";
        return problem switch
        {
            "local_login_disabled" => "Password sign-in is turned off on this server. Use your phone instead.",
            "not_permitted" => "Your account isn't allowed to use this server.",
            "password_expired" => "Your password has expired. Change it, then sign in again.",
            "provider_unavailable" => "The sign-in service can't be reached right now. Try again later.",
            "email_in_use" => $"An account with this email already exists. Ask an admin to connect it to {external}.",
            "identity_linked_elsewhere" => $"That {external} account is already connected to another account.",
            "account_required" => "You don't have an account on this server yet. Ask an admin to add you.",
            "identity_changed" => "The account or server changed. Start sign-in again.",
            "rate_limited" => "Too many attempts. Wait a moment, then try again.",
            _ => status switch
            {
                429 => "Too many attempts. Wait a moment, then try again.",
                401 => providerName is null ? "Incorrect username or password."
                    : $"Incorrect username or password. If you sign in with {providerName}, use your phone instead.",
                403 => "Account is disabled",
                503 => "The sign-in service can't be reached right now. Try again later.",
                _ => string.IsNullOrWhiteSpace(detail) ? "Login failed" : detail,
            },
        };
    }
}

public sealed record PasswordSignInResult(TokenPair? Tokens, string? Error, bool PasswordTurnedOff = false);
public sealed record SignInOptions(bool PasswordAvailable = true, string? ProviderName = null, string? DirectoryName = null);

/// <summary>TvLoginViewModel.kt: fetch tokens on an isolated anonymous client; the caller owns session adoption.</summary>
public sealed class PasswordSignIn : IDisposable
{
    private readonly SiloConnection _connection;

    public PasswordSignIn(Uri server, ClientIdentity identity, HttpMessageHandler? transport = null)
    {
        // A 307 must not forward a password to a different origin, even on a trusted LAN.
        _connection = new SiloConnection(server, identity, transport: transport ?? new HttpClientHandler { AllowAutoRedirect = false });
    }

    public async Task<SignInOptions> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var providers = await _connection.Api.Api.V2.Auth.Providers.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SignInOptions(providers?.PasswordLogin ?? true,
                providers?.Items?.FirstOrDefault(p => p.Mode == "oauth")?.DisplayName,
                providers?.Items?.FirstOrDefault(p => p.Mode == "credentials" && p.Id != "local")?.DisplayName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or OperationCanceledException)
        {
            // TvLoginViewModel.kt retains the default password offer when discovery is temporarily unavailable.
            return new SignInOptions();
        }
    }

    public async Task<PasswordSignInResult> SubmitAsync(string username, string password, SignInOptions options, CancellationToken cancellationToken = default)
    {
        if (AuthInputRules.ValidationError(username, password) is { } error) return new(null, error);
        try
        {
            // openapi.json login: non_retryable. An uncertain sign-in must never create several sessions.
            var tokens = await _connection.Api.Api.V2.Auth.Login.PostAsync(new LoginInputBody { Username = username, Password = password },
                config => config.Options.Add(new RetryHandlerOption { MaxRetry = 0 }), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return string.IsNullOrWhiteSpace(tokens?.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken)
                ? new(null, "Login failed") : new(tokens, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ApiException ex)
        {
            var problem = ex as Problem;
            var code = problem?.Type?.Split('/').LastOrDefault();
            return new(null, AuthInputRules.FailureMessage(ex.ResponseStatusCode, code, options.ProviderName, options.DirectoryName, problem?.Detail), code == "local_login_disabled");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new(null, "Network error. Check your connection.");
        }
    }

    public void Dispose() => _connection.Dispose();
}
