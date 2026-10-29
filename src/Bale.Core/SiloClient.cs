using Bale.Core.Api;
using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Bale.Core.Connection;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core;

/// <summary>
/// One Silo server plus the signed-in session on it. Requests made through <see cref="Api"/>
/// carry the current access token automatically.
/// </summary>
public sealed class SiloClient : IDisposable
{
    private readonly SiloConnection _connection;

    public SiloClient(Uri server, ClientIdentity identity, HttpMessageHandler? transport = null, TimeProvider? time = null)
    {
        _connection = new SiloConnection(server, identity, () => AccessToken, transport);
        DeviceLogin = new DeviceLoginFlow(_connection.Api, time);
    }

    public Uri Server => _connection.Server;

    public SiloApiClient Api => _connection.Api;

    public DeviceLoginFlow DeviceLogin { get; }

    public string? AccessToken { get; private set; }

    /// <summary>Persist this to resume the session on next launch. It rotates on every refresh.</summary>
    public string? RefreshToken { get; private set; }

    public Account? Account { get; private set; }

    public bool IsSignedIn => AccessToken is not null;

    /// <summary>Adopts the tokens from an approved device login or a password login.</summary>
    public void SignIn(TokenPair tokens)
    {
        AccessToken = tokens.AccessToken;
        RefreshToken = tokens.RefreshToken;
        Account = tokens.User;
    }

    /// <summary>
    /// Resumes a saved session. Returns false when the server no longer accepts the refresh token,
    /// in which case the user has to pair again.
    /// </summary>
    public async Task<bool> ResumeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        RefreshedTokens? refreshed;
        try
        {
            refreshed = await Api.Api.V2.Auth.Refresh.PostAsync(
                new RefreshSessionInputBody { RefreshToken = refreshToken },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.ResponseStatusCode is 401 or 403)
        {
            return false;
        }

        if (refreshed?.AccessToken is null)
        {
            return false;
        }

        AccessToken = refreshed.AccessToken;
        RefreshToken = refreshed.RefreshToken ?? refreshToken;
        Account = await Api.Api.V2.Account.Me.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Ends the session on the server (best effort) and forgets it locally.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (IsSignedIn)
        {
            try
            {
                await Api.Api.V2.Auth.Logout.PostAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException)
            {
                // Already gone server-side; forgetting it locally is what matters.
            }
        }

        AccessToken = null;
        RefreshToken = null;
        Account = null;
    }

    public void Dispose() => _connection.Dispose();
}
