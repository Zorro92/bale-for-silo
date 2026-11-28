using Bale.Core.Auth;
using Bale.Core.Connection;
using Bale.Core.Settings;
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

    // The open account's own login, so two accounts on one server never share playback recovery.
    private static string PlaybackLoginKey => "PlaybackLogin." + (Servers.ActiveAccount?.ServerScope
        ?? throw new InvalidOperationException("Playback needs a saved account."));

    private static readonly Lazy<SavedServerRegistry> ServerRegistry = new(LoadServers);
    public static SavedServerRegistry Servers => ServerRegistry.Value;

    private static SavedServerRegistry LoadServers()
    {
        // AndroidServerRegistry.kt migrates synchronously before route resolution, offline (multi-account spec §5.2).
        // The one-account list and the LocalSettings session before it are only read; each server with a vault
        // session becomes one account that keeps its EXACT vault usernames, so nobody has to sign in again.
        var folder = ApplicationData.Current.LocalFolder.Path;
        var legacy = new LegacyServerSession(Local.Values[nameof(ServerUrl)] as string,
            Local.Values[$"ServerName.{Local.Values[nameof(ServerUrl)] as string}"] as string,
            Local.Values[nameof(ProfileId)] as string, Local.Values[nameof(ProfileName)] as string,
            Local.Values[nameof(ProfileAvatar)] as string, Local.Values[nameof(AccountName)] as string,
            Local.Values[nameof(AccessTokenExpiresAt)] is long seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
        var migration = new SavedServerMigration(new FileSavedServerStorage(Path.Combine(folder, "servers.v1.json")).Read(), legacy,
            key => HasCredential(VaultResource, key));
        var registry = new SavedServerRegistry(new FileSavedServerStorage(Path.Combine(folder, "servers.v2.json")), migration);
        var pruned = registry.RemoveAccountlessServers();
        var state = registry.Snapshot;
        Log.Info($"Server registry loaded servers={state.Entries.Length} accounts={state.Accounts.Length} pruned={pruned}");
        return registry;
    }

    public static string? ServerUrl => Servers.Active?.Url;

    /// <summary>The open account's last pick, not permission to open it automatically (ProfileLaunchPreferences.kt).</summary>
    public static string? ProfileId
    {
        get => Servers.ActiveAccount?.ProfileId;
        set => Servers.UpdateActiveAccount(a => a with { ProfileId = value });
    }

    public static string? ProfileName
    {
        get => Servers.ActiveAccount?.ProfileName;
        set => Servers.UpdateActiveAccount(a => a with { ProfileName = value });
    }

    public static string? ProfileAvatar
    {
        get => Servers.ActiveAccount?.ProfileAvatar;
        set => Servers.UpdateActiveAccount(a => a with { ProfileAvatar = value });
    }

    public static string? AccountName
    {
        get => Servers.ActiveAccount?.Username;
        set => Servers.UpdateActiveAccount(a => a with { Username = string.IsNullOrWhiteSpace(value) ? a.Username : value });
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

    /// <summary>
    /// Device-wide SDR Brightness: the SDR white (int nits) the console tone map maps HDR to (<see cref="SdrBrightness"/>).
    /// Default is stored as no value, so it keeps following Bale's default; an unreadable, unknown or wrongly typed value
    /// reads as Default, never as another level. A player attempt reads it once, when it starts.
    /// </summary>
    public static int SdrWhiteNits
    {
        get
        {
            try { return SdrBrightness.FromStored(Local.Values[nameof(SdrWhiteNits)] as int?); }
            catch (Exception) { return SdrBrightness.DefaultNits; }
        }
        set
        {
            var nits = SdrBrightness.FromStored(value);
            if (nits == SdrBrightness.DefaultNits) Local.Values.Remove(nameof(SdrWhiteNits));
            else Local.Values[nameof(SdrWhiteNits)] = nits;
        }
    }

    public static DateTimeOffset? AccessTokenExpiresAt
    {
        get => Servers.ActiveAccount?.AccessTokenExpiresAt;
        set => Servers.UpdateActiveAccount(a => a with { AccessTokenExpiresAt = value });
    }

    /// <summary>
    /// The key a device-local per-profile preference uses: the profile id, plus the account for every account but the
    /// one migrated from the one-account layout (profile ids repeat across accounts, multi-account spec §5.1).
    /// </summary>
    public static string? ProfileScope(string? profileId) =>
        profileId is null ? null : Servers.ActiveAccount is { } account ? account.ProfileScope(profileId) : profileId;

    /// <summary>The library a multi-library tab is scoped to, per server, account, profile and tab (Skyline §8).</summary>
    public static string? GetLibraryScope(string? profileId, string tab)
    {
        if (Servers.ActiveAccount is not { } account) return null;
        var key = $"ServerScope.{account.ServerScope}.{profileId}.{tab}";
        if (Local.Values[key] is string scope) return scope;
        // Only the account migrated from the first install-wide session may inherit its old scope.
        if (!account.AdoptsLegacyData || account.CredentialKey != Local.Values[nameof(ServerUrl)] as string) return null;
        if (Local.Values[$"Scope.{profileId}.{tab}"] is not string legacy) return null;
        Local.Values[key] = legacy;
        return legacy;
    }

    public static void SetLibraryScope(string? profileId, string tab, string libraryId)
    {
        if (Servers.ActiveAccount is { } account) Local.Values[$"ServerScope.{account.ServerScope}.{profileId}.{tab}"] = libraryId;
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

    // Each account has its own vault slot (its CredentialKey); a migrated account keeps the old per-server one.
    public static bool HasRefreshToken(SavedAccount account) => HasCredential(VaultResource, account.CredentialKey);

    public static string? LoadRefreshToken(SavedAccount account) => LoadCredential(VaultResource, account.CredentialKey);

    public static void SaveRefreshToken(SavedAccount account, string refreshToken) => SaveCredential(VaultResource, account.CredentialKey, refreshToken);

    public static string? LoadAccessToken(SavedAccount account) => LoadCredential(AccessResource, account.CredentialKey);

    public static void SaveAccessToken(SavedAccount account, string? accessToken) => SaveCredential(AccessResource, account.CredentialKey, accessToken);

    public static string? LoadProfileToken(SavedAccount account) => LoadCredential(ProfileResource, account.CredentialKey);

    public static void SaveProfileToken(SavedAccount account, string? profileToken) => SaveCredential(ProfileResource, account.CredentialKey, profileToken);

    public static void ForgetTokens(SavedAccount account)
    {
        ForgetCredential(VaultResource, account.CredentialKey);
        ForgetCredential(AccessResource, account.CredentialKey);
        ForgetCredential(ProfileResource, account.CredentialKey);
    }

    private static bool HasCredential(string resource, string key)
    {
        try
        {
            // Retrieve without RetrievePassword: presence only, the secret is never read here.
            return new PasswordVault().Retrieve(resource, key) is not null;
        }
        catch (Exception)
        {
            // Retrieve throws when nothing is stored.
            return false;
        }
    }

    private static string? LoadCredential(string resource, string key)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(resource, key);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception)
        {
            // Retrieve throws when nothing is stored.
            return null;
        }
    }

    private static void SaveCredential(string resource, string key, string? token)
    {
        ForgetCredential(resource, key);
        if (!string.IsNullOrEmpty(token))
        {
            new PasswordVault().Add(new PasswordCredential(resource, key, token));
        }
    }

    private static void ForgetCredential(string resource, string key)
    {
        var vault = new PasswordVault();
        try
        {
            vault.Remove(vault.Retrieve(resource, key));
        }
        catch (Exception)
        {
            // Nothing stored.
        }
    }
}
