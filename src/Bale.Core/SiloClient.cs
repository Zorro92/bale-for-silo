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
    private readonly Func<SiloClient, Task>? _onSessionExpired;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTimeOffset? _accessTokenExpiresAt;
    private TaskCompletionSource? _sessionExpiry;
    private long _accountGeneration;
    private long _playbackProfileGeneration;

    /// <param name="onSessionExpired">Finish root sign-in navigation before failed requests reach page error handlers.</param>
    public SiloClient(Uri server, ClientIdentity identity, HttpMessageHandler? transport = null, TimeProvider? time = null,
        Func<SiloClient, Task>? onSessionExpired = null)
    {
        _time = time ?? TimeProvider.System;
        _onSessionExpired = onSessionExpired;
        _connection = new SiloConnection(server, identity, CurrentCredentialsAsync, transport, RefreshRejectedCredentialsAsync);
        DeviceLogin = new DeviceLoginFlow(_connection.Api, time);
        Browse = new BrowseFeed(_connection.Api, server);
        LibraryBrowse = new LibraryBrowseFeed(_connection.Api, Browse);
        Detail = new DetailFeed(_connection.Api, Browse);
        People = new People.PersonFeed(_connection.Api, Browse);
        Requests = new Requests.RequestsFeed(_connection.Api, _time);
        Playback = new Playback.PlaybackClient(_connection.Api, _connection.RequestAdapter, server, CapturePlaybackScope);
        Profiles = new Profiles.ProfileRepository(_connection.Api, () => Interlocked.Read(ref _accountGeneration), id =>
        {
            if (ProfileId == id) UseProfile(null);
        });
    }

    public Uri Server => _connection.Server;

    public SiloApiClient Api => _connection.Api;

    public DeviceLoginFlow DeviceLogin { get; }

    public BrowseFeed Browse { get; }

    public LibraryBrowseFeed LibraryBrowse { get; }

    public DetailFeed Detail { get; }

    public People.PersonFeed People { get; }

    public Requests.RequestsFeed Requests { get; }

    public Playback.PlaybackClient Playback { get; }

    public Profiles.ProfileRepository Profiles { get; }

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

    /// <summary>A refresh rejected the credentials (400/401/403). Tokens are already cleared; keep the server.</summary>
    public event EventHandler? SessionExpired;

    /// <summary>
    /// Restores local credentials without a network request (MainTvActivity.kt). Even a legacy install
    /// with only a refresh token enters the picker/shell; its first API request performs the refresh.
    /// </summary>
    public void RestoreSession(string refreshToken, string? accessToken = null, DateTimeOffset? accessTokenExpiresAt = null,
        string? profileId = null, string? profileToken = null)
    {
        _sessionExpiry = null;
        Interlocked.Increment(ref _accountGeneration);
        Profiles.Management.End();
        Requests.Reset();
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
        _sessionExpiry = null;
        Interlocked.Increment(ref _accountGeneration);
        Profiles.Management.End();
        Requests.Reset();
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
        catch (ApiException e) when (RejectsRefresh(e))
        {
            await ExpireSessionAsync().ConfigureAwait(false);
            return false;
        }

        if (refreshed?.AccessToken is null)
        {
            throw new InvalidOperationException("The server returned an empty refresh response.");
        }

        _sessionExpiry = null;
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
        return await Profiles.ListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Acts as this profile; null clears selection. New locked picks need <see cref="VerifyPinAsync"/>.</summary>
    public void UseProfile(string? profileId)
    {
        Interlocked.Increment(ref _playbackProfileGeneration);
        Requests.Reset();
        ProfileId = profileId;
        ProfileToken = null;
    }

    /// <summary>Checks a locked profile's PIN and, when it's right, acts as that profile.</summary>
    public async Task<bool> VerifyPinAsync(string profileId, string pin, CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Read(ref _accountGeneration);
        string? proof;
        try
        {
            proof = await Profiles.VerifyPinAsync(profileId, pin, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.ResponseStatusCode is 401 or 403)
        {
            return false;
        }

        if (proof is null || generation != Interlocked.Read(ref _accountGeneration))
        {
            return false;
        }

        // TvProfileSelectionViewModel.kt drops a late answer after Cancel or another profile pick.
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _playbackProfileGeneration);
        Requests.Reset();
        ProfileId = profileId;
        ProfileToken = proof;
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
        Interlocked.Increment(ref _accountGeneration);
        Profiles.Management.End();
        Requests.Reset();
        AccessToken = null;
        RefreshToken = null;
        _accessTokenExpiresAt = null;
        Account = null;
        ProfileId = null;
        ProfileToken = null;
    }

    public void Dispose()
    {
        Profiles.Management.End();
        _connection.Dispose();
        _refreshLock.Dispose();
    }

    private Playback.PlaybackRequestScope CapturePlaybackScope()
    {
        if (!IsSignedIn || string.IsNullOrWhiteSpace(ProfileId))
            throw new InvalidOperationException("Playback needs a signed-in profile.");
        var generation = Interlocked.Read(ref _accountGeneration);
        var profileGeneration = Interlocked.Read(ref _playbackProfileGeneration);
        var profileId = ProfileId;
        // repository/SequencedPlayback.kt fences every playback mutation after a viewer/login change.
        return new(Account?.Id, profileId, new ProfileRequestOption(profileId, ProfileToken,
            () => generation == Interlocked.Read(ref _accountGeneration)
                && profileGeneration == Interlocked.Read(ref _playbackProfileGeneration) && ProfileId == profileId && IsSignedIn));
    }

    private async Task<RequestCredentials?> CurrentCredentialsAsync(CancellationToken cancellationToken)
    {
        await ThrowIfSessionExpiredAsync().ConfigureAwait(false);
        if (NeedsRefresh())
        {
            await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
        }

        await ThrowIfSessionExpiredAsync().ConfigureAwait(false);
        return AccessToken is null ? null : new RequestCredentials(AccessToken, ProfileId, ProfileToken);
    }

    private async Task<RequestCredentials?> RefreshRejectedCredentialsAsync(string rejectedAccessToken, CancellationToken cancellationToken)
    {
        // AuthInterceptorImpl.kt double-checks inside its refresh mutex: concurrent 401s reuse
        // an already-rotated token, and a late response after sign-out cannot restore credentials.
        await RefreshNowAsync(cancellationToken, rejectedAccessToken).ConfigureAwait(false);
        await ThrowIfSessionExpiredAsync().ConfigureAwait(false);
        return AccessToken is null ? null : new RequestCredentials(AccessToken, ProfileId, ProfileToken);
    }

    private bool NeedsRefresh() =>
        RefreshToken is not null && (AccessToken is null
            || _accessTokenExpiresAt is { } expiresAt && _time.GetUtcNow() >= expiresAt - RefreshMargin);

    private async Task ThrowIfSessionExpiredAsync()
    {
        if (Volatile.Read(ref _sessionExpiry) is { } expiry)
        {
            // TvAppNavigation.kt owns expiry outside every page. Even new requests must wait
            // for that root navigation, rather than race it with a page's own error state.
            await expiry.Task.ConfigureAwait(false);
            throw new ApiException("The session expired. Sign in again.") { ResponseStatusCode = 401 };
        }
    }

    private async Task ExpireSessionAsync()
    {
        var expiry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _sessionExpiry, expiry, null) is { } existing)
        {
            await existing.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            ForgetSession();
            SessionExpired?.Invoke(this, EventArgs.Empty);
            if (_onSessionExpired is not null)
            {
                await _onSessionExpired(this).ConfigureAwait(false);
            }
        }
        finally
        {
            expiry.TrySetResult();
        }
    }

    // AuthInterceptorImpl.kt shouldInvalidateSessionAfterRefreshFailure: only auth rejection
    // (including invalid/forbidden refresh tokens), never an outage, proves the session is dead.
    private static bool RejectsRefresh(ApiException error) => error.ResponseStatusCode is 400 or 401 or 403;

    /// <summary>One refresh at a time; requests that queue behind it reuse its result.</summary>
    private async Task RefreshNowAsync(CancellationToken cancellationToken, string? rejectedAccessToken = null)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ThrowIfSessionExpiredAsync().ConfigureAwait(false);
            if (!NeedsRefresh() && (rejectedAccessToken is null || AccessToken != rejectedAccessToken || RefreshToken is null))
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

                // AuthInterceptorImpl.kt: a sign-out/new sign-in while refresh was in flight owns
                // the credentials now; never resurrect the old session or overwrite its replacement.
                if (RefreshToken == current) AdoptRefreshed(refreshed, current);
            }
            catch (ApiException e) when (RejectsRefresh(e))
            {
                if (RefreshToken == current) await ExpireSessionAsync().ConfigureAwait(false);
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
