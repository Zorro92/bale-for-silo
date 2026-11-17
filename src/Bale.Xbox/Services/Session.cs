using Bale.Core;
using Bale.Core.Auth;
using Bale.Core.Connection;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Services;

/// <summary>The one signed-in Silo session the app works with, shared by every page.</summary>
internal static class Session
{
    private static readonly object StateGate = new();
    private static Frame? _rootFrame;
    private static bool _routingExpiry;
    private static bool _foreground = true;
    private static string? _clientAccountId;

    public static SiloClient? Client { get; private set; }

    public static ServerReachabilityMonitor? Reachability { get; private set; }

    /// <summary>Shown on the top bar's profile button.</summary>
    public static string? ProfileName { get; set; }

    /// <summary>The acting profile's picture, when it has one.</summary>
    public static Uri? ProfileAvatar { get; set; }

    public static void Attach(Frame frame) => _rootFrame = frame;

    /// <summary>MainTvActivity.kt starts fresh discovery only while the app is in the foreground.</summary>
    public static void SetForeground(bool foreground)
    {
        if (_foreground == foreground) return;
        _foreground = foreground;
        if (foreground) Reachability?.StartForeground();
        else Reachability?.StopForeground();
    }

    public static SiloClient Connect(Uri server)
    {
        lock (StateGate)
        {
            // TvServerResolver.kt never lets stored credentials reach an unapproved HTTP origin.
            if (AppSettings.Servers.RequiresHttpApproval(server)) throw new InvalidOperationException("HTTP approval is required.");
            Disconnect();
            var client = Client = new SiloClient(server, DeviceIdentity.Current(), onSessionExpired: RouteExpiryAsync);
            // The account this client signs requests for; null while a new account signs in.
            _clientAccountId = AppSettings.Servers.ActiveAccount?.Id;
            Reachability = new ServerReachabilityMonitor(server);
            if (_foreground) Reachability.StartForeground();
            _routingExpiry = false;
            client.TokensRefreshed += (_, _) =>
            {
                lock (StateGate)
                {
                    // A late refresh from the old server or account can never overwrite the new one's slot.
                    if (ReferenceEquals(Client, client)) Persist();
                }
            };
            return client;
        }
    }

    private static void Disconnect()
    {
        Reachability?.Dispose();
        Reachability = null;
        var old = Client;
        Client = null;
        _clientAccountId = null;
        old?.Dispose();
        ProfileName = null;
        ProfileAvatar = null;
    }

    public static void ActivateServer(Uri server, string? name = null, string? verifiedServerId = null)
    {
        lock (StateGate)
        {
            Persist();
            AppSettings.Servers.AddAndActivate(server, name, verifiedServerId);
            Disconnect();
            Log.Info($"Server selection committed; account open={AppSettings.Servers.ActiveAccount is not null}");
        }
    }

    /// <summary>
    /// TvChangeAccountViewModel.kt activate: the open account changes (null: the server with no account, where a new
    /// sign-in starts). The departing account's session is saved first and the live client is dropped.
    /// </summary>
    public static void SwitchAccount(string serverId, string? accountId)
    {
        lock (StateGate)
        {
            Persist();
            AppSettings.Servers.ActivateAccount(serverId, accountId);
            Disconnect();
            Log.Info($"Account switched; add={accountId is null}");
        }
    }

    /// <summary>
    /// A sign-in on the active server succeeded: save it as a new account, or refresh the saved account it matches
    /// (multi-account spec §5). Call after <see cref="SiloClient.SignIn"/> and before <see cref="Persist"/>.
    /// </summary>
    public static SavedAccount AdoptSignIn(SiloClient client)
    {
        lock (StateGate)
        {
            if (!ReferenceEquals(Client, client)) throw new InvalidOperationException("The sign-in client is no longer current.");
            var account = AppSettings.Servers.AdoptSignIn(client.Account?.Id, client.Account?.Username);
            _clientAccountId = account.Id;
            return account;
        }
    }

