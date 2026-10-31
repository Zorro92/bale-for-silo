using Windows.Security.Credentials;
using Windows.Storage;

namespace Bale.Xbox.Services;

/// <summary>Per-install state. Tokens and the profile's PIN proof live in the credential vault.</summary>
internal static class AppSettings
{
    private const string VaultResource = "Bale.Silo";
    private const string AccessResource = "Bale.Silo.Access";
    private const string ProfileResource = "Bale.Silo.Profile";

    private static ApplicationDataContainer Local => ApplicationData.Current.LocalSettings;

    /// <summary>Generated on first launch and kept for the life of the install.</summary>
    public static string DeviceId
    {
        get
        {
            if (Local.Values[nameof(DeviceId)] is string id)
            {
                return id;
            }

            id = Guid.NewGuid().ToString("N");
            Local.Values[nameof(DeviceId)] = id;
            return id;
        }
    }

    public static string? ServerUrl
    {
        get => Local.Values[nameof(ServerUrl)] as string;
        set => Local.Values[nameof(ServerUrl)] = value;
    }

    /// <summary>The last pick, not permission to open it automatically (ProfileLaunchPreferences.kt).</summary>
    public static string? ProfileId
    {
        get => Local.Values[nameof(ProfileId)] as string;
        set => Local.Values[nameof(ProfileId)] = value;
    }

    public static string? ProfileName
    {
        get => Local.Values[nameof(ProfileName)] as string;
        set => Local.Values[nameof(ProfileName)] = value;
    }

    public static string? ProfileAvatar
    {
        get => Local.Values[nameof(ProfileAvatar)] as string;
        set => Local.Values[nameof(ProfileAvatar)] = value;
    }

    public static string? AccountName
    {
        get => Local.Values[nameof(AccountName)] as string;
        set => Local.Values[nameof(AccountName)] = value;
    }

    // The device-wide choice survives sign-out, as in ProfileLaunchPreferences.kt; a new TV asks.
    public static bool OpenProfileAutomatically
    {
        get => Local.Values[nameof(OpenProfileAutomatically)] is true;
        set => Local.Values[nameof(OpenProfileAutomatically)] = value;
    }

    public static bool ProfileRememberOfferAnswered
    {
        get => Local.Values[nameof(ProfileRememberOfferAnswered)] is true;
        set => Local.Values[nameof(ProfileRememberOfferAnswered)] = value;
    }

    public static DateTimeOffset? AccessTokenExpiresAt
    {
        get => Local.Values[nameof(AccessTokenExpiresAt)] is long seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        set => Local.Values[nameof(AccessTokenExpiresAt)] = value?.ToUnixTimeSeconds();
    }

    /// <summary>The library a multi-library tab is scoped to, per profile and tab (Skyline §8: persisted).</summary>
    public static string? GetLibraryScope(string? profileId, string tab) =>
        Local.Values[$"Scope.{profileId}.{tab}"] as string;

    public static void SetLibraryScope(string? profileId, string tab, string libraryId) =>
        Local.Values[$"Scope.{profileId}.{tab}"] = libraryId;

    /// <summary>CalendarViewModel remembers the Following / Trending / All view across launches.</summary>
    public static string? CalendarFilter
    {
        get => Local.Values["calendar.filter"] as string;
        set => Local.Values["calendar.filter"] = value;
    }

    public static string? LoadRefreshToken(string server) => LoadCredential(VaultResource, server);

    public static void SaveRefreshToken(string server, string refreshToken) => SaveCredential(VaultResource, server, refreshToken);

    public static string? LoadAccessToken(string server) => LoadCredential(AccessResource, server);

    public static void SaveAccessToken(string server, string? accessToken) => SaveCredential(AccessResource, server, accessToken);

    public static string? LoadProfileToken(string server) => LoadCredential(ProfileResource, server);

    public static void SaveProfileToken(string server, string? profileToken) => SaveCredential(ProfileResource, server, profileToken);

    public static void ForgetTokens(string server)
    {
        ForgetCredential(VaultResource, server);
        ForgetCredential(AccessResource, server);
        ForgetCredential(ProfileResource, server);
        AccessTokenExpiresAt = null;
    }

    private static string? LoadCredential(string resource, string server)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(resource, server);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception)
        {
            // Retrieve throws when nothing is stored.
            return null;
        }
    }

    private static void SaveCredential(string resource, string server, string? token)
    {
        ForgetCredential(resource, server);
        if (!string.IsNullOrEmpty(token))
        {
            new PasswordVault().Add(new PasswordCredential(resource, server, token));
        }
    }

    private static void ForgetCredential(string resource, string server)
    {
        var vault = new PasswordVault();
        try
        {
            vault.Remove(vault.Retrieve(resource, server));
        }
        catch (Exception)
        {
            // Nothing stored.
        }
    }
}
