using Bale.Core;
using Bale.Core.Browse;
using Bale.Core.Settings;
using Windows.Storage;

namespace Bale.Xbox.Services;

/// <summary>TvHomeSectionPreferences.kt: device-local choices, never profile-wide server overrides.</summary>
internal static class HomeSectionSettings
{
    private static SiloClient? _cachedClient;
    private static string? _cachedProfile;
    private static IReadOnlyList<MediaRow>? _cachedRows;

    public static HomeSectionLayout Load(SiloClient client)
    {
        if (client.ProfileId is not { } profileId) return new();
        try
        {
            return HomeSectionLayout.Deserialize(ApplicationData.Current.LocalSettings.Values[
                HomeSectionLayout.StorageKey(client.Server, profileId)] as string);
        }
        catch (Exception error)
        {
            Log.Error("Read Home section preferences", error);
            return new();
        }
    }

    // Only the editor's explicit toggle/move handlers call this. No network writes, on open or save.
    public static bool Save(SiloClient client, string profileId, HomeSectionLayout layout)
    {
        if (!ReferenceEquals(client, Session.Client) || client.ProfileId != profileId) return false;
        try
        {
            ApplicationData.Current.LocalSettings.Values[HomeSectionLayout.StorageKey(client.Server, profileId)] = layout.Serialize();
            return true;
        }
        catch (Exception error)
        {
            Log.Error("Save Home section preferences", error);
            return false;
        }
    }

    // TvHomeSectionsEditor.kt shares the populated Home result (including hidden rows), not another
    // read just to open the editor. Client reference AND profile guard against stale session reads.
    public static void Remember(SiloClient client, string? profileId, IReadOnlyList<MediaRow> rows)
    {
        if (!ReferenceEquals(client, Session.Client) || client.ProfileId != profileId) return;
        _cachedClient = client;
        _cachedProfile = profileId;
        _cachedRows = rows.Where(row => row.Cards.Count > 0).ToList();
    }

    public static bool TryGetRows(SiloClient client, out IReadOnlyList<MediaRow> rows)
    {
        rows = [];
        if (!ReferenceEquals(client, _cachedClient) || client.ProfileId != _cachedProfile || _cachedRows is null) return false;
        rows = _cachedRows;
        return true;
    }
}