    /// <summary>Remove Server signs out of every account on it, then forgets it (TvAccountsViewModel.kt onRemoveServer).</summary>
    public static bool RemoveServer(string id)
    {
        List<(Uri Server, string Refresh, string? Access)> revoke;
        bool wasActive;
        lock (StateGate)
        {
            var entry = AppSettings.Servers.Snapshot.Entries.First(s => s.Id == id);
            wasActive = AppSettings.Servers.Active?.Id == id;
            revoke = Revocations(entry, AppSettings.Servers.AccountsOn(id));
            // Persist the removal before destroying credentials; a failed local write leaves the sessions usable.
            var removed = AppSettings.Servers.Remove(id);
            EndOpenClientIfRemoved(removed);
            if (wasActive) Disconnect();
            foreach (var account in removed) AppSettings.ForgetTokens(account);
            Log.Info($"Saved server removed active={wasActive} accounts={removed.Count}");
        }
        _ = RevokeAsync(revoke);
        return wasActive;
    }

    /// <summary>
    /// Settings › Accounts Sign Out of an account that is not open: it leaves this device (its server stays unless
    /// <paramref name="removeServer"/>), and its session ends on the server in the background, best effort.
    /// </summary>
    public static async Task SignOutAccountAsync(SavedAccount account, bool removeServer = false)
    {
        if (AppSettings.Servers.ActiveAccount?.Id == account.Id)
        {
            await SignOutAsync(removeServer);
            return;
        }
        List<(Uri Server, string Refresh, string? Access)> revoke;
        lock (StateGate)
        {
            var entry = AppSettings.Servers.Snapshot.Entries.First(s => s.Id == account.ServerId);
            revoke = Revocations(entry, [account]);
            var removed = AppSettings.Servers.RemoveAccount(account.Id, removeServer);
            EndOpenClientIfRemoved(removed);
            foreach (var gone in removed) AppSettings.ForgetTokens(gone);
            Log.Info($"Account signed out open=false removeServer={removeServer}");
        }
        await RevokeAsync(revoke);
    }

    /// <summary>The saved sessions to end on the server; the open account's live client ends its own.</summary>
    private static List<(Uri Server, string Refresh, string? Access)> Revocations(SavedServer server, IReadOnlyList<SavedAccount> accounts)
    {
        var list = new List<(Uri, string, string?)>();
        foreach (var account in accounts)
        {
            if (account.Id == _clientAccountId || account.NeedsSignIn) continue;
            if (AppSettings.LoadRefreshToken(account) is { } refresh) list.Add((new Uri(server.Url), refresh, AppSettings.LoadAccessToken(account)));
        }
        return list;
    }

    /// <summary>After a durable removal: the open account's client, if removed, ends its session and is dropped.</summary>
    private static void EndOpenClientIfRemoved(IReadOnlyList<SavedAccount> removed)
    {
        if (Client is not { } client || !removed.Any(a => a.Id == _clientAccountId)) return;
        Reachability?.Dispose();
        Reachability = null;
        Client = null; // Detached first, so no late refresh writes it back.
        _clientAccountId = null;
        ProfileName = null;
        ProfileAvatar = null;
        _ = EndClientAsync(client);
    }

