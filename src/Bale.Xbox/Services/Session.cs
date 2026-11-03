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
            Reachability = new ServerReachabilityMonitor(server);
            if (_foreground) Reachability.StartForeground();
            _routingExpiry = false;
            client.TokensRefreshed += (_, _) =>
            {
                lock (StateGate)
                {
                    // A late refresh from the old server can never reactivate it or overwrite the new scope.
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
            Log.Info("Server selection committed; saved credentials retained");
        }
    }

    public static void SwitchServer(string id)
    {
        lock (StateGate)
        {
            Persist();
            AppSettings.Servers.Activate(id);
            Disconnect();
            Log.Info("Saved server switched; resolving saved session");
        }
    }

    public static bool RemoveServer(string id)
    {
        lock (StateGate)
        {
            var entry = AppSettings.Servers.Snapshot.Entries.First(s => s.Id == id);
            var wasActive = AppSettings.Servers.Active?.Id == id;
            // Persist the removal before destroying credentials; a failed local write leaves the session usable.
            AppSettings.Servers.Remove(id);
            if (wasActive) Disconnect();
            AppSettings.ForgetTokens(entry.CredentialKey);
            Log.Info($"Saved server removed active={wasActive}");
            return wasActive;
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

    /// <summary>Restores credentials locally, without making the TV wait for a refresh (MainTvActivity.kt).</summary>
    public static void Restore(Uri server, string refreshToken)
    {
        var url = ServerAddress.ToBaseUrl(server);
        var id = ProfileChoice.AutomaticId(AppSettings.ProfileId, AppSettings.OpenProfileAutomatically);
        Connect(server).RestoreSession(refreshToken, AppSettings.LoadAccessToken(url), AppSettings.AccessTokenExpiresAt,
            id, id is null ? null : AppSettings.LoadProfileToken(url));
        ProfileName = id is null ? null : AppSettings.ProfileName;
        ProfileAvatar = id is not null && Uri.TryCreate(AppSettings.ProfileAvatar, UriKind.Absolute, out var avatar) ? avatar : null;
    }

    /// <summary>Saves resumable credentials, including secure PIN proof for an explicitly automatic profile.</summary>
    public static void Persist()
    {
        lock (StateGate)
        {
            if (Client is not { RefreshToken: { } refreshToken } client) return;
            var server = ServerAddress.ToBaseUrl(client.Server);
            if (AppSettings.Servers.Active?.Url != server) return;
            _routingExpiry = false;
            AppSettings.SaveRefreshToken(server, refreshToken);
            AppSettings.SaveAccessToken(server, client.AccessToken);
            AppSettings.SaveProfileToken(server, client.ProfileToken);
            AppSettings.Servers.UpdateActive(s => s with
            {
                AccessTokenExpiresAt = client.AccessTokenExpiresAt,
                // The picker has no acting profile; switching there must retain the last saved pick.
                ProfileId = client.ProfileId ?? s.ProfileId,
                ProfileName = client.ProfileId is null ? s.ProfileName : ProfileName,
                ProfileAvatar = client.ProfileId is null ? s.ProfileAvatar : ProfileAvatar?.AbsoluteUri,
                AccountName = client.Account?.Username ?? s.AccountName,
            });
        }
    }

    /// <summary>Local launch policy: default to the picker, even offline; only an explicit Yes opens Home.</summary>
    public static void ContinueToHome(Frame frame)
    {
        var client = Client!;
        var id = ProfileChoice.AutomaticId(AppSettings.ProfileId, AppSettings.OpenProfileAutomatically);
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
    public static void ShowProfilePicker(Frame frame)
    {
        Client?.UseProfile(null);
        if (Client is { } client)
        {
            AppSettings.SaveProfileToken(ServerAddress.ToBaseUrl(client.Server), null);
        }

        ClearProfile();
        frame.Navigate(typeof(ProfilesPage));
        frame.BackStack.Clear();
    }

    /// <summary>TvAppNavigation.kt: a rejected refresh on any route keeps the server, never a dead Back entry.</summary>
    private static void Expire()
    {
        if (_routingExpiry || Client is not { } client || _rootFrame is not { } frame)
        {
            return;
        }

        _routingExpiry = true;
        var server = ServerAddress.ToBaseUrl(client.Server);
        AppSettings.ServerUrl = server;
        AppSettings.ForgetTokens(server);
        client.ForgetSession();
        ClearProfile();
        AppSettings.AccountName = null;
        // TvAppNavigation.kt sessionExpired: Login on the same server with the explanatory notice.
        frame.Navigate(typeof(MainPage), MainPage.SessionExpired);
        frame.BackStack.Clear();
    }

    public static async Task SignOutAsync(bool removeServer = false)
    {
        SiloClient? client;
        lock (StateGate)
        {
            var entry = AppSettings.Servers.Active;
            // TvServerResolver.kt: durable removal first. Failure leaves the old session usable.
            if (removeServer && entry is not null) AppSettings.Servers.Remove(entry.Id);
            else AppSettings.Servers.UpdateActive(s => s with
            {
                ProfileId = null, ProfileName = null, ProfileAvatar = null, AccountName = null, AccessTokenExpiresAt = null,
            });
            Reachability?.Dispose();
            Reachability = null;
            client = Client;
            Client = null; // A late refresh must not write a signed-out session back to the vault.
            if (entry is not null) AppSettings.ForgetTokens(entry.CredentialKey);
            ProfileName = null;
            ProfileAvatar = null;
            Log.Info($"Sign out committed removeServer={removeServer}");
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
