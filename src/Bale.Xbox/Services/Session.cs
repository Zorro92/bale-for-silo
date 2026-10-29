using Bale.Core;
using Bale.Core.Auth;
using Bale.Core.Connection;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Services;

/// <summary>The one signed-in Silo session the app works with, shared by every page.</summary>
internal static class Session
{
    public static SiloClient? Client { get; private set; }

    /// <summary>Shown on Home's profile button.</summary>
    public static string? ProfileName { get; set; }

    public static SiloClient Connect(Uri server)
    {
        Client?.Dispose();
        Client = new SiloClient(server, DeviceIdentity.Current());
        Client.TokensRefreshed += (_, _) => Persist();
        return Client;
    }

    /// <summary>Saves what's needed to resume on next launch. Call after sign-in and after every token refresh.</summary>
    public static void Persist()
    {
        if (Client is { RefreshToken: { } refreshToken })
        {
            var server = ServerAddress.ToBaseUrl(Client.Server);
            AppSettings.ServerUrl = server;
            AppSettings.SaveRefreshToken(server, refreshToken);
        }

        AppSettings.ProfileId = Client?.ProfileId;
    }

    /// <summary>
    /// After sign-in or resume: straight to Home when the profile is settled (approved on the phone,
    /// remembered, or the only one), otherwise "Who's watching?".
    /// </summary>
    public static async Task ContinueToHomeAsync(Frame frame)
    {
        var client = Client!;
        var profiles = await client.GetProfilesAsync();

        var approved = client.ProfileToken is not null ? profiles.FirstOrDefault(p => p.Id == client.ProfileId) : null;
        var profile = approved ?? ProfileChoice.Automatic(profiles, client.ProfileId ?? AppSettings.ProfileId);
        if (profile is null)
        {
            frame.Navigate(typeof(ProfilesPage));
        }
        else
        {
            if (approved is null)
            {
                client.UseProfile(profile.Id!);
            }

            ProfileName = profile.Name;
            Persist();
            frame.Navigate(typeof(HomePage));
        }

        frame.BackStack.Clear();
    }

    public static async Task SignOutAsync()
    {
        if (Client is null)
        {
            return;
        }

        AppSettings.ForgetRefreshToken(ServerAddress.ToBaseUrl(Client.Server));
        AppSettings.ProfileId = null;
        ProfileName = null;
        await Client.SignOutAsync();
    }
}
