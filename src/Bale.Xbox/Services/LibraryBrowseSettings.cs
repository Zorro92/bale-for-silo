using System.Security.Cryptography;
using System.Text;
using Bale.Core;
using Bale.Core.Browse;
using Windows.Storage;

namespace Bale.Xbox.Services;

/// <summary>BrowsePrefsStore.kt: preserve ON by default, scoped to server/profile/library/media scope.</summary>
internal static class LibraryBrowseSettings
{
    private static string Key(SiloClient client, LibraryBrowseQuery query)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{client.Server}\n{client.ProfileId}\n{query.LibraryId}\n{query.MediaType}")));
        return "LibraryBrowse." + identity;
    }

    public static bool Preserve(SiloClient client, LibraryBrowseQuery query) =>
        ApplicationData.Current.LocalSettings.Values[Key(client, query) + ".preserve"] is not false;

    public static LibraryBrowseFilter Load(SiloClient client, LibraryBrowseQuery query)
    {
        if (!Preserve(client, query)) return new();
        var json = ApplicationData.Current.LocalSettings.Values[Key(client, query)] as string;
        return LibraryBrowsePreferences.Deserialize(json, query.MediaType) ?? new();
    }

    public static void Save(SiloClient client, LibraryBrowseQuery query)
    {
        if (query.CollectionId is not null || !Preserve(client, query)) return;
        try
        {
            ApplicationData.Current.LocalSettings.Values[Key(client, query)] = LibraryBrowsePreferences.Serialize(query.Filter);
        }
        catch (Exception ex)
        {
            // A full settings store must not prevent browsing or close the input handler natively.
            Log.Error("Save library browse preferences", ex);
        }
    }

    public static void SetPreserve(SiloClient client, LibraryBrowseQuery query, bool preserve)
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        values[Key(client, query) + ".preserve"] = preserve;
        if (preserve) Save(client, query);
        else values.Remove(Key(client, query));
    }
}