    private static async Task EndClientAsync(SiloClient client)
    {
        try
        {
            await client.SignOutAsync();
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task RevokeAsync(List<(Uri Server, string Refresh, string? Access)> sessions)
    {
        foreach (var (server, refresh, access) in sessions)
        {
            // Never send a removed account's credentials to an HTTP origin the viewer has not approved.
            if (AppSettings.Servers.RequiresHttpApproval(server)) continue;
            try
            {
                using var client = new SiloClient(server, DeviceIdentity.Current());
                client.RestoreSession(refresh, access);
                await client.SignOutAsync();
            }
            catch (Exception ex)
            {
                Log.Info($"Background sign-out unavailable type={ex.GetType().Name}");
            }
        }
    }

    private static async Task RouteExpiryAsync(SiloClient client)
    {
        // TvAppNavigation.kt collects TokenManager.sessionExpired outside every page/route.
        // Await the root replacement so no failing request can publish a page error first.
        // Refresh runs off the UI thread: use the attached frame, never Window.Current there.
        if (_rootFrame is not { } frame) return;
        await frame.Dispatcher.RunAsync(CoreDispatcherPriority.High, () =>
        {
            try
            {
                if (ReferenceEquals(Client, client) && !client.IsSignedIn)
                {
                    Expire();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Session expiry navigation", ex);
            }
        });
    }

    /// <summary>Restores the open account's credentials locally, without making the TV wait for a refresh (MainTvActivity.kt).</summary>
    public static void Restore(Uri server, SavedAccount account, string refreshToken)
    {
        var id = ProfileChoice.AutomaticId(account.ProfileId, AppSettings.OpenProfileAutomatically);
        Connect(server).RestoreSession(refreshToken, AppSettings.LoadAccessToken(account), account.AccessTokenExpiresAt,
            id, id is null ? null : AppSettings.LoadProfileToken(account));
        ProfileName = id is null ? null : account.ProfileName;
        ProfileAvatar = id is not null && Uri.TryCreate(account.ProfileAvatar, UriKind.Absolute, out var avatar) ? avatar : null;
    }

    /// <summary>Saves resumable credentials to the open account's slot, including PIN proof for an automatic profile.</summary>
    public static void Persist()
    {
        lock (StateGate)
        {
            if (Client is not { RefreshToken: { } refreshToken } client) return;
            if (AppSettings.Servers.Active?.Url != ServerAddress.ToBaseUrl(client.Server)) return;
            if (AppSettings.Servers.ActiveAccount is not { } account || account.Id != _clientAccountId) return;
            _routingExpiry = false;
            AppSettings.SaveRefreshToken(account, refreshToken);
            AppSettings.SaveAccessToken(account, client.AccessToken);
            AppSettings.SaveProfileToken(account, client.ProfileToken);
            AppSettings.Servers.UpdateActiveAccount(a => a with
            {
                AccessTokenExpiresAt = client.AccessTokenExpiresAt,
                // The picker has no acting profile; switching there must retain the last saved pick.
                ProfileId = client.ProfileId ?? a.ProfileId,
                ProfileName = client.ProfileId is null ? a.ProfileName : ProfileName,
                ProfileAvatar = client.ProfileId is null ? a.ProfileAvatar : ProfileAvatar?.AbsoluteUri,
                Username = client.Account?.Username ?? a.Username,
                // The first verified /me fills the id a migrated account never stored (AccountEntry.kt).
                ServerUserId = a.ServerUserId ?? client.Account?.Id,
                NeedsSignIn = false,
            });
        }
    }

    /// <summary>The open account's profile count as last read, for Change Account and Settings › Accounts offline.</summary>
    public static void RecordProfileCount(SiloClient client, int count)
    {
        lock (StateGate)
        {
            if (!ReferenceEquals(Client, client) || AppSettings.Servers.ActiveAccount is not { } account
                || account.Id != _clientAccountId || account.ProfileCount == count) return;
            try
            {
                AppSettings.Servers.UpdateActiveAccount(a => a with { ProfileCount = count });
            }
            catch (Exception ex)
            {
                // A cached count is cosmetic; a failed write must not fail the picker or the menu.
                Log.Info($"Profile count save failed type={ex.GetType().Name}");
            }
        }
    }

    /// <summary>Local launch policy: default to the picker, even offline; only an explicit Yes opens Home.</summary>
    /// <param name="choose">An account was just chosen on Change Account: its picker waits for a choice.</param>
    public static void ContinueToHome(Frame frame, bool choose = false)
    {
        var client = Client!;
        var id = choose ? null : ProfileChoice.AutomaticId(AppSettings.ProfileId, AppSettings.OpenProfileAutomatically);
        if (id is null)
        {
            client.UseProfile(null);
            frame.Navigate(typeof(ProfilesPage));
        }
        else
        {
            // Preserve a restored protected profile's proof (ProfileLaunchPreferences.kt Automatic).
            if (client.ProfileId != id)
            {
                client.UseProfile(id);
            }

            frame.Navigate(typeof(ShellPage));
        }

        frame.BackStack.Clear();
    }

    /// <summary>Switch Profile replaces the shell, not a Back destination (TvAppNavigation.kt onSwitchProfile).</summary>
    /// <param name="add">Open the new-profile form once the picker loads (the profile menu's Add Profile).</param>
    public static void ShowProfilePicker(Frame frame, bool add = false)
    {
        Client?.UseProfile(null);
        if (Client is not null && AppSettings.Servers.ActiveAccount is { } account && account.Id == _clientAccountId)
        {
            AppSettings.SaveProfileToken(account, null);
        }

        ClearProfile();
        frame.Navigate(typeof(ProfilesPage), add && Client is { } current
            ? new ProfileNavigation(current.Profiles, current.Browse.Resolve, StartAdd: true) : null);
        frame.BackStack.Clear();
    }

    /// <summary>
    /// TvAppNavigation.kt: a rejected refresh on any route keeps the server, never a dead Back entry. Expiry is not a
    /// sign-out (AccountEntry.kt needsSignIn): the account stays saved and asks to sign in again.
    /// </summary>
    private static void Expire()
    {
        if (_routingExpiry || Client is not { } client || _rootFrame is not { } frame)
        {
            return;
        }

        _routingExpiry = true;
        if (AppSettings.Servers.ActiveAccount is { } account && account.Id == _clientAccountId)
        {
            AppSettings.Servers.MarkNeedsSignIn(account.Id);
            // The server refused these credentials; keep the account, not its dead tokens.
            AppSettings.ForgetTokens(account);
        }
        client.ForgetSession();
        ProfileName = null;
        ProfileAvatar = null;
        // TvAppNavigation.kt sessionExpired: Login on the same server with the explanatory notice.
        frame.Navigate(typeof(MainPage), MainPage.SessionExpired);
        frame.BackStack.Clear();
    }

    /// <summary>Sign Out of the open account (TvSignOutDialog.kt): its server stays unless removed with it.</summary>
    public static async Task SignOutAsync(bool removeServer = false)
    {
        SiloClient? client;
        lock (StateGate)
        {
            var entry = AppSettings.Servers.Active;
            var account = AppSettings.Servers.ActiveAccount;
            // TvServerResolver.kt: durable removal first. Failure leaves the old session usable.
            IReadOnlyList<SavedAccount> removed = account is not null ? AppSettings.Servers.RemoveAccount(account.Id, removeServer)
                : removeServer && entry is not null ? AppSettings.Servers.Remove(entry.Id) : [];
            Reachability?.Dispose();
            Reachability = null;
            client = Client;
            Client = null; // A late refresh must not write a signed-out session back to the vault.
            _clientAccountId = null;
            foreach (var gone in removed) AppSettings.ForgetTokens(gone);
            ProfileName = null;
            ProfileAvatar = null;
            Log.Info($"Sign out committed removeServer={removeServer} accounts={removed.Count}");
        }
        if (client is not null)
        {
            try
            {
                await client.SignOutAsync();
            }
            finally
            {
                client.Dispose();
            }
        }
    }

    private static void ClearProfile()
    {
        AppSettings.ProfileId = null;
        AppSettings.ProfileName = null;
        AppSettings.ProfileAvatar = null;
        ProfileName = null;
        ProfileAvatar = null;
    }
}
