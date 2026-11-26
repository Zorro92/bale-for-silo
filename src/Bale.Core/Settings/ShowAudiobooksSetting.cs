using System.Security.Cryptography;
using System.Text;
using Bale.Core.Connection;

namespace Bale.Core.Settings;

/// <summary>
/// Settings › General › Top Menu › Show Audiobooks (TvSettingsScreen.kt:1124-1137; the settings manifest's client_local
/// <c>nav.show_audiobooks</c>, default off): whether the top menu, Search and a person's page offer audiobooks when the
/// server has an audiobook library (TvMainShell.kt:327-341, TvSearchViewModel.kt, TvPersonDetailViewModel.kt). Android TV
/// keeps it on the device per account, profile and server (TvLibraryScopeStore.kt show_audiobooks_{serverId}) and never
/// syncs it; Bale keeps it in this Xbox's local settings per server, account and profile.
/// </summary>
public static class ShowAudiobooksSetting
{
    /// <summary>The key in contracts/settings/v1/manifest.json (persistence client_local).</summary>
    public const string ManifestKey = "nav.show_audiobooks";

    /// <summary>The manifest's default_value: the Audiobooks tab is hidden until the viewer turns it on.</summary>
    public const bool Default = false;

    /// <summary>
    /// The local settings key for a server and a profile scope (the profile id, with its account for every account but
    /// the one migrated from the one-account layout). Length-prefixed so no two server/profile pairs share a key, and
    /// hashed so the key names no address or identity, as HomeSectionLayout.StorageKey does.
    /// </summary>
    public static string StorageKey(Uri server, string profileScope)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileScope);
        var url = ServerAddress.ToBaseUrl(server);
        var identity = $"{url.Length}:{url}{profileScope.Length}:{profileScope}";
        return ManifestKey + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    /// <summary>A stored value as the setting: only a stored true shows audiobooks; nothing stored, or anything else, is off.</summary>
    public static bool FromStored(object? value) => value is true;
}
