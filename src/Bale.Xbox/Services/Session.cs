using Bale.Core;
using Bale.Core.Auth;
using Bale.Core.Connection;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Services;

/// <summary>The one signed-in Silo session the app works with, shared by every page.</summary>
internal static class Session
{
    private static Frame? _rootFrame;
    private static bool _routingExpiry;

    public static SiloClient? Client { get; private set; }

    /// <summary>Shown on the top bar's profile button.</summary>
    public static string? ProfileName { get; set; }

    /// <summary>The acting profile's picture, when it has one.</summary>
    public static Uri? ProfileAvatar { get; set; }

    public static void Attach(Frame frame) => _rootFrame = frame;

    public static SiloClient Connect(Uri server)
    {
        Client?.Dispose();
        var client = Client = new SiloClient(server, DeviceIdentity.Current());
        _routingExpiry = false;
        client.TokensRefreshed += (_, _) =>
        {
            if (ReferenceEquals(Client, client))
            {
                Persist();
            }
        };
        client.SessionExpired += (_, _) =>
        {
            // Refresh runs off the UI thread. Capture the root frame, not Window.Current there.
            if (_rootFrame is { } frame)
            {
                _ = frame.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        if (ReferenceEquals(Client, client))
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
        };
        return client;
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
        if (Client is not { RefreshToken: { } refreshToken } client)
        {
            return;
        }

        _routingExpiry = false;
        var server = ServerAddress.ToBaseUrl(client.Server);
        AppSettings.ServerUrl = server;
        AppSettings.SaveRefreshToken(server, refreshToken);
        AppSettings.SaveAccessToken(server, client.AccessToken);
        AppSettings.AccessTokenExpiresAt = client.AccessTokenExpiresAt;
        AppSettings.ProfileId = client.ProfileId;
        AppSettings.SaveProfileToken(server, client.ProfileToken);
        AppSettings.ProfileName = ProfileName;
        AppSettings.ProfileAvatar = ProfileAvatar?.AbsoluteUri;
        if (client.Account?.Username is { } accountName)
        {
            AppSettings.AccountName = accountName;
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

    /// <summary>Central refresh/Home-401 recovery: keep the server, but never Back into a dead session.</summary>
    public static void Expire()
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

    public static async Task SignOutAsync()
    {
        var client = Client;
        Client = null; // A late refresh must not write a signed-out session back to the vault.
        if (client is not null)
        {
            AppSettings.ForgetTokens(ServerAddress.ToBaseUrl(client.Server));
        }

        ClearProfile();
        AppSettings.AccountName = null;
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
