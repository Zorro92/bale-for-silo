using Bale.Core.Api;
using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Bale.Core.Connection;
using Bale.Core.Browse;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core;

/// <summary>
/// One Silo server plus the signed-in session on it. Requests made through <see cref="Api"/>
/// carry the current access token and acting profile automatically.
/// </summary>
public sealed class SiloClient : IDisposable
{
    /// <summary>Renew the access token this long before it expires.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly SiloConnection _connection;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTimeOffset? _accessTokenExpiresAt;

    public SiloClient(Uri server, ClientIdentity identity, HttpMessageHandler? transport = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _connection = new SiloConnection(server, identity, CurrentCredentialsAsync, transport);
        DeviceLogin = new DeviceLoginFlow(_connection.Api, time);
        Browse = new BrowseFeed(_connection.Api, server);
    }

    public Uri Server => _connection.Server;

    public SiloApiClient Api => _connection.Api;

    public DeviceLoginFlow DeviceLogin { get; }

    public BrowseFeed Browse { get; }

    public string? AccessToken { get; private set; }

    /// <summary>Persist this to resume the session on next launch. It rotates on every refresh.</summary>
    public string? RefreshToken { get; private set; }

    public Account? Account { get; private set; }

    /// <summary>The household profile browsing and playback act as. Null lets the server pick its default.</summary>
    public string? ProfileId { get; private set; }

    /// <summary>Proof of the PIN for a locked <see cref="ProfileId"/>. Short-lived; don't persist it.</summary>
    public string? ProfileToken { get; private set; }

    public bool IsSignedIn => AccessToken is not null;

    /// <summary>Raised when the refresh token rotates, so the app can save the new one.</summary>
    public event EventHandler? TokensRefreshed;

    /// <summary>Adopts the tokens from an approved device login or a password login.</summary>
    public void SignIn(TokenPair tokens, string? profileId = null, string? profileToken = null)
    {
        AccessToken = tokens.AccessToken;
        RefreshToken = tokens.RefreshToken;
        _accessTokenExpiresAt = ExpiresAt(tokens.ExpiresIn);
        Account = tokens.User;
        ProfileId = profileId;
        ProfileToken = profileToken;
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

        AdoptRefreshed(refreshed, refreshToken);
        Account = await Api.Api.V2.Account.Me.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The household profiles on this account, for a "Who's watching?" screen.</summary>
    public async Task<IReadOnlyList<Profile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await Api.Api.V2.Profiles.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return profiles?.Items ?? [];
    }

    /// <summary>Acts as <paramref name="profileId"/> from now on. Locked profiles need <see cref="VerifyPinAsync"/> instead.</summary>
    public void UseProfile(string profileId)
    {
        ProfileId = profileId;
        ProfileToken = null;
    }

    /// <summary>Checks a locked profile's PIN and, when it's right, acts as that profile.</summary>
    public async Task<bool> VerifyPinAsync(string profileId, string pin, CancellationToken cancellationToken = default)
    {
        ProfileVerification? verification;
        try
        {
            verification = await Api.Api.V2.Profiles[profileId].VerifyPin.PostAsync(
                new ProfilePINCheck { Pin = pin },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.ResponseStatusCode is 401 or 403)
        {
            return false;
        }

        if (verification?.Valid != true || string.IsNullOrEmpty(verification.ProfileToken))
        {
            return false;
        }

        ProfileId = profileId;
        ProfileToken = verification.ProfileToken;
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
        _accessTokenExpiresAt = null;
        Account = null;
        ProfileId = null;
        ProfileToken = null;
    }

    public void Dispose()
    {
        _connection.Dispose();
        _refreshLock.Dispose();
    }

    private async Task<RequestCredentials?> CurrentCredentialsAsync(CancellationToken cancellationToken)
    {
        if (AccessToken is null)
        {
            return null;
        }

        if (NeedsRefresh())
        {
            await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
        }

        return AccessToken is null ? null : new RequestCredentials(AccessToken, ProfileId, ProfileToken);
    }

    private bool NeedsRefresh() =>
        RefreshToken is not null && _accessTokenExpiresAt is { } expiresAt && _time.GetUtcNow() >= expiresAt - RefreshMargin;

    /// <summary>One refresh at a time; requests that queue behind it reuse its result.</summary>
    private async Task RefreshNowAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!NeedsRefresh())
            {
                return;
            }

            var current = RefreshToken!;
            try
            {
                var refreshed = await Api.Api.V2.Auth.Refresh.PostAsync(
                    new RefreshSessionInputBody { RefreshToken = current },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (refreshed?.AccessToken is not null)
                {
                    AdoptRefreshed(refreshed, current);
                }
            }
            catch (ApiException)
            {
                // Leave the old token in place; the request fails with 401 and the app sends the user to sign in.
                _accessTokenExpiresAt = null;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void AdoptRefreshed(RefreshedTokens refreshed, string previousRefreshToken)
    {
        AccessToken = refreshed.AccessToken;
        RefreshToken = refreshed.RefreshToken ?? previousRefreshToken;
        _accessTokenExpiresAt = ExpiresAt(refreshed.ExpiresIn);
        TokensRefreshed?.Invoke(this, EventArgs.Empty);
    }

    private DateTimeOffset? ExpiresAt(long? expiresInSeconds) =>
        expiresInSeconds is > 0 ? _time.GetUtcNow().AddSeconds(expiresInSeconds.Value) : null;
}
