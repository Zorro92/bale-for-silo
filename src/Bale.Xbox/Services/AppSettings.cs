using Bale.Core.Auth;
using Bale.Core.Connection;
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

    // repository/SequencedPlayback.kt binds recovery to the saved login, not rotating refresh tokens.
    public static string PlaybackLoginId
    {
        get
        {
            var key = PlaybackLoginKey;
            if (Local.Values[key] is string id) return id;
            id = Guid.NewGuid().ToString();
            Local.Values[key] = id;
            return id;
        }
    }

    public static void RenewPlaybackLogin() => Local.Values[PlaybackLoginKey] = Guid.NewGuid().ToString();

    private static string PlaybackLoginKey => "PlaybackLogin." + (Servers.Active?.Id
        ?? throw new InvalidOperationException("Playback needs a saved server."));

    private static readonly Lazy<SavedServerRegistry> ServerRegistry = new(LoadServers);
    public static SavedServerRegistry Servers => ServerRegistry.Value;

    private static SavedServerRegistry LoadServers()
    {
        // AndroidServerRegistry.kt migrates synchronously before route resolution. Keep the old vault
        // usernames untouched; metadata moves once into a durable file (LocalSettings strings are size-limited).
        var legacy = new LegacyServerSession(Local.Values[nameof(ServerUrl)] as string,
            Local.Values[$"ServerName.{Local.Values[nameof(ServerUrl)] as string}"] as string,
            Local.Values[nameof(ProfileId)] as string, Local.Values[nameof(ProfileName)] as string,
            Local.Values[nameof(ProfileAvatar)] as string, Local.Values[nameof(AccountName)] as string,
            Local.Values[nameof(AccessTokenExpiresAt)] is long seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
        var registry = new SavedServerRegistry(new FileSavedServerStorage(Path.Combine(ApplicationData.Current.LocalFolder.Path, "servers.v1.json")), legacy);
        Log.Info($"Server registry loaded count={registry.Snapshot.Entries.Length}");
        return registry;
    }

    public static string? ServerUrl
    {
        get => Servers.Active?.Url;
        set
        {
            if (value is not null && ServerAddress.TryParse(value, out var server)) Servers.AddAndActivate(server);
        }
    }

    /// <summary>The last pick, not permission to open it automatically (ProfileLaunchPreferences.kt).</summary>
    public static string? ProfileId
    {
        get => Servers.Active?.ProfileId;
        set => Servers.UpdateActive(s => s with { ProfileId = value });
    }

    public static string? ProfileName
    {
        get => Servers.Active?.ProfileName;
        set => Servers.UpdateActive(s => s with { ProfileName = value });
    }

    public static string? ProfileAvatar
    {
        get => Servers.Active?.ProfileAvatar;
        set => Servers.UpdateActive(s => s with { ProfileAvatar = value });
    }

    public static string? AccountName
    {
        get => Servers.Active?.AccountName;
        set => Servers.UpdateActive(s => s with { AccountName = value });
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
        get => Servers.Active?.AccessTokenExpiresAt;
        set => Servers.UpdateActive(s => s with { AccessTokenExpiresAt = value });
    }

    /// <summary>The library a multi-library tab is scoped to, per server, profile and tab (Skyline §8).</summary>
    public static string? GetLibraryScope(string? profileId, string tab)
    {
        if (Servers.Active is not { } server) return null;
        var key = $"ServerScope.{server.Id}.{profileId}.{tab}";
        if (Local.Values[key] is string scope) return scope;
        // Only the migrated server may inherit the old install-wide scope, never a second server.
        if (server.CredentialKey != Local.Values[nameof(ServerUrl)] as string) return null;
        if (Local.Values[$"Scope.{profileId}.{tab}"] is not string legacy) return null;
        Local.Values[key] = legacy;
        return legacy;
    }

    public static void SetLibraryScope(string? profileId, string tab, string libraryId)
    {
        if (Servers.Active is { } server) Local.Values[$"ServerScope.{server.Id}.{profileId}.{tab}"] = libraryId;
    }

    // TrackSelectionPresets.kt: a device audio-language setting overrides the active profile's language.
    // Bale has no Playback settings editor yet, so an unset value follows the profile (then English).
    public static string? AudioLanguage
    {
        get => Local.Values[nameof(AudioLanguage)] as string;
        set => Local.Values[nameof(AudioLanguage)] = value;
    }

    /// <summary>CalendarViewModel remembers the Following / Trending / All view across launches.</summary>
    public static string? CalendarFilter
    {
        get => Local.Values["calendar.filter"] as string;
        set => Local.Values["calendar.filter"] = value;
    }

    // SignInServer.kt / ServerBranding.kt cache the public name per server, including after sign-out.
    public static string? GetServerName(string server) =>
        ServerAddress.TryParse(server, out var address) && Servers.Find(address)?.FetchedName is { } name ? name : Local.Values[$"ServerName.{server}"] as string;

    public static ServerBranding GetServerBranding(Uri server)
    {
        var url = ServerAddress.ToBaseUrl(server);
        return new ServerBranding(GetServerName(url), ServerBranding.ResolveAsset(server, Local.Values[$"ServerMark.{url}"] as string));
    }

    public static void SetServerBranding(string server, ServerBranding branding)
    {
        if (branding.ServerName is { } name)
        {
            Local.Values[$"ServerName.{server}"] = name;
            if (Servers.Active?.Url == server) Servers.UpdateActive(s => s with { FetchedName = name });
        }
        Local.Values[$"ServerMark.{server}"] = branding.MarkUrl?.AbsoluteUri;
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
        if (Servers.Active is { } active && (active.Url == server || active.CredentialKey == server)) AccessTokenExpiresAt = null;
    }

    private static string CredentialKey(string server) =>
        ServerAddress.TryParse(server, out var address) ? Servers.Find(address)?.CredentialKey ?? server : server;

    private static string? LoadCredential(string resource, string server)
    {
        server = CredentialKey(server);
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
        server = CredentialKey(server);
        ForgetCredential(resource, server);
        if (!string.IsNullOrEmpty(token))
        {
            new PasswordVault().Add(new PasswordCredential(resource, server, token));
        }
    }

    private static void ForgetCredential(string resource, string server)
    {
        server = CredentialKey(server);
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
