using Bale.Core.Api;
using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Bale.Core.Connection;
using Bale.Core.Detail;
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
    private bool _sessionExpired;

    public SiloClient(Uri server, ClientIdentity identity, HttpMessageHandler? transport = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _connection = new SiloConnection(server, identity, CurrentCredentialsAsync, transport);
        DeviceLogin = new DeviceLoginFlow(_connection.Api, time);
        Browse = new BrowseFeed(_connection.Api, server);
        LibraryBrowse = new LibraryBrowseFeed(_connection.Api, Browse);
        Detail = new DetailFeed(_connection.Api, Browse);
        People = new People.PersonFeed(_connection.Api, Browse);
    }

    public Uri Server => _connection.Server;

    public SiloApiClient Api => _connection.Api;

    public DeviceLoginFlow DeviceLogin { get; }

    public BrowseFeed Browse { get; }

    public LibraryBrowseFeed LibraryBrowse { get; }

    public DetailFeed Detail { get; }

    public People.PersonFeed People { get; }

    public string? AccessToken { get; private set; }

    /// <summary>Persist this to resume the session on next launch. It rotates on every refresh.</summary>
    public string? RefreshToken { get; private set; }

    public Account? Account { get; private set; }

    /// <summary>The household profile browsing and playback act as. Null lets the server pick its default.</summary>
    public string? ProfileId { get; private set; }

    /// <summary>Proof of the PIN for a locked profile. Store only in secure storage, never the PIN itself.</summary>
    public string? ProfileToken { get; private set; }

    public DateTimeOffset? AccessTokenExpiresAt => _accessTokenExpiresAt;

    public bool IsSignedIn => AccessToken is not null || RefreshToken is not null;

    /// <summary>Raised when the refresh token rotates, so the app can save the new one.</summary>
    public event EventHandler? TokensRefreshed;

    /// <summary>A refresh returned 401. Tokens are already cleared; keep the server and ask for sign-in.</summary>
    public event EventHandler? SessionExpired;

    /// <summary>
    /// Restores local credentials without a network request (MainTvActivity.kt). Even a legacy install
    /// with only a refresh token enters the picker/shell; its first API request performs the refresh.
    /// </summary>
    public void RestoreSession(string refreshToken, string? accessToken = null, DateTimeOffset? accessTokenExpiresAt = null,
        string? profileId = null, string? profileToken = null)
    {
        _sessionExpired = false;
        RefreshToken = refreshToken;
        AccessToken = accessToken;
        _accessTokenExpiresAt = accessTokenExpiresAt ?? _time.GetUtcNow();
        Account = null;
        ProfileId = profileId;
        ProfileToken = profileId is null ? null : profileToken;
    }

    /// <summary>Adopts the tokens from an approved device login or a password login.</summary>
    public void SignIn(TokenPair tokens, string? profileId = null, string? profileToken = null)
    {
        _sessionExpired = false;
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
        catch (ApiException e) when (e.ResponseStatusCode == 401)
        {
            ExpireSession();
            return false;
        }

        if (refreshed?.AccessToken is null)
        {
            throw new InvalidOperationException("The server returned an empty refresh response.");
        }

        _sessionExpired = false;
        AdoptRefreshed(refreshed, refreshToken);
        await GetAccountAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The account name shown under Who's watching; loading it isn't a startup gate.</summary>
    public async Task<Account?> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        Account = await Api.Api.V2.Account.Me.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return Account;
    }

    /// <summary>The household profiles on this account, for a "Who's watching?" screen.</summary>
    public async Task<IReadOnlyList<Profile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await Api.Api.V2.Profiles.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return profiles?.Items ?? [];
    }

    /// <summary>Acts as this profile; null clears selection. New locked picks need <see cref="VerifyPinAsync"/>.</summary>
    public void UseProfile(string? profileId)
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

        // TvProfileSelectionViewModel.kt drops a late answer after Cancel or another profile pick.
        cancellationToken.ThrowIfCancellationRequested();
        ProfileId = profileId;
        ProfileToken = verification.ProfileToken;
        return true;
    }

    /// <summary>Ends the session on the server (best effort) and forgets it locally.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (IsSignedIn)
            {
                await Api.Api.V2.Auth.Logout.PostAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or OperationCanceledException)
        {
            // Offline and already revoked sessions must still sign out locally.
        }
        finally
        {
            ForgetSession();
        }
    }

    /// <summary>Forgets credentials locally without a logout request, including after session expiry.</summary>
    public void ForgetSession()
    {
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
        ThrowIfSessionExpired();
        if (NeedsRefresh())
        {
            await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
        }

        ThrowIfSessionExpired();
        return AccessToken is null ? null : new RequestCredentials(AccessToken, ProfileId, ProfileToken);
    }

    private bool NeedsRefresh() =>
        RefreshToken is not null && (AccessToken is null
            || _accessTokenExpiresAt is { } expiresAt && _time.GetUtcNow() >= expiresAt - RefreshMargin);

    private void ThrowIfSessionExpired()
    {
        if (_sessionExpired)
        {
            throw new ApiException("The session expired. Sign in again.") { ResponseStatusCode = 401 };
        }
    }

    private void ExpireSession()
    {
        if (_sessionExpired)
        {
            return;
        }

        // TvAppNavigation.kt listens only for a real refresh 401, not offline/5xx failures.
        _sessionExpired = true;
        ForgetSession();
        SessionExpired?.Invoke(this, EventArgs.Empty);
    }

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
                if (refreshed?.AccessToken is null)
                {
                    throw new InvalidOperationException("The server returned an empty refresh response.");
                }

                AdoptRefreshed(refreshed, current);
            }
            catch (ApiException e) when (e.ResponseStatusCode == 401)
            {
                ExpireSession();
                throw;
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
